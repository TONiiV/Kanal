using Avalonia;
using Avalonia.Headless;
using Kanal.Host;

[assembly: AvaloniaTestApplication(typeof(Kanal.UI.UnitTests.UnitTestAppBuilder))]

// The application language is a singleton. Keep view-model tests deterministic when a test
// temporarily changes it and restores it in a finally block.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kanal.UI.UnitTests;

public static class UnitTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
