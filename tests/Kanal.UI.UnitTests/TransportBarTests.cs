using Avalonia.Headless.XUnit;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// Which marks the bar offers, and what the operator is told beside them. The transport carries no
/// labels any more, so the state has to be legible from the shapes that are present and the one
/// line of text next to them.
/// </summary>
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

    /// <summary>A model that is loading can be cancelled, and stop is the control that cancels it.</summary>
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

    /// <summary>
    /// Stopping is the one state where neither mark may be pressed: the room is being torn down and
    /// a second stop, or a start racing it, is exactly what the lifecycle guards against.
    /// </summary>
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

    /// <summary>
    /// The two full-width bands are gone, so this line is the whole of what the operator is told
    /// while the meeting runs. It has to say something in every state that had a band.
    /// </summary>
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
