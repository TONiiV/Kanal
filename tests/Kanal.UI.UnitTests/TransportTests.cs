using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Relay;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The three transport states an operator drives mid-meeting. Pause exists so the room can be
/// taken off the record for a minute — a side conversation with your own side of a negotiation —
/// without ending the meeting, losing the transcript, or making everyone rescan a QR code.
/// </summary>
public class TransportTests
{
    private static async Task PumpAsync(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void PauseIsNotOfferedBeforeTheMeetingStarts()
    {
        var vm = TestViewModels.Demo();

        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.IsPaused);
    }

    [AvaloniaFact]
    public async Task PauseAndResumeToggleOnOneButton()
    {
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(120);

        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.Equal("Pause", vm.PauseLabel);

        await vm.PauseCommand.ExecuteAsync(null);
        Assert.True(vm.IsPaused);
        Assert.Equal("Resume", vm.PauseLabel);
        Assert.Contains("Paused", vm.Status);

        await vm.PauseCommand.ExecuteAsync(null);
        Assert.False(vm.IsPaused);
        Assert.Equal("Pause", vm.PauseLabel);

        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// A paused meeting is still a meeting: the room stays open, the QR keeps working, and the
    /// columns keep everything already said. Resuming must not cost anyone a rescan — that is
    /// the whole difference between Pause and Stop-then-Start.
    /// </summary>
    [AvaloniaFact]
    public async Task PausingKeepsTheRoomAndItsHistory()
    {
        var vm = TestViewModels.Demo();
        vm.RelayEnabled = true;
        // without a factory, an enabled relay falls back to the real Supabase publisher —
        // a unit test must never put packets on the production channel
        vm.RelayPublisherFactory = _ => new NullRelayPublisher();
        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Columns.Count > 0 && vm.Columns[0].Bubbles.Count > 0);

        var room = vm.JoinUrl;
        var columns = vm.Columns.Count;
        var said = vm.Columns[0].Bubbles.Count;

        await vm.PauseCommand.ExecuteAsync(null);
        await PumpAsync(200);

        Assert.True(vm.IsRunning);
        Assert.Equal(room, vm.JoinUrl);
        Assert.Equal(columns, vm.Columns.Count);
        Assert.True(vm.Columns[0].Bubbles.Count >= said, "history was dropped by pausing.");

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task StopClearsThePausedState()
    {
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(120);
        await vm.PauseCommand.ExecuteAsync(null);

        await vm.StopCommand.ExecuteAsync(null);

        Assert.False(vm.IsPaused);
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.Equal("Pause", vm.PauseLabel);
    }

    /// <summary>Starting again after a paused meeting must not open the new room already paused.</summary>
    [AvaloniaFact]
    public async Task RestartingAfterAPausedMeetingStartsLive()
    {
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(120);
        await vm.PauseCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(120);

        Assert.False(vm.IsPaused);
        await vm.StopCommand.ExecuteAsync(null);
    }
}
