using Avalonia.Controls;
using Avalonia.Interactivity;
using Kanal.Host.Localization;

namespace Kanal.Host.Views;

public partial class DeleteMeetingWindow : Window
{
    public DeleteMeetingWindow(string meetingTitle)
    {
        InitializeComponent();
        Heading.Text = Localizer.Instance.Format("meeting.delete.heading", meetingTitle);
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnKeepClick(object? sender, RoutedEventArgs e) => Close(false);
}
