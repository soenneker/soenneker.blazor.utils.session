using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Blazor.Navigation;
using Soenneker.Blazor.Navigation.Abstract;
using Soenneker.Blazor.Utils.Session.Abstract;

namespace Soenneker.Blazor.Utils.Session.Registrars;

/// <summary>
/// A Blazor utility for automatic navigation after JWT expiration
/// </summary>
public static class SessionUtilRegistrar
{
    /// <summary>
    /// Shorthand for <code>services.AddScoped</code>
    /// </summary>
    public static void AddSessionUtil(this IServiceCollection services)
    {
        services.TryAddScoped<INavigationUtil, NavigationUtil>();
        services.TryAddScoped<ISessionUtil, SessionUtil>();
    }
}