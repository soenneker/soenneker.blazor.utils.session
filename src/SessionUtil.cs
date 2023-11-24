using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Blazor.Utils.Navigation.Abstract;
using Soenneker.Blazor.Utils.Session.Abstract;
using Soenneker.Extensions.String;
using Soenneker.Utils.AsyncSingleton;

namespace Soenneker.Blazor.Utils.Session;

/// <inheritdoc cref="ISessionUtil"/>
public class SessionUtil : ISessionUtil
{
    private readonly INavigationUtil _navigationUtil;
    private readonly ILogger<SessionUtil> _logger;

    private DateTime? _jwtExpiration;
    private bool _running;

    private readonly AsyncSingleton<PeriodicTimer> _timer;

    private readonly string _sessionExpiredUri;

    public SessionUtil(INavigationUtil navigationUtil, ILogger<SessionUtil> logger, IConfiguration config)
    {
        _navigationUtil = navigationUtil;
        _logger = logger;

        var sessionExpiredUri = config.GetValue<string>("Session:Uri");

        if (sessionExpiredUri.HasContent())
            _sessionExpiredUri = sessionExpiredUri;
        else
            _sessionExpiredUri = "errors/sessionexpired";

        _timer = new AsyncSingleton<PeriodicTimer>(() => new PeriodicTimer(TimeSpan.FromSeconds(10)));
    }

    public void UpdateWithAccessToken(DateTime expiration)
    {
        if (_jwtExpiration == expiration)
            return;

        _jwtExpiration = expiration;

        if (!_running)
        {
            _running = true;

            // We don't want to await on this because it never ends
            _ = RunInBackground();
        }
    }

    public async Task RunInBackground()
    {
        // avoids another lazy lookup in the loop
        PeriodicTimer timer = await _timer.Get();

        while (await timer.WaitForNextTickAsync())
        {
            if (_jwtExpiration == null)
            {
                ExpireSession(true);
                return;
            }

            DateTime utcNow = DateTime.UtcNow;

            if (utcNow < _jwtExpiration!.Value)
                continue;

            ExpireSession(false);
            return;
        }
    }

    public void ExpireSession(bool error)
    {
        if (error)
            _logger.LogError("Session expiration has errored and is null, so resetting and exiting");
        else
            _logger.LogWarning("Session has expired, navigating to expiration page...");

        _jwtExpiration = null;
        _running = false;

        _navigationUtil.NavigateTo(_sessionExpiredUri);
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        return _timer.DisposeAsync();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        _timer.Dispose();
    }
}