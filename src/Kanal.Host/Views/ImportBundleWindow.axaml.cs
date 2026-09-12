using Avalonia.Controls;
using Avalonia.Interactivity;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class ImportBundleWindow : Window
{
    public ImportBundleWindow(string meetingTitle)
    {
        InitializeComponent();
        Heading.Text = Localizer.Instance.Format("workspace.importbundle.heading", meetingTitle);
    }

    private void OnSkipClick(object? sender, RoutedEventArgs e) => Close(BundleImportChoice.Skip);

    private void OnSaveAsNewClick(object? sender, RoutedEventArgs e) => Close(BundleImportChoice.SaveAsNew);
}
