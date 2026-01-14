using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Asyncs.Locks;
using Soenneker.Atomics.Longs;
using Soenneker.Blazor.Utils.Navigation.Abstract;
using Soenneker.Blazor.Utils.Session.Abstract;
using Soenneker.Extensions.String;
using Soenneker.Utils.Delay;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Blazor.Utils.Session;

/// <inheritdoc cref="ISessionUtil"/>
public sealed class SessionUtil : ISessionUtil
{
    private readonly INavigationUtil _navigationUtil;
    private readonly IAccessTokenProvider _accessTokenProvider;
    private readonly ILogger<SessionUtil> _logger;
    private readonly NavigationManager _navigationManager;

    // Cached access token (string) + expiration (UTC ticks).
    // _accessToken is read outside locks for fast path, but written under _updateLock for coherence.
    private string? _accessToken;

    // UTC ticks of JWT expiration; 0 means "none/unknown".
    // Stored as DateTimeOffset.UtcTicks.
    private AtomicLong _expirationTicks;

    // Swapped atomically whenever a new watcher is created.
    private CancellationTokenSource? _cts;

    private readonly string _sessionExpiredUri;

    // Protects token refresh/update, redirect-once, and CTS swap.
    private readonly AsyncLock _updateLock = new();

    // Redirect flag; access under _updateLock when mutating
    private bool _hasRedirected;

    private static readonly TimeSpan _refreshThreshold = TimeSpan.FromMinutes(1);

    // Max Task.Delay we will schedule in one shot (keep well below int.MaxValue ms)
    private static readonly TimeSpan _maxDelayChunk = TimeSpan.FromDays(20);

    // Single-flight token request (never await under _updateLock)
    private Task<AccessTokenResult>? _inFlightTokenRequest;

    // Hard timeout for MSAL token acquisition (prevents "infinite hang")
    private static readonly TimeSpan _tokenAcquireTimeout = TimeSpan.FromSeconds(30);

    public SessionUtil(
        INavigationUtil navigationUtil,
        IAccessTokenProvider accessTokenProvider,
        ILogger<SessionUtil> logger,
        IConfiguration config,
        NavigationManager navigationManager)
    {
        _navigationUtil = navigationUtil;
        _accessTokenProvider = accessTokenProvider;
        _logger = logger;
        _navigationManager = navigationManager;

        var sessionExpiredUri = config.GetValue<string>("Session:Uri");
        _sessionExpiredUri = sessionExpiredUri.HasContent() ? sessionExpiredUri : "errors/sessionexpired";
    }

    public async ValueTask<string> GetAccessToken(CancellationToken cancellationToken = default)
    {
        // --------
        // Fast path: cached token and not near expiry
        // --------
        string? tokenValue = _accessToken;
        long expTicks = _expirationTicks.Read();

        if (tokenValue is not null && expTicks != 0)
        {
            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            if (expTicks - nowTicks >= _refreshThreshold.Ticks)
                return tokenValue;
        }

        // --------
        // Slow path: single-flight request, but NEVER hold _updateLock while awaiting MSAL.
        // --------
        Task<AccessTokenResult> requestTask;

        using (await _updateLock.Lock(cancellationToken))
        {
            // Double-check after acquiring lock
            tokenValue = _accessToken;
            expTicks = _expirationTicks.Read();

            if (tokenValue is not null && expTicks != 0)
            {
                long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                if (expTicks - nowTicks >= _refreshThreshold.Ticks)
                    return tokenValue;
            }

            // Create or reuse in-flight request
            _inFlightTokenRequest ??= _accessTokenProvider.RequestAccessToken().AsTask();
            requestTask = _inFlightTokenRequest;
        }

        AccessTokenResult result;

        // Await outside lock + ensure in-flight is cleared on ANY completion path.
        try
        {
            Task completed = await Task.WhenAny(requestTask, Task.Delay(_tokenAcquireTimeout, cancellationToken))
                                       ;

            if (!ReferenceEquals(completed, requestTask))
            {
                // Timeout or cancellation won the race.
                await ResetTokenFlowAndRedirectOnStall(requestTask, cancellationToken);
                throw new TimeoutException($"RequestAccessToken timed out after {_tokenAcquireTimeout}.");
            }

            // Propagate exceptions/cancellation from requestTask here
            result = await requestTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // If caller canceled, don't redirect; just ensure we don't keep a poisoned in-flight task.
            await ClearInFlightIfMatches(requestTask, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            // Any MSAL/interop fault: clear in-flight so future calls can retry.
            await ClearInFlightIfMatches(requestTask, CancellationToken.None);

            // Optional: if you want to force a session-expired redirect on token pipeline faults:
            _logger.LogError(ex, "Access token acquisition failed");
            await ClearStateAndRedirect(error: true, cancellationToken: CancellationToken.None);

            throw;
        }
        finally
        {
            // If the task completed (success/fault/cancel), ensure it doesn't remain cached.
            if (requestTask.IsCompleted)
                await ClearInFlightIfMatches(requestTask, CancellationToken.None);
        }

        // Commit/clear state under lock
        using (await _updateLock.Lock(cancellationToken))
        {
            if (result.TryGetToken(out AccessToken? token))
            {
                _accessToken = token.Value;

                // We are holding _updateLock already
                await UpdateWithAccessToken_NoLock(token.Expires);

                return _accessToken!;
            }

            // Failed to acquire token: clear local state and force interactive.
            _accessToken = null;

            await ClearState_NoLock();

            _navigationManager.NavigateToLogin(result.InteractiveRequestUrl);
            throw new AccessTokenNotAvailableException(_navigationManager, result, null);
        }
    }

    public async ValueTask UpdateWithAccessToken(DateTimeOffset expiration, CancellationToken cancellationToken = default)
    {
        long newTicks = expiration.ToUniversalTime().UtcTicks;

        // Fast path: if unchanged, skip lock/work
        if (_expirationTicks.Read() == newTicks)
            return;

        using (await _updateLock.Lock(cancellationToken))
        {
            if (_expirationTicks.Read() == newTicks)
                return;

            await UpdateWithAccessToken_NoLock(expiration);
        }
    }

    // Assumes caller holds _updateLock
    private async ValueTask UpdateWithAccessToken_NoLock(DateTimeOffset expiration)
    {
        long newTicks = expiration.ToUniversalTime().UtcTicks;

        _hasRedirected = false;
        _expirationTicks.Write(newTicks);

        var newCts = new CancellationTokenSource();
        CancellationTokenSource? oldCts = Interlocked.Exchange(ref _cts, newCts);

        try
        {
            if (oldCts != null)
                await oldCts.CancelAsync();
        }
        catch
        {
            /* ignore */
        }
        finally
        {
            oldCts?.Dispose();
        }

        _ = RunInBackground(newCts.Token);
    }

    private async Task RunInBackground(CancellationToken token)
    {
        try
        {
            long expTicks = _expirationTicks.Read();

            if (expTicks == 0)
            {
                await ClearStateAndRedirect(error: true, cancellationToken: token);
                return;
            }

            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            long remainingTicks = expTicks - nowTicks;

            if (remainingTicks <= 0)
            {
                await ClearStateAndRedirect(error: false, cancellationToken: token);
                return;
            }

            // Chunk long delays to stay responsive to cancellation
            while (remainingTicks > 0)
            {
                TimeSpan chunk = remainingTicks > _maxDelayChunk.Ticks
                    ? _maxDelayChunk
                    : TimeSpan.FromTicks(remainingTicks);

                try
                {
                    await DelayUtil.Delay(chunk, null, token);
                }
                catch (TaskCanceledException)
                {
                    // New expiration set / canceled: just exit
                    return;
                }

                long currentExpTicks = _expirationTicks.Read();

                if (currentExpTicks == 0)
                {
                    await ClearStateAndRedirect(error: false, cancellationToken: token);
                    return;
                }

                long nowTicks2 = DateTimeOffset.UtcNow.UtcTicks;
                remainingTicks = currentExpTicks - nowTicks2;

                if (remainingTicks <= 0)
                    break;
            }

            long finalExpTicks = _expirationTicks.Read();
            if (finalExpTicks == 0 || DateTimeOffset.UtcNow.UtcTicks >= finalExpTicks)
            {
                await ClearStateAndRedirect(error: false, cancellationToken: token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session background loop failed");
        }
    }

    public async ValueTask ClearStateAndRedirect(bool error, CancellationToken cancellationToken = default)
    {
        using (await _updateLock.Lock(cancellationToken))
        {
            if (_hasRedirected)
                return;

            _hasRedirected = true;

            if (error)
                _logger.LogError("Session expiration errored, resetting state");
            else
                _logger.LogWarning("Session expired, redirecting to expiration page");

            await ClearState_NoLock();
        }

        _navigationUtil.NavigateTo(_sessionExpiredUri);
    }

    public ValueTask ClearState() => ClearState_Locked();

    private async ValueTask ClearState_Locked()
    {
        using (await _updateLock.Lock(CancellationToken.None))
        {
            await ClearState_NoLock();
        }
    }

    // Assumes caller holds _updateLock
    private async ValueTask ClearState_NoLock()
    {
        _accessToken = null;
        _expirationTicks.Write(0);

        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);

        if (cts != null)
        {
            try
            {
                await cts.CancelAsync();
            }
            catch
            {
                /* ignore */
            }

            cts.Dispose();
        }
    }

    private async ValueTask ClearInFlightIfMatches(Task<AccessTokenResult> requestTask, CancellationToken cancellationToken)
    {
        using (await _updateLock.Lock(cancellationToken))
        {
            if (ReferenceEquals(_inFlightTokenRequest, requestTask))
                _inFlightTokenRequest = null;
        }
    }

    private async ValueTask ResetTokenFlowAndRedirectOnStall(Task<AccessTokenResult> requestTask, CancellationToken cancellationToken)
    {
        // We cannot reliably obtain InteractiveRequestUrl on a stall, so redirect to session-expired
        using (await _updateLock.Lock(cancellationToken))
        {
            if (ReferenceEquals(_inFlightTokenRequest, requestTask))
                _inFlightTokenRequest = null;

            _accessToken = null;
            await ClearState_NoLock();

            // Ensure we don't spam redirects
            if (_hasRedirected)
                return;

            _hasRedirected = true;
        }

        _logger.LogWarning("Access token acquisition stalled; redirecting to expiration page");
        _navigationUtil.NavigateTo(_sessionExpiredUri);
    }

    public ValueTask DisposeAsync() => ClearState();

    public void Dispose()
    {
        // Best-effort: dispose without awaiting.
        _accessToken = null;
        _expirationTicks.Write(0);

        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null)
            return;

        try
        {
            cts.Cancel();
        }
        catch
        {
            /* ignore */
        }

        cts.Dispose();
    }
}
