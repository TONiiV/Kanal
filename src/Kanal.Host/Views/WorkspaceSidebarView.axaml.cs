using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class WorkspaceSidebarView : UserControl
{
    public WorkspaceSidebarView() => AvaloniaXamlLoader.Load(this);

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await new SettingsWindow().ShowDialog(owner);
        (DataContext as MainViewModel)?.RefreshPipelineStatus();
    }
}
