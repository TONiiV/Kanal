using Avalonia.Controls;
using Avalonia.Interactivity;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow();
        await dialog.ShowDialog(this);
        (DataContext as MainViewModel)?.RefreshKeyStatus();
    }
}
