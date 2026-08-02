using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}