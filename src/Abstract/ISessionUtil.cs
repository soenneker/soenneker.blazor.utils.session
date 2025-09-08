using System;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Blazor.Utils.Session.Abstract;

/// <summary>
/// Provides session management utilities for Blazor applications, automatically
/// triggering navigation when a JWT expires.
/// </summary>
/// <remarks>
/// The session expiration redirect target is configurable via the
/// <c>Session:Uri</c> configuration value. This service is intended for
/// scoped dependency injection in Blazor WebAssembly.
/// </remarks>
public interface ISessionUtil : IDisposable, IAsyncDisposable
{
    ValueTask<string> GetAccessToken(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the session expiration timer using the specified JWT expiration time.
    /// Cancels any existing timer and schedules a new background task to monitor expiration.
    /// </summary>
    /// <param name="expiration">
    /// The <see cref="DateTime"/> (UTC) when the JSON Web Token will expire.
    /// </param>
    /// <param name="cancellationToken"></param>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes once the expiration update has been applied.
    /// </returns>
    ValueTask UpdateWithAccessToken(DateTime expiration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all session state and navigates to the configured expiration page.
    /// </summary>
    /// <param name="error">
    /// <c>true</c> if the state is being cleared due to an error (e.g., missing expiration);
    /// <c>false</c> if clearing due to normal token expiration.
    /// </param>
    /// <param name="cancellationToken"></param>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes once the state has been cleared
    /// and navigation has started.
    /// </returns>
    ValueTask ClearStateAndRedirect(bool error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the JWT expiration and cancels any pending expiration timer
    /// without performing a navigation redirect.
    /// </summary>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes once the session state has been cleared.
    /// </returns>
    ValueTask ClearState();
}
