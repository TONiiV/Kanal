using Avalonia.Controls;
using Avalonia.Interactivity;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class OpenSourceWindow : Window
{
    public OpenSourceWindow()
    {
        InitializeComponent();
        DataContext = new OpenSourceViewModel();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
