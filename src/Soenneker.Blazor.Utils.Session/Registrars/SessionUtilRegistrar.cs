using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Blazor.Utils.Navigation.Registrars;
using Soenneker.Blazor.Utils.Session.Abstract;

namespace Soenneker.Blazor.Utils.Session.Registrars;

/// <summary>
/// A Blazor utility for access-token caching and optional idle-timeout navigation.
/// </summary>
public static class SessionUtilRegistrar
{
    /// <summary>
    /// Registers the session and navigation utilities with scoped lifetimes.
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddSessionUtilAsScoped(this IServiceCollection services)
    {
        services.AddNavigationUtilAsScoped();
        services.TryAddScoped<ISessionUtil, SessionUtil>();

        return services;
    }
}
