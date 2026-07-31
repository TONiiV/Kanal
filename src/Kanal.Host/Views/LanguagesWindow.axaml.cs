using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Kanal.Host.Views;

/// <summary>Modal editor for the room language set; DataContext is the host's MainViewModel.</summary>
public partial class LanguagesWindow : Window
{
    public LanguagesWindow()
    {
        InitializeComponent();
    }

    private void OnDoneClick(object? sender, RoutedEventArgs e) => Close();
}
