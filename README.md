[![](https://img.shields.io/nuget/v/Soenneker.Blazor.Utils.Session.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Blazor.Utils.Session/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.blazor.utils.session/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.blazor.utils.session/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/Soenneker.Blazor.Utils.Session.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Blazor.Utils.Session/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.blazor.utils.session/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.blazor.utils.session/actions/workflows/codeql.yml)

# Soenneker.Blazor.Utils.Session

Caches Blazor WebAssembly access tokens, refreshes tokens near expiration, and optionally redirects when session activity through the utility stops.

## Install

```bash
dotnet add package Soenneker.Blazor.Utils.Session
```

Register the scoped service in `Program.cs` after configuring Blazor WebAssembly authentication:

```csharp
using Soenneker.Blazor.Utils.Session.Registrars;

builder.Services.AddSessionUtilAsScoped();
```

Inject `ISessionUtil` where an access token is needed:

```razor
@using Soenneker.Blazor.Utils.Session.Abstract
@inject ISessionUtil Session
```

```csharp
string accessToken = await Session.GetAccessToken(cancellationToken);
```

The utility uses the registered `IAccessTokenProvider`. It returns a cached token while that token has at least one minute remaining; otherwise it coalesces concurrent callers into one provider request. A provider request that does not finish within 30 seconds clears local session state and redirects to the session-expired route.

## Configuration

`Session:Uri` controls the app-local route used for expiration and token-acquisition failures. It defaults to `errors/sessionexpired`.

An idle timeout is optional. `Session:IdleTimeoutMinutes` is preferred; `Session:TimeoutMinutes` is accepted as a fallback.

```json
{
  "Session": {
    "Uri": "account/session-expired",
    "IdleTimeoutMinutes": 30
  }
}
```

Idle activity means calls to `GetAccessToken` or `UpdateWithAccessToken`; this package does not listen for mouse, keyboard, navigation, or network activity. Omit the timeout setting, set it to `0`, or use a negative value to disable idle expiration.

## Update expiration explicitly

`GetAccessToken` records the provider token's expiration automatically. If another authentication path obtains or renews a token, supply its UTC expiration:

```csharp
await Session.UpdateWithAccessToken(jwtExpiration, cancellationToken);
```

When the timestamp is reached, the cached token is cleared. The next `GetAccessToken` call asks the provider for a token again.

## Clear session state

Clear the cached token and cancel expiration timers without navigating:

```csharp
await Session.ClearState();
```

Or clear state and navigate to the configured expiration route:

```csharp
await Session.ClearStateAndRedirect(error: false, cancellationToken);
```

The `error` argument controls whether the event is logged as an error or normal expiration. Redirects are suppressed after the first one in a session state cycle. Updating the access-token expiration starts a new cycle.

## Operational behavior

- Clearing or disposing the session invalidates token requests already in flight; stale results cannot restore cleared state.
- Cancelling one caller stops that caller from waiting. The underlying `IAccessTokenProvider` request may continue because its API does not accept a cancellation token.
- Token-provider failures clear local state, redirect to the expiration route, and are rethrown to the caller.
- The service is intended to be scoped. Dependency injection disposes it automatically; manually created instances must be disposed.
