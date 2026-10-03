using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Blazor.Utils.Navigation;

namespace Soenneker.Blazor.Utils.Session.Tests;

public sealed class SessionLifetimeTests
{
    private static readonly FieldInfo _idleSource = typeof(SessionUtil).GetField("_idleCts", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static SessionUtil Create(ControlledTokenProvider provider, RecordingNavigationManager navigation, NavigationUtil navigationUtil) =>
        new(navigationUtil, provider, NullLogger<SessionUtil>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Session:IdleTimeoutMinutes"] = "1" }).Build(), navigation);

    private static AccessTokenResult Success() => new(AccessTokenResultStatus.Success,
        new AccessToken { Value = "test-token", Expires = DateTimeOffset.UtcNow.AddHours(1) }, string.Empty,
        new InteractiveRequestOptions { Interaction = InteractionType.GetToken, ReturnUrl = "https://example.test/" });

    [Test]
    public async Task Cancelling_one_waiter_preserves_the_shared_request()
    {
        var provider = new ControlledTokenProvider();
        var navigation = new RecordingNavigationManager();
        await using var navigationUtil = new NavigationUtil(navigation);
        await using var session = Create(provider, navigation, navigationUtil);
        using var cancellation = new CancellationTokenSource();
        Task<string> cancelled = session.GetAccessToken(cancellation.Token).AsTask();
        Task<string> surviving = session.GetAccessToken().AsTask();
        await cancellation.CancelAsync();
        try { await cancelled; throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { }
        provider.Completion.SetResult(Success());
        if (await surviving != "test-token" || provider.RequestCount != 1 || navigation.NavigationCount != 0)
            throw new InvalidOperationException("Cancellation disrupted the shared token request.");
    }

    [Test]
    public async Task Cached_token_activity_reuses_the_idle_timer_and_clear_releases_it()
    {
        var provider = new ControlledTokenProvider();
        provider.Completion.SetResult(Success());
        var navigation = new RecordingNavigationManager();
        await using var navigationUtil = new NavigationUtil(navigation);
        await using var session = Create(provider, navigation, navigationUtil);
        await session.GetAccessToken();
        object? timer = _idleSource.GetValue(session);
        for (var i = 0; i < 1000; i++)
            await session.GetAccessToken();
        if (timer is null || !ReferenceEquals(timer, _idleSource.GetValue(session)) || provider.RequestCount != 1)
            throw new InvalidOperationException("Cached activity restarted the timer or requested a token.");
        await session.ClearState();
        if (_idleSource.GetValue(session) is not null || navigation.NavigationCount != 0)
            throw new InvalidOperationException("ClearState did not release the idle timer cleanly.");
    }

    [Test]
    public async Task ClearState_prevents_an_inflight_request_from_restoring_the_token()
    {
        var provider = new ControlledTokenProvider();
        var navigation = new RecordingNavigationManager();
        await using var navigationUtil = new NavigationUtil(navigation);
        await using var session = Create(provider, navigation, navigationUtil);
        Task<string> request = session.GetAccessToken().AsTask();
        await session.ClearState();
        provider.Completion.SetResult(Success());
        try { await request; throw new InvalidOperationException("Stale token was committed."); }
        catch (OperationCanceledException) { }
        if (_idleSource.GetValue(session) is not null)
            throw new InvalidOperationException("The stale request restarted the idle timer.");
    }
}
