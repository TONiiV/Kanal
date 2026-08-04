using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Relay;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// A phone stays subscribed to the channel it scanned into. When the operator stops or
/// restarts the meeting, the host must say so on that channel — otherwise the page sits
/// on a dead room looking connected, and a restart (new room id, new channel) strands
/// every participant until they rescan the QR.
/// </summary>
public class RoomLifecycleTests
{
    private sealed class RecordingRelayPublisher(string roomId, ConcurrentQueue<(string Room, RelayMessage Message)> log)
        : IRelayPublisher
    {
        public Task PublishAsync(RelayMessage message, CancellationToken ct = default)
        {
            log.Enqueue((roomId, message));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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

    /// <summary>Relay stays on (this is what is being observed) but settings do not: the real
    /// %APPDATA% file would load whatever translation model the developer has downloaded.</summary>
    private static MainViewModel BuildViewModel(ConcurrentQueue<(string Room, RelayMessage Message)> log)
    {
        var vm = TestViewModels.Demo();
        vm.RelayEnabled = true;
        vm.RelayPublisherFactory = room => new RecordingRelayPublisher(room, log);
        return vm;
    }

    [AvaloniaFact]
    public async Task StopAnnouncesTheRoomIsClosed()
    {
        var log = new ConcurrentQueue<(string Room, RelayMessage Message)>();
        var vm = BuildViewModel(log);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        await vm.StopCommand.ExecuteAsync(null);

        var room = log.First().Room;
        Assert.Contains(log, e => e.Room == room && e.Message is RoomClosedMessage);
    }

    [AvaloniaFact]
    public async Task RestartTellsTheOldChannelWhereTheMeetingMovedTo()
    {
        var log = new ConcurrentQueue<(string Room, RelayMessage Message)>();
        var vm = BuildViewModel(log);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        var firstRoom = log.First().Room;
        await vm.StopCommand.ExecuteAsync(null);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        await vm.StopCommand.ExecuteAsync(null);

        var moved = log.Where(e => e.Message is RoomMovedMessage)
            .Select(e => (e.Room, Message: (RoomMovedMessage)e.Message))
            .ToList();

        var announcement = Assert.Single(moved);
        Assert.Equal(firstRoom, announcement.Room); // published where the phones actually are
        Assert.NotEqual(firstRoom, announcement.Message.NewRoomId);
        Assert.Contains(log, e => e.Room == announcement.Message.NewRoomId);
    }

    [AvaloniaFact]
    public async Task FirstStartAnnouncesNoMove()
    {
        var log = new ConcurrentQueue<(string Room, RelayMessage Message)>();
        var vm = BuildViewModel(log);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.DoesNotContain(log, e => e.Message is RoomMovedMessage);
    }
}
