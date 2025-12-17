using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using Soenneker.Blazor.Utils.Navigation.Abstract;
using Soenneker.Blazor.Utils.Session.Abstract;
using Soenneker.Extensions.String;
using Soenneker.Utils.Delay;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Atomics.Longs;
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

    // UTC ticks of JWT expiration; 0 means "none/unknown".
    private readonly AtomicLong _expirationTicks = new();

    // Swapped atomically whenever a new watcher is created.
    private CancellationTokenSource? _cts;

    private readonly string _sessionExpiredUri;

    // Protects redirect-once and CTS swap + redirect states when needed.
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
        DateTime now = DateTime.UtcNow;
        long expTicks = _expirationTicks.Read();

        // If we think the token is about to expire, clear our local watcher state so we re-evaluate.
        if (expTicks != 0 && (new DateTime(expTicks, DateTimeKind.Utc) - now) < _refreshThreshold)
        {
            await ClearState()
                .NoSync();
        }

        // Normal MSAL pipeline
        AccessTokenResult result = await _accessTokenProvider.RequestAccessToken()
                                                             .NoSync();

        if (result.TryGetToken(out AccessToken? token))
        {
            await UpdateWithAccessToken(token.Expires.UtcDateTime, cancellationToken)
                .NoSync();
            return token.Value;
        }

        await ClearState()
            .NoSync();
        _navigationManager.NavigateToLogin(result.InteractiveRequestUrl);

        throw new AccessTokenNotAvailableException(_navigationManager, result, null);
    }

    public async ValueTask UpdateWithAccessToken(DateTime expiration, CancellationToken cancellationToken = default)
    {
        long newTicks = expiration.Kind == DateTimeKind.Utc
            ? expiration.Ticks
            : expiration.ToUniversalTime()
                        .Ticks;

        // Fast path: if unchanged, skip lock/work
        if (_expirationTicks.Read() == newTicks)
            return;

        using (await _updateLock.LockAsync(cancellationToken)
                                .ConfigureAwait(false))
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
                await ClearStateAndRedirect(error: true, cancellationToken: token)
                    .NoSync();
                return;
            }

            DateTime now = DateTime.UtcNow;
            var expirationUtc = new DateTime(expTicks, DateTimeKind.Utc);
            TimeSpan delay = expirationUtc - now;

            if (delay <= TimeSpan.Zero)
            {
                await ClearStateAndRedirect(error: false, cancellationToken: token)
                    .NoSync();
                return;
            }

            // Chunk long delays to stay responsive to cancellation
            while (delay > TimeSpan.Zero)
            {
                TimeSpan chunk = delay > _maxDelayChunk ? _maxDelayChunk : delay;

                try
                {
                    await DelayUtil.Delay(chunk, null, token)
                                   .NoSync();
                }
                catch (TaskCanceledException)
                {
                    // New expiration set / canceled: just exit
                    return;
                }

                // Recompute remaining delay after each chunk
                DateTime now2 = DateTime.UtcNow;
                long currentExpTicks = _expirationTicks.Read();
                if (currentExpTicks == 0)
                {
                    await ClearStateAndRedirect(error: false, cancellationToken: token)
                        .NoSync();
                    return;
                }

                var currentExpUtc = new DateTime(currentExpTicks, DateTimeKind.Utc);
                delay = currentExpUtc - now2;

                if (delay <= TimeSpan.Zero)
                    break;
            }

            // If after waiting we are expired or we lost our expiration, redirect
            long finalExpTicks = _expirationTicks.Read();
            if (finalExpTicks == 0 || DateTime.UtcNow >= new DateTime(finalExpTicks, DateTimeKind.Utc))
            {
                await ClearStateAndRedirect(error: false, cancellationToken: token)
                    .NoSync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session background loop failed");
        }
    }

    public async ValueTask ClearStateAndRedirect(bool error, CancellationToken cancellationToken = default)
    {
        bool shouldNavigate;

        using (await _updateLock.LockAsync(cancellationToken)
                                .ConfigureAwait(false))
        {
            if (_hasRedirected)
                return;

            _hasRedirected = true;
            shouldNavigate = true;
        }

        if (shouldNavigate)
        {
            if (error)
                _logger.LogError("Session expiration errored, resetting state");
            else
                _logger.LogWarning("Session expired, redirecting to expiration page");

            await ClearState()
                .NoSync();
            _navigationUtil.NavigateTo(_sessionExpiredUri);
        }
    }

    public async ValueTask ClearState()
    {
        _expirationTicks.Write(0);

        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);

        if (cts != null)
        {
            try
            {
                await cts.CancelAsync()
                         .NoSync();
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