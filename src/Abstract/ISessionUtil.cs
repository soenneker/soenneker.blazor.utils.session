using System;

namespace Soenneker.Blazor.Utils.Session.Abstract;

/// <summary>
/// A Blazor utility for automatic navigation after JWT expiration <para/>
/// Redirection is configurable via "Session:Uri" config value
/// Scoped IoC
/// </summary>
public interface ISessionUtil : IDisposable, IAsyncDisposable
{
    void UpdateWithAccessToken(DateTime expiration);
}