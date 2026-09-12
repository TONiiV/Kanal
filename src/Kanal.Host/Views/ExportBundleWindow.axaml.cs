using Avalonia.Controls;
using Avalonia.Interactivity;
using Kanal.Host.Localization;

namespace Kanal.Host.Views;

public partial class ExportBundleWindow : Window
{
    public ExportBundleWindow(string meetingTitle)
    {
        InitializeComponent();
        Heading.Text = Localizer.Instance.Format("meeting.exportbundle.heading", meetingTitle);
    }

    private void OnExportClick(object? sender, RoutedEventArgs e) => Close(IncludeAudio.IsChecked == true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
