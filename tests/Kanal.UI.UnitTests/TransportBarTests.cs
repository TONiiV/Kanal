using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Host.Views;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class TransportBarTests
{
    private static MainViewModel Idle() => TestViewModels.Demo();

    [AvaloniaFact]
    public void IdleOffersRecordAloneWithNothingToPauseOrStop()
    {
        var vm = Idle();

        Assert.True(vm.ShowRecord);
        Assert.False(vm.ShowPause);
        Assert.False(vm.ShowStop);
    }

    [AvaloniaFact]
    public void LoadingReplacesRecordWithTheStopThatCancelsIt()
    {
        var vm = Idle();
        vm.IsStarting = true;

        Assert.False(vm.ShowRecord);
        Assert.False(vm.ShowPause);
        Assert.True(vm.ShowStop);
    }

    [AvaloniaFact]
    public void RunningOffersPauseAndStopButNoSecondRecord()
    {
        var vm = Idle();
        vm.IsRunning = true;

        Assert.False(vm.ShowRecord);
        Assert.True(vm.ShowPause);
        Assert.True(vm.ShowStop);
    }

    [AvaloniaFact]
    public void PausedStillOffersBothSoTheMeetingCanBeResumedOrEnded()
    {
        var vm = Idle();
        vm.IsRunning = true;
        vm.IsPaused = true;

        Assert.False(vm.ShowRecord);
        Assert.True(vm.ShowPause);
        Assert.True(vm.ShowStop);
    }

    [AvaloniaFact]
    public void StoppingOffersNothingToPress()
    {
        var vm = Idle();
        vm.IsRunning = true;
        vm.IsStopping = true;

        Assert.False(vm.ShowRecord);
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.False(vm.PauseCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void TheTransportCarriesNoTextInAnyState()
    {
        var vm = Idle();
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 760 };
        window.Show();
        var transport = window.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Name == "Transport");

        void AssertSilent(string state)
        {
            Dispatcher.UIThread.RunJobs();
            var words = transport.GetLogicalDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                .Select(t => t.Text);
            Assert.True(!words.Any(), $"{state}: the transport reads \"{string.Join(" / ", words)}\".");
        }

        AssertSilent("idle");
        vm.IsStarting = true;
        AssertSilent("loading");
        vm.IsStarting = false;
        vm.IsRunning = true;
        vm.IsTranscribing = true;
        AssertSilent("running");
        vm.RecordingPath = "/tmp/room.wav";
        AssertSilent("recording");
        vm.IsPaused = true;
        AssertSilent("paused");

        window.Close();
    }
}
