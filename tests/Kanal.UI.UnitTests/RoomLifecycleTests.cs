using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Providers;
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
    private sealed class RecordingRelayPublisher(
        string roomId,
        ConcurrentQueue<(string Room, RelayMessage Message)> log,
        Func<string, Task>? hold)
        : IRelayPublisher
    {
        public async Task PublishAsync(RelayMessage message, CancellationToken ct = default)
        {
            if (hold is not null)
                await hold(roomId);
            log.Enqueue((roomId, message));
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
    private static MainViewModel BuildViewModel(
        ConcurrentQueue<(string Room, RelayMessage Message)> log, Func<string, Task>? hold = null)
    {
        var vm = TestViewModels.Demo();
        vm.RelayEnabled = true;
        vm.RelayPublisherFactory = room =>
            Task.FromResult<IRelayPublisher>(new RecordingRelayPublisher(room, log, hold));
        return vm;
    }

    private sealed class RefusingAsr(IAsrProvider inner) : IAsrProvider
    {
        public string Id => inner.Id;

        public AsrCapabilities Caps => inner.Caps;

        public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
            throw new InvalidOperationException("transcriber refused");
    }

    private static string VerificationKey(MainViewModel vm)
    {
        var fragment = new Uri(vm.JoinUrl).Fragment.TrimStart('#');
        var values = fragment.Split('&')
            .Select(part => part.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts[1]));
        return values["vk"];
    }

    private static RelayMessage Verified(RelayMessage message, string verificationKey)
    {
        var envelope = Assert.IsType<SignedRelayMessage>(message);
        Assert.True(RelaySigningKey.TryVerify(verificationKey, envelope, out var verified));
        return Assert.IsAssignableFrom<RelayMessage>(verified);
    }

    [AvaloniaFact]
    public async Task StopAnnouncesTheRoomIsClosed()
    {
        var log = new ConcurrentQueue<(string Room, RelayMessage Message)>();
        var vm = BuildViewModel(log);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        var verificationKey = VerificationKey(vm);
        await vm.StopCommand.ExecuteAsync(null);

        var room = log.First().Room;
        Assert.Contains(log, e =>
            e.Room == room && Verified(e.Message, verificationKey) is RoomClosedMessage);
    }

    [AvaloniaFact]
    public async Task RestartTellsTheOldChannelWhereTheMeetingMovedTo()
    {
        var log = new ConcurrentQueue<(string Room, RelayMessage Message)>();
        var vm = BuildViewModel(log);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        var firstRoom = log.First().Room;
        var firstKey = VerificationKey(vm);
        await vm.StopCommand.ExecuteAsync(null);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        var secondKey = VerificationKey(vm);
        await vm.StopCommand.ExecuteAsync(null);

        var moved = log.Where(e => e.Room == firstRoom)
            .Select(e => (e.Room, Message: Verified(e.Message, firstKey)))
            .Where(e => e.Message is RoomMovedMessage)
            .Select(e => (e.Room, Message: (RoomMovedMessage)e.Message))
            .ToList();

        var announcement = Assert.Single(moved);
        Assert.Equal(firstRoom, announcement.Room); // published where the phones actually are
        Assert.NotEqual(firstRoom, announcement.Message.NewRoomId);
        Assert.Equal(secondKey, announcement.Message.NewVerificationKey);
        Assert.False(string.IsNullOrWhiteSpace(announcement.Message.NewInviteTicket));
        Assert.Contains(log, e =>
            e.Room == announcement.Message.NewRoomId &&
            Verified(e.Message, secondKey) is RoomConfigMessage);
    }

    [AvaloniaFact]
    public async Task FirstStartAnnouncesNoMove()
    {
        var log = new ConcurrentQueue<(string Room, RelayMessage Message)>();
        var vm = BuildViewModel(log);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        var verificationKey = VerificationKey(vm);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.DoesNotContain(log, e =>
            Verified(e.Message, verificationKey) is RoomMovedMessage);
    }

    [AvaloniaFact]
    public async Task AFailedRestartAnnouncesNoMove()
    {
        var log = new ConcurrentQueue<(string Room, RelayMessage Message)>();
        var vm = BuildViewModel(log);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        var firstRoom = log.First().Room;
        var firstKey = VerificationKey(vm);
        await vm.StopCommand.ExecuteAsync(null);

        vm.PlanFilter = plan => plan with { Asr = new RefusingAsr(plan.Asr!) };
        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.DoesNotContain(log, e =>
            e.Room == firstRoom && Verified(e.Message, firstKey) is RoomMovedMessage);
    }

    [AvaloniaFact]
    public async Task StopWhileTheMoveIsAnnouncedClosesTheNewRoom()
    {
        var log = new ConcurrentQueue<(string Room, RelayMessage Message)>();
        string? heldRoom = null;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = BuildViewModel(log, room => room == heldRoom ? gate.Task : Task.CompletedTask);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        var firstRoom = log.First().Room;
        var firstKey = VerificationKey(vm);
        await vm.StopCommand.ExecuteAsync(null);

        heldRoom = firstRoom;
        var restarting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(200);
        Assert.True(vm.IsStarting);
        await vm.StopCommand.ExecuteAsync(null);
        gate.SetResult();
        await restarting.WaitAsync(TimeSpan.FromSeconds(5));

        var moved = Assert.Single(log
            .Where(e => e.Room == firstRoom)
            .Select(e => Verified(e.Message, firstKey))
            .OfType<RoomMovedMessage>());
        Assert.Contains(log, e =>
            e.Room == moved.NewRoomId &&
            Verified(e.Message, moved.NewVerificationKey) is RoomClosedMessage);
        Assert.False(vm.IsStarting);
        Assert.False(vm.IsRunning);
        Assert.True(vm.StartCommand.CanExecute(null));
    }
}
