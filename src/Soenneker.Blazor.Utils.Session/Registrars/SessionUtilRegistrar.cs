using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Blazor.Utils.Navigation.Registrars;
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
    public static IServiceCollection AddSessionUtilAsScoped(this IServiceCollection services)
    {
        services.AddNavigationUtilAsScoped();
        services.TryAddScoped<ISessionUtil, SessionUtil>();

        return services;
    }
}
