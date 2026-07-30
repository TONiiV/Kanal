using Avalonia.Controls;
using Avalonia.Interactivity;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.Save();
        Close();
    }
}
