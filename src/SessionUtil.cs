using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Blazor.Utils.Navigation.Abstract;
using Soenneker.Blazor.Utils.Session.Abstract;
using Soenneker.Extensions.String;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Delay;

namespace Soenneker.Blazor.Utils.Session;

/// <inheritdoc cref="ISessionUtil"/>
public sealed class SessionUtil : ISessionUtil
{
    private readonly INavigationUtil _navigationUtil;
    private readonly ILogger<SessionUtil> _logger;

    private DateTime? _jwtExpiration;
    private CancellationTokenSource? _cts;

    private readonly string _sessionExpiredUri;

    public SessionUtil(INavigationUtil navigationUtil, ILogger<SessionUtil> logger, IConfiguration config)
    {
        _navigationUtil = navigationUtil;
        _logger = logger;

        var sessionExpiredUri = config.GetValue<string>("Session:Uri");

        _sessionExpiredUri = sessionExpiredUri.HasContent() ? sessionExpiredUri : "errors/sessionexpired";
    }

    public async ValueTask UpdateWithAccessToken(DateTime expiration)
    {
        if (_jwtExpiration == expiration)
            return;

        _jwtExpiration = expiration;

        if (_cts != null)
        {
            await _cts.CancelAsync().NoSync();
            _cts.Dispose();
        }

        _cts = new CancellationTokenSource();

        _ = RunInBackground(_cts.Token);
    }

    private async Task RunInBackground(CancellationToken cancellationToken)
    {
        if (_jwtExpiration == null)
        {
            await ExpireSession(true).NoSync();
            return;
        }

        TimeSpan delay = _jwtExpiration.Value - DateTime.UtcNow;

        if (delay <= TimeSpan.Zero)
        {
            await ExpireSession(false).NoSync();
            return;
        }

        try
        {
            await DelayUtil.Delay(delay, null, cancellationToken).NoSync();
        }
        catch (TaskCanceledException)
        {
            // Task was canceled due to a new expiration time being set.
            return;
        }

        if (_jwtExpiration == null || DateTime.UtcNow >= _jwtExpiration.Value)
        {
            await ExpireSession(false).NoSync();
        }
    }

    public async ValueTask ExpireSession(bool error)
    {
        if (error)
            _logger.LogError("Session expiration has errored and is null, so resetting and exiting");
        else
            _logger.LogWarning("Session has expired, navigating to expiration page...");

        _jwtExpiration = null;

        if (_cts != null)
        {
            await _cts.CancelAsync().NoSync();
            _cts.Dispose();
            _cts = null;
        }

        _navigationUtil.NavigateTo(_sessionExpiredUri);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().NoSync();
            _cts.Dispose();
            _cts = null;
        }

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (_cts != null)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        GC.SuppressFinalize(this);
    }
}