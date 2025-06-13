using System;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Blazor.Utils.Session.Abstract;

/// <summary>
/// A Blazor utility for automatic navigation after JWT expiration <para/>
/// Redirection is configurable via "Session:Uri" config value
/// Scoped IoC
/// </summary>
public interface ISessionUtil : IDisposable, IAsyncDisposable
{
    ValueTask UpdateWithAccessToken(DateTime expiration);

    ValueTask ClearStateAndRedirect(bool error);

    ValueTask ClearState();
}