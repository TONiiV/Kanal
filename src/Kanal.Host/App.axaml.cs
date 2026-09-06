using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.Host;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Before the first window, so nothing is built in one language and shown in another.
            Localizer.Instance.Current = SettingsStore.Load().AppLanguage ?? Localizer.FromSystem();

            var splash = new SplashWindow();
            desktop.MainWindow = splash;

            // Let the lightweight splash render before constructing the host view model and its
            // device watcher. The splash covers real startup work; there is no artificial delay.
            splash.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                var main = new MainWindow
                {
                    DataContext = new MainViewModel(),
                };

                desktop.MainWindow = main;
                main.Show();
                splash.Close();
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
