using System;
using Avalonia.Controls;
using Avalonia.Input;
using Kanal.Audio;
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
    /// The list opens on the microphone in use, so arrowing starts from where the operator is
    /// rather than from wherever the list was left last time.
    /// </summary>
    private void OnDevicePickerOpening(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
            DeviceList.SelectedItem = vm.SelectedDevice;
    }

    /// <summary>
    /// The list highlights; only a commit writes the microphone through, and only a commit
    /// closes the list. Both halves matter. A ListBox raises SelectionChanged on every arrow
    /// key, so dismissing on that closed the list on the first keystroke of keyboard
    /// navigation; and a two-way bound selection meant Escape - or a click outside - left the
    /// operator on whatever row they had arrowed past, which is the opposite of what
    /// dismissing a menu means.
    /// </summary>
    private void OnDeviceCommitted(object? sender, TappedEventArgs e) => CommitDevice();

    private void OnDevicePickerKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
            CommitDevice();
    }

    private void CommitDevice()
    {
        if (DataContext is MainViewModel vm && DeviceList.SelectedItem is AudioDeviceInfo device)
            vm.SelectedDevice = device;

        DevicePicker.Flyout?.Hide();
    }

    private async void OnLanguagesClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await new LanguagesWindow { DataContext = DataContext }.ShowDialog(owner);
    }
}
