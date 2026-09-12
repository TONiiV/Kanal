using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Kanal.Host.Views;

public partial class ConsentWindow : Window
{
    public ConsentWindow(bool saveAudio, bool emphasiseRemote)
    {
        InitializeComponent();
        SaveAudio.IsChecked = saveAudio;
        // Emphasised, never a second wording: a hybrid meeting has people in the room and people
        // on the call, and the reminder has to be there for both (ADR 0054, decision 27).
        if (emphasiseRemote)
            RemoteReminder.FontWeight = FontWeight.SemiBold;
    }

    private void OnInformedChanged(object? sender, RoutedEventArgs e) =>
        Start.IsEnabled = Informed.IsChecked == true;

    // Null, not false: a dismissed dialog is not an answer, and nothing may be created from it.
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnStartClick(object? sender, RoutedEventArgs e) => Close(SaveAudio.IsChecked == true);
}
