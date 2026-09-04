using Avalonia.Controls;
using Avalonia.Interactivity;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class IconBarView : UserControl
{
    public IconBarView() => InitializeComponent();

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await new SettingsWindow().ShowDialog(owner);
        (DataContext as MainViewModel)?.RefreshPipelineStatus();
    }

    /// <summary>
    /// A list in a flyout does not dismiss itself the way a menu does, and an input picker that
    /// stays open over the meeting after the choice is made is one more thing to close mid-sentence.
    /// </summary>
    private void OnDeviceChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0)
            DevicePicker.Flyout?.Hide();
    }

    private async void OnLanguagesClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await new LanguagesWindow { DataContext = DataContext }.ShowDialog(owner);
    }
}
