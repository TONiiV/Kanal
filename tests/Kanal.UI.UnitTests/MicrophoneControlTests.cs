using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The microphone on the control bar is one instrument: the glyph mutes, the meter says the room
/// is arriving, and the caret picks the input. What matters outside the pixels is that muting
/// keeps the provider's stream fed and that the picker is wired to the same selection the capture
/// reads.
/// </summary>
public class MicrophoneControlTests
{
    [Fact]
    public void AMutedFrameLeavesAsSilenceOfTheSameLength()
    {
        var frame = new byte[] { 1, 2, 3, 4, 5, 6 };

        var gated = MainViewModel.Gate(frame, muted: true);

        // Not a gap: an ASR provider reads a stream that stops arriving as a dropped connection.
        Assert.Equal(frame.Length, gated.Length);
        Assert.All(gated.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void AnUnmutedFrameIsPassedThroughUntouched()
    {
        var frame = new byte[] { 1, 2, 3, 4, 5, 6 };

        var gated = MainViewModel.Gate(frame, muted: false);

        Assert.True(gated.Span.SequenceEqual(frame));
    }

    [AvaloniaFact]
    public void TheMicrophoneButtonTogglesMuteAndSaysWhatItWillDoNext()
    {
        var vm = TestViewModels.Hermetic();
        Assert.False(vm.IsMuted);
        Assert.Equal(Localizer.Instance["input.mute"], vm.MuteTip);

        vm.ToggleMuteCommand.Execute(null);

        Assert.True(vm.IsMuted);
        Assert.Equal(Localizer.Instance["input.unmute"], vm.MuteTip);

        vm.ToggleMuteCommand.Execute(null);

        Assert.False(vm.IsMuted);
        Assert.Equal(Localizer.Instance["input.mute"], vm.MuteTip);
    }

    [AvaloniaFact]
    public void AnAbsentMicrophoneIsNamedRatherThanLeftBlank()
    {
        var vm = TestViewModels.Hermetic();

        vm.SelectedDevice = null;

        Assert.Equal(Localizer.Instance["input.none"], vm.SelectedDeviceLabel);
    }

    [AvaloniaFact]
    public void TheBarCarriesAMuteButtonAMeterAndAPickerBoundToTheSameSelection()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var iconBar = window.GetLogicalDescendants().OfType<IconBarView>().Single();

        Assert.Contains(
            iconBar.GetLogicalDescendants().OfType<Button>(),
            button => ReferenceEquals(button.Command, vm.ToggleMuteCommand));

        var meter = Assert.Single(iconBar.GetLogicalDescendants().OfType<ProgressBar>());
        vm.MicLevel = 42;
        Assert.Equal(42, meter.Value);

        var picker = Assert.Single(
            iconBar.GetLogicalDescendants().OfType<Button>(),
            button => button.Name == "DevicePicker");
        // A flyout's content is not realised until it opens, which is also the only moment the
        // operator can see whether the list is the one the capture reads.
        var flyout = Assert.IsType<Flyout>(picker.Flyout);
        flyout.ShowAt(picker);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var list = Assert.IsType<ListBox>(flyout.Content);
        Assert.Same(vm.Devices, list.ItemsSource);
        Assert.Same(vm.SelectedDevice, list.SelectedItem);
        flyout.Hide();

        window.Close();
    }

    /// <summary>The level meter belongs beside the microphone, and lives in exactly one place.</summary>
    [AvaloniaFact]
    public void TheStatusBarNoLongerCarriesASecondLevelMeter()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var statusBar = window.GetLogicalDescendants().OfType<StatusBarView>().Single();

        Assert.Empty(statusBar.GetLogicalDescendants().OfType<ProgressBar>());

        window.Close();
    }
}
