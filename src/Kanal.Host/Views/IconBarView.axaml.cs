using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;
using Avalonia.Interactivity;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class IconBarView : UserControl
{
    public IconBarView()
    {
        InitializeComponent();
        Bar.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(bounds =>
        {
            if (DataContext is MainViewModel vm)
                vm.Shell.HeaderHeight = bounds.Height;
        }));
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await new SettingsWindow().ShowDialog(owner);
        (DataContext as MainViewModel)?.RefreshPipelineStatus();
    }

    private async void OnLanguagesClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await new LanguagesWindow { DataContext = DataContext }.ShowDialog(owner);
    }
}
