using Avalonia.Controls;
using Avalonia.Interactivity;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

/// <summary>What changed, newest first — the changelog embedded in this build.</summary>
public partial class ChangelogWindow : Window
{
    public ChangelogWindow()
    {
        InitializeComponent();
        DataContext = new ChangelogViewModel();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
