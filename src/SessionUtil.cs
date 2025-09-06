using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using Soenneker.Blazor.Utils.Navigation.Abstract;
using Soenneker.Blazor.Utils.Session.Abstract;
using Soenneker.Extensions.String;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Delay;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Blazor.Utils.Session;

/// <inheritdoc cref="ISessionUtil"/>
public sealed class SessionUtil : ISessionUtil
{
    private readonly INavigationUtil _navigationUtil;
    private readonly ILogger<SessionUtil> _logger;

    private DateTime? _jwtExpiration;
    private CancellationTokenSource? _cts;

    private readonly string _sessionExpiredUri;

    private readonly AsyncLock _updateLock = new();

    private bool _hasRedirected;

    public SessionUtil(INavigationUtil navigationUtil, ILogger<SessionUtil> logger, IConfiguration config)
    {
        _navigationUtil = navigationUtil;
        _logger = logger;

        var sessionExpiredUri = config.GetValue<string>("Session:Uri");

        _sessionExpiredUri = sessionExpiredUri.HasContent() ? sessionExpiredUri : "errors/sessionexpired";
    }

    public async ValueTask UpdateWithAccessToken(DateTime expiration)
    {
        using (await _updateLock.LockAsync())
        {
            _hasRedirected = false;

            if (_jwtExpiration == expiration)
                return;

            _jwtExpiration = expiration;

            if (_cts != null)
            {
                await _cts.CancelAsync();
                _cts.Dispose();
            }

            _cts = new CancellationTokenSource();
            _ = RunInBackground(_cts.Token);
        }
    }

    private async Task RunInBackground(CancellationToken cancellationToken)
    {
        try
        {
            if (_jwtExpiration == null)
            {
                await ClearStateAndRedirect(true);
                return;
            }

            TimeSpan delay = _jwtExpiration.Value - DateTime.UtcNow;

            if (delay <= TimeSpan.Zero)
            {
                await ClearStateAndRedirect(false);
                return;
            }

            try
            {
                await DelayUtil.Delay(delay, null, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                // Task was canceled due to a new expiration time being set.
                return;
            }

            if (_jwtExpiration == null || DateTime.UtcNow >= _jwtExpiration.Value)
            {
                await ClearStateAndRedirect(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session background loop failed");
        }
    }

    public async ValueTask ClearStateAndRedirect(bool error)
    {
        using (await _updateLock.LockAsync())
        {
            // only navigate once per session
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

    public async ValueTask ClearState()
    {
        _jwtExpiration = null;

        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        return ClearState();
    }

    public void Dispose()
    {
        if (_cts != null)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
