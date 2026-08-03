using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.Localization;
using Kanal.Host.Views;

namespace Kanal.Tests;

/// <summary>
/// The transport row is the one thing the operator reaches for mid-meeting, so its geometry
/// must not depend on which language the chrome happens to be in.
/// </summary>
public class TransportLayoutTests
{
    private static (Button Start, Button Pause, Button Stop) TransportButtons(MainWindow window)
    {
        var vm = (Kanal.Host.ViewModels.MainViewModel)window.DataContext!;
        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        return (
            buttons.Single(b => ReferenceEquals(b.Command, vm.StartCommand)),
            buttons.Single(b => ReferenceEquals(b.Command, vm.PauseCommand)),
            buttons.Single(b => ReferenceEquals(b.Command, vm.StopCommand)));
    }

    /// <summary>
    /// Start, Pause and Stop must render at one width in every chrome language — "Zakończ" and
    /// "Weiter" are wider than "Stop", and three unequal buttons read as three unrelated
    /// controls. A fixed-width hack on one label cannot survive a translation change.
    /// </summary>
    [AvaloniaFact]
    public void TransportButtonsShareOneWidthInEveryLanguage()
    {
        var previous = Localizer.Instance.Current;
        var window = new MainWindow { DataContext = TestViewModels.Demo() };
        window.Show();
        try
        {
            foreach (var language in Localizer.Available)
            {
                Localizer.Instance.Current = language.Code;
                Dispatcher.UIThread.RunJobs();

                var (start, pause, stop) = TransportButtons(window);
                Assert.True(start.Bounds.Width > 0, $"{language.Code}: transport row did not lay out.");
                Assert.True(
                    start.Bounds.Width == pause.Bounds.Width && pause.Bounds.Width == stop.Bounds.Width,
                    $"{language.Code}: transport widths are {start.Bounds.Width}/{pause.Bounds.Width}/{stop.Bounds.Width}.");
            }
        }
        finally
        {
            Localizer.Instance.Current = previous;
            window.Close();
        }
    }

    /// <summary>
    /// The pipeline is named once, by the mode selector. A second copy of the same fact in the
    /// masthead is a thing the operator has to read and then discard, so it must not exist.
    /// </summary>
    [AvaloniaFact]
    public void MastheadDoesNotRepeatThePipelineStatus()
    {
        var vm = TestViewModels.Demo();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            Assert.DoesNotContain(vm.TranscriptionStatus, texts);
            Assert.DoesNotContain(vm.TranslationStatus, texts);
        }
        finally
        {
            window.Close();
        }
    }
}
