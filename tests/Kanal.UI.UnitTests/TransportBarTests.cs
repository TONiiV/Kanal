using Avalonia.Headless.XUnit;
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
    public void TheCompactStateSpeaksForEveryStateThatUsedToHaveItsOwnBand()
    {
        var vm = Idle();

        Assert.Empty(vm.CompactState);

        vm.IsStarting = true;
        var loading = vm.CompactState;
        Assert.NotEmpty(loading);

        vm.IsStarting = false;
        vm.IsRunning = true;
        vm.IsTranscribing = true;
        var running = vm.CompactState;
        Assert.NotEmpty(running);

        vm.IsPaused = true;
        var paused = vm.CompactState;
        Assert.NotEmpty(paused);
        Assert.NotEqual(running, paused);
    }

    [AvaloniaFact]
    public void TheCompactStateFallsSilentOnceTheMeetingEnds()
    {
        var vm = Idle();
        vm.IsRunning = true;
        vm.IsTranscribing = true;

        vm.IsRunning = false;
        vm.IsTranscribing = false;

        Assert.Empty(vm.CompactState);
    }
}
