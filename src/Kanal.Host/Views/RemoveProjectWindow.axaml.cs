using Avalonia.Controls;
using Avalonia.Interactivity;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class RemoveProjectWindow : Window
{
    public RemoveProjectWindow(string projectName)
    {
        InitializeComponent();
        Heading.Text = Localizer.Instance.Format("workspace.remove.heading", projectName);
    }

    private void OnDeleteFilesClick(object? sender, RoutedEventArgs e) => Close(ProjectRemoval.DeleteFiles);

    private void OnListOnlyClick(object? sender, RoutedEventArgs e) => Close(ProjectRemoval.ListOnly);

    private void OnKeepClick(object? sender, RoutedEventArgs e) => Close(null);
}
