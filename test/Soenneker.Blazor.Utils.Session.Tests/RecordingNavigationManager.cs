using Microsoft.AspNetCore.Components;

namespace Soenneker.Blazor.Utils.Session.Tests;

internal sealed class RecordingNavigationManager : NavigationManager
{
    internal int NavigationCount { get; private set; }

    internal RecordingNavigationManager() => Initialize("https://example.test/", "https://example.test/");

    protected override void NavigateToCore(string uri, bool forceLoad) => NavigationCount++;
}
