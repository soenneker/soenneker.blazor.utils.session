using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace Soenneker.Blazor.Utils.Session.Tests;

internal sealed class ControlledTokenProvider : IAccessTokenProvider
{
    internal TaskCompletionSource<AccessTokenResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int RequestCount { get; private set; }

    public ValueTask<AccessTokenResult> RequestAccessToken()
    {
        RequestCount++;
        return new(Completion.Task);
    }

    public ValueTask<AccessTokenResult> RequestAccessToken(AccessTokenRequestOptions options) => RequestAccessToken();
}
