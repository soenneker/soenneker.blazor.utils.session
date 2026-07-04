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

    // Swapped atomically whenever a new token expiration watcher is created.
    private CancellationTokenSource? _cts;

    private CancellationTokenSource? _idleCts;
    private readonly TimeSpan _idleTimeout;
    private AtomicLong _lastActivityTicks;

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

        var idleTimeoutMinutes = config.GetValue<int?>("Session:IdleTimeoutMinutes") ?? config.GetValue<int?>("Session:TimeoutMinutes");
        if (idleTimeoutMinutes is > 0)
            _idleTimeout = TimeSpan.FromMinutes(idleTimeoutMinutes.Value);
    }

    public async ValueTask<string> GetAccessToken(CancellationToken cancellationToken = default)
    {
        await RecordActivity(cancellationToken);

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

            _navigationManager.NavigateToLogin(result.InteractiveRequestUrl ?? _sessionExpiredUri);
            throw new AccessTokenNotAvailableException(_navigationManager, result, null);
        }
    }

    public async ValueTask UpdateWithAccessToken(DateTimeOffset expiration, CancellationToken cancellationToken = default)
    {
        long newTicks = expiration.ToUniversalTime().UtcTicks;

        // Fast path: if unchanged, skip lock/work
        if (_expirationTicks.Read() == newTicks)
        {
            await RecordActivity(cancellationToken);
            return;
        }

        using (await _updateLock.Lock(cancellationToken))
        {
            if (_expirationTicks.Read() == newTicks)
            {
                await RecordActivity_NoLock();
                return;
            }

            await UpdateWithAccessToken_NoLock(expiration);
        }
    }

    // Assumes caller holds _updateLock
    private async ValueTask UpdateWithAccessToken_NoLock(DateTimeOffset expiration)
    {
        long newTicks = expiration.ToUniversalTime().UtcTicks;

        _hasRedirected = false;
        _expirationTicks.Write(newTicks);
        await RecordActivity_NoLock();

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
                return;

            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            long remainingTicks = expTicks - nowTicks;

            if (remainingTicks <= 0)
            {
                await ClearExpiredAccessToken(token);
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
                    return;

                long nowTicks2 = DateTimeOffset.UtcNow.UtcTicks;
                remainingTicks = currentExpTicks - nowTicks2;

                if (remainingTicks <= 0)
                    break;
            }

            long finalExpTicks = _expirationTicks.Read();
            if (finalExpTicks == 0 || DateTimeOffset.UtcNow.UtcTicks >= finalExpTicks)
            {
                await ClearExpiredAccessToken(token);
            }
        }
        catch (OperationCanceledException)
        {
            // New expiration set / canceled: just exit.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Access token expiration background loop failed");
        }
    }

    private async ValueTask RecordActivity(CancellationToken cancellationToken)
    {
        if (_idleTimeout <= TimeSpan.Zero)
            return;

        using (await _updateLock.Lock(cancellationToken))
        {
            await RecordActivity_NoLock();
        }
    }

    // Assumes caller holds _updateLock
    private async ValueTask RecordActivity_NoLock()
    {
        if (_idleTimeout <= TimeSpan.Zero || _hasRedirected)
            return;

        _lastActivityTicks.Write(DateTimeOffset.UtcNow.UtcTicks);

        var newCts = new CancellationTokenSource();
        CancellationTokenSource? oldCts = Interlocked.Exchange(ref _idleCts, newCts);

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

        _ = RunIdleTimer(newCts.Token);
    }

    private async Task RunIdleTimer(CancellationToken token)
    {
        try
        {
            while (true)
            {
                long lastActivityTicks = _lastActivityTicks.Read();

                if (lastActivityTicks == 0)
                    return;

                TimeSpan elapsed = TimeSpan.FromTicks(DateTimeOffset.UtcNow.UtcTicks - lastActivityTicks);
                TimeSpan remaining = _idleTimeout - elapsed;

                if (remaining <= TimeSpan.Zero)
                    break;

                TimeSpan chunk = remaining > _maxDelayChunk ? _maxDelayChunk : remaining;
                await DelayUtil.Delay(chunk, null, token);
            }

            await ClearStateAndRedirect(error: false, cancellationToken: token);
        }
        catch (OperationCanceledException)
        {
            // Activity reset / cleared: just exit.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session idle background loop failed");
        }
    }

    private async ValueTask ClearExpiredAccessToken(CancellationToken cancellationToken)
    {
        using (await _updateLock.Lock(cancellationToken))
        {
            long expTicks = _expirationTicks.Read();

            if (expTicks != 0 && DateTimeOffset.UtcNow.UtcTicks >= expTicks)
            {
                _accessToken = null;
                _expirationTicks.Write(0);
            }
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
        _lastActivityTicks.Write(0);

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

        CancellationTokenSource? idleCts = Interlocked.Exchange(ref _idleCts, null);

        if (idleCts != null)
        {
            try
            {
                await idleCts.CancelAsync();
            }
            catch
            {
                /* ignore */
            }

            idleCts.Dispose();
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

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask DisposeAsync() => ClearState();

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
    public void Dispose()
    {
        // Best-effort: dispose without awaiting.
        _accessToken = null;
        _expirationTicks.Write(0);
        _lastActivityTicks.Write(0);

        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null)
        {
            CancellationTokenSource? idleCts = Interlocked.Exchange(ref _idleCts, null);

            if (idleCts == null)
                return;

            try
            {
                idleCts.Cancel();
            }
            catch
            {
                /* ignore */
            }

            idleCts.Dispose();
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
            /* ignore */
        }

        cts.Dispose();

        CancellationTokenSource? idleCts2 = Interlocked.Exchange(ref _idleCts, null);
        if (idleCts2 == null)
            return;

        try
        {
            idleCts2.Cancel();
        }
        catch
        {
            /* ignore */
        }

        idleCts2.Dispose();
    }
}
