using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
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
    // Stored as DateTimeOffset.UtcTicks (i.e., ticks from UTC DateTime).
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

    public SessionUtil(INavigationUtil navigationUtil, IAccessTokenProvider accessTokenProvider, ILogger<SessionUtil> logger, IConfiguration config,
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
        // Fast path: if we have a cached token, and it's not near expiry, return it with no async/MSAL work.
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
        // Slow path: lock to avoid stampede refreshing the token.
        // Re-check after acquiring lock (double-checked locking pattern).
        // --------
        using (await _updateLock.Lock(cancellationToken))
        {
            tokenValue = _accessToken;
            expTicks = _expirationTicks.Read();

            if (tokenValue is not null && expTicks != 0)
            {
                long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                if (expTicks - nowTicks >= _refreshThreshold.Ticks)
                    return tokenValue;
            }

            // Normal MSAL pipeline
            AccessTokenResult result = await _accessTokenProvider.RequestAccessToken();

            if (result.TryGetToken(out AccessToken? token))
            {
                _accessToken = token.Value;

                await UpdateWithAccessToken(token.Expires, cancellationToken);

                return _accessToken;
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
        // Normalize to UTC ticks for stable comparisons/storage
        long newTicks = expiration.ToUniversalTime()
                                  .UtcTicks;

        // Fast path: if unchanged, skip lock/work
        if (_expirationTicks.Read() == newTicks)
            return;

        using (await _updateLock.Lock(cancellationToken))
        {
            _hasRedirected = false;

            if (_expirationTicks.Read() == newTicks)
                return;

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

            // Compute delay using ticks for less overhead
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
                TimeSpan chunk = remainingTicks > _maxDelayChunk.Ticks ? _maxDelayChunk : TimeSpan.FromTicks(remainingTicks);

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
        }

        if (error)
            _logger.LogError("Session expiration errored, resetting state");
        else
            _logger.LogWarning("Session expired, redirecting to expiration page");

        await ClearState();
        _navigationUtil.NavigateTo(_sessionExpiredUri);
    }

    public ValueTask ClearState()
    {
        // Take the lock to keep state coherent if someone is refreshing token concurrently
        return ClearState_Locked();
    }

    private async ValueTask ClearState_Locked()
    {
        using (await _updateLock.Lock(CancellationToken.None))
        {
            await ClearState_NoLock();
        }
    }

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