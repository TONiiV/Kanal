using Avalonia;
using Kanal.Core.Diagnostics;
using Kanal.Host.Diagnostics;
using Kanal.Host.Services;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Kanal.Host;

sealed class Program
{
    private const string Category = "host";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Before anything else that can fail: a crash during startup is exactly the one nobody
        // is watching the screen for, and the log is where it has to turn up.
        LogSetup.Apply(SettingsStore.Load());
        WatchForUnhandledFailures();
        Log.Info(Category, $"Kanal {AppVersion.Current} starting on {RuntimeInformation.OSDescription}.");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Log.Info(Category, "Kanal stopped.");
        }
        catch (Exception ex)
        {
            Log.Error(Category, "The host exited with an unhandled exception.", ex);
            throw;
        }
        finally
        {
            // Flushed, not shut down: the capture loop and the session are still unwinding behind
            // this, and the lines they are about to write are the ones explaining why.
            LogSetup.Flush();
        }
    }

    /// <summary>
    /// The two failures that otherwise leave nothing behind: one that kills the process from a
    /// thread <c>Main</c> cannot catch, and one that dies unobserved inside an abandoned task.
    /// </summary>
    private static void WatchForUnhandledFailures()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error(Category, "Unhandled exception.", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warning(Category, "A background task failed with nobody waiting on it.", e.Exception);
            e.SetObserved();
        };
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
