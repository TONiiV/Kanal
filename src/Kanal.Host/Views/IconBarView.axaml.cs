using Avalonia.Controls;
using Avalonia.Input;
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
    /// A list in a flyout does not dismiss itself the way a menu does, and an input picker left
    /// open over the meeting is one more thing to close mid-sentence. Dismissal is bound to a
    /// commit and not to the selection: a ListBox raises SelectionChanged on every arrow key, so
    /// gating on that closed the list — and switched the device — on the first keystroke of
    /// keyboard navigation.
    /// </summary>
    private void OnDeviceCommitted(object? sender, TappedEventArgs e) => DevicePicker.Flyout?.Hide();

    private void OnDevicePickerKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
            DevicePicker.Flyout?.Hide();
    }

    private async void OnLanguagesClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await new LanguagesWindow { DataContext = DataContext }.ShowDialog(owner);
    }
}
