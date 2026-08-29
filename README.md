[![](https://img.shields.io/nuget/v/Soenneker.Blazor.Utils.Session.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Blazor.Utils.Session/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.blazor.utils.session/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.blazor.utils.session/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/Soenneker.Blazor.Utils.Session.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Blazor.Utils.Session/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.blazor.utils.session/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.blazor.utils.session/actions/workflows/codeql.yml)

# Soenneker.Blazor.Utils.Session

Provides session management utilities for Blazor applications, including access-token caching and optional idle-timeout navigation.

## Install

```bash
dotnet add package Soenneker.Blazor.Utils.Session
```

## Quick start

```csharp
using Soenneker.Blazor.Utils.Session.Registrars;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
var result = services.AddSessionUtilAsScoped();
```

Shorthand for services.AddScoped.

## What you get

- `ISessionUtil` — Provides session management utilities for Blazor applications, including access-token caching and optional idle-timeout navigation.
- `SessionUtilRegistrar` — A Blazor utility for access-token caching and optional idle-timeout navigation.

## API at a glance

| API | What it does | Result / important behavior |
| --- | --- | --- |
| `ISessionUtil.UpdateWithAccessToken(expiration, cancellationToken)` | Updates cached access-token expiration using the specified JWT expiration time. Cancels any existing token-expiration timer and schedules a new background task to clear the cached token. | A `ValueTask` that completes once the expiration update has been applied. |
| `ISessionUtil.ClearStateAndRedirect(error, cancellationToken)` | Clears all session state and navigates to the configured expiration page. | A `ValueTask` that completes once the state has been cleared and navigation has started. |
| `ISessionUtil.ClearState()` | Clears the JWT expiration and cancels any pending expiration timer without performing a navigation redirect. | A `ValueTask` that completes once the session state has been cleared. |
| `SessionUtilRegistrar.AddSessionUtilAsScoped(services)` | Shorthand for services.AddScoped. | The same service collection, so additional registrations can be chained. |

## Important behavior

- `ISessionUtil`: The session expiration redirect target is configurable via the `Session:Uri` configuration value. This service is intended for scoped dependency injection in Blazor WebAssembly.

## Practical notes

- Cancellation stops pending work; it does not undo work that has already completed.
- Calls that return a cached or singleton value reuse the same instance until the owning service is disposed.
- Dispose instances you own when their scope ends so held resources can be released.
