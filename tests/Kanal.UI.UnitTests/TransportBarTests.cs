using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        try
        {
            var transport = window.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Name == "Transport");

            void AssertSilent()
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(transport.GetLogicalDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                    .Select(t => t.Text));
            }

            AssertSilent();
            vm.IsStarting = true;
            AssertSilent();
            vm.IsStarting = false;
            vm.IsRunning = true;
            vm.RecordingPath = "/tmp/room.wav";
            AssertSilent();
            vm.IsTranscribing = true;
            AssertSilent();
            vm.IsPaused = true;
            AssertSilent();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheLoadingRingStaysInsideTheStopMark()
    {
        var vm = Idle();
        vm.IsStarting = true;
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 760 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var controls = window.GetLogicalDescendants().OfType<Control>().ToList();
            var stop = controls.Single(c => c.Name == "StopMark");
            var ring = controls.Single(c => c.Name == "StartingSpinner");

            var footprint = ring.Bounds;
            for (var parent = ring.GetVisualParent(); parent != stop; parent = parent!.GetVisualParent())
                footprint = footprint.Translate(parent!.Bounds.Position);
            Assert.True(new Rect(stop.Bounds.Size).Contains(footprint),
                $"the ring {footprint} spills out of the stop mark {stop.Bounds.Size}.");
        }
        finally
        {
            window.Close();
        }
    }
}
