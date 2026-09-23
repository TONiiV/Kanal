using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Relay;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// Cloud/Cloud sat for about five seconds after consent with nothing on screen: the relay room
/// was created, then the transcriber connected, one after the other, and the meeting only got
/// its record — and its name — once both had answered.
/// </summary>
public class StartConnectingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-connecting-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private sealed class GatedAsr(IAsrProvider inner, Task gate) : IAsrProvider
    {
        public int Starts;
        public bool Cancelled;

        public string Id => inner.Id;

        public AsrCapabilities Caps => inner.Caps;

        public async Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            try
            {
                await gate.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return await inner.StartAsync(options, ct);
        }
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

    private (WorkspaceStore Store, Workspace Workspace) Opened()
    {
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        return (store, store.CreateWorkspace("ACME", folder).Workspace!);
    }

    private static (MainViewModel Vm, Func<GatedAsr> Asr) Demo(
        Task asrGate, WorkspaceStore? store = null, Func<DateTimeOffset>? utcNow = null)
    {
        var vm = store is null
            ? TestViewModels.Hermetic(utcNow: utcNow)
            : TestViewModels.Hermetic(workspaces: () => store, utcNow: utcNow);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
        GatedAsr? asr = null;
        vm.PlanFilter = plan =>
        {
            asr = new GatedAsr(plan.Asr!, asrGate);
            return plan with { Asr = asr };
        };
        return (vm, () => asr!);
    }

    private static MainViewModel CloudCloud(
        Task asrGate, WorkspaceStore store, Func<DateTimeOffset> utcNow)
    {
        var settings = new AppSettings();
        settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "k"));
        settings.ActiveGladiaKeyName = "meeting-room";
        var vm = TestViewModels.Hermetic(settings, workspaces: () => store, utcNow: utcNow);
        vm.SelectedMode = vm.Modes.Single(o => o.Mode.Id == PipelineModeId.CloudCloud);
        vm.PlanFilter = plan => plan with
        {
            Asr = new GatedAsr(
                new FakeAsrProvider(loop: true, caps: new AsrCapabilities(
                    Streaming: true,
                    Diarization: true,
                    Translation: true,
                    AutoLanguageDetect: true,
                    Languages: new HashSet<string> { "zh", "de", "pl" },
                    Latency: LatencyClass.Realtime)),
                asrGate),
            Mt = null,
            CloudTranslation = true,
        };
        vm.ConfirmConsent = save => Task.FromResult<bool?>(save);
        return vm;
    }

    [AvaloniaFact]
    public async Task StartSaysItIsConnectingUntilTheRoomOpens()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _) = Demo(gate.Task);

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        Assert.True(vm.IsStarting, "nothing on screen said the room was still opening.");
        Assert.Equal(Localizer.Instance["status.connecting"], vm.Status);
        Assert.False(vm.IsRunning);
        Assert.True(vm.StopCommand.CanExecute(null), "an opening room could not be abandoned.");

        gate.SetResult();
        await starting;

        Assert.False(vm.IsStarting);
        Assert.True(vm.IsRunning);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task TheRelayRoomIsCreatedWhileTheTranscriberConnects()
    {
        var asrGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relayGate = new TaskCompletionSource<IRelayPublisher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, asr) = Demo(asrGate.Task);
        var relayRequested = false;
        vm.RelayEnabled = true;
        vm.RelayPublisherFactory = _ =>
        {
            relayRequested = true;
            return relayGate.Task;
        };

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        Assert.True(relayRequested);
        Assert.Equal(1, asr().Starts);

        asrGate.SetResult();
        await PumpAsync(50);
        Assert.False(vm.IsRunning, "the room opened before the relay had a channel for it.");

        relayGate.SetResult(new NullRelayPublisher());
        await starting;

        Assert.True(vm.IsRunning);
        Assert.True(vm.HasJoinInfo);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task TheMeetingIsNamedAsSoonAsConsentIsGiven()
    {
        var (store, workspace) = Opened();
        var now = new DateTimeOffset(2026, 9, 23, 9, 5, 0, TimeSpan.Zero);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = CloudCloud(gate.Task, store, () => now);
        var named = $"ACME {now.ToLocalTime():yyyy-MM-dd HH:mm}";

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        Assert.False(vm.IsRunning);
        Assert.Equal(named, vm.MeetingTitle);
        Assert.Equal(named, vm.Sidebar.SelectedMeeting?.Title);

        gate.SetResult();
        await starting;

        var record = Assert.Single(store.ListMeetings(workspace.Id).Meetings);
        Assert.Equal(named, record.Title);
        Assert.Equal(now, record.StartedAt);
        Assert.False(vm.Titling.NamedByHand);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AStartThatFailsLeavesNoRecordBehind()
    {
        var (store, workspace) = Opened();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _) = Demo(gate.Task, store);

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);
        Assert.Single(store.ListMeetings(workspace.Id).Meetings);

        gate.SetException(new InvalidOperationException("transcriber refused"));
        await starting;

        Assert.Empty(store.ListMeetings(workspace.Id).Meetings);
        Assert.Null(vm.Sidebar.SelectedMeeting);
        Assert.Null(vm.Sidebar.RecordingMeetingId);
        Assert.Contains("transcriber refused", vm.Status);
        Assert.False(vm.IsStarting);
        Assert.False(vm.IsRunning);
        Assert.True(vm.StartCommand.CanExecute(null), "Start never came back after a failed start.");
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ABlankRecordTheStartWouldHaveUsedIsLeftBlank()
    {
        var (store, workspace) = Opened();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _) = Demo(gate.Task, store);
        vm.Sidebar.NewMeetingCommand.Execute(null);
        var blank = vm.Sidebar.SelectedMeeting!.Record;

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);
        gate.SetException(new InvalidOperationException("transcriber refused"));
        await starting;

        var record = Assert.Single(store.ListMeetings(workspace.Id).Meetings);
        Assert.Equal(blank.Id, record.Id);
        Assert.Equal(blank.Title, record.Title);
        Assert.Null(record.StartedAt);
        Assert.Equal(blank.Id, vm.Sidebar.SelectedMeeting?.Id);
    }

    [AvaloniaFact]
    public async Task StopWhileConnectingAbandonsTheStart()
    {
        var (store, workspace) = Opened();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, asr) = Demo(gate.Task, store);

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        await vm.StopCommand.ExecuteAsync(null);
        await starting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(asr().Cancelled, "the transcriber was left connecting after Stop.");
        Assert.False(vm.IsStarting);
        Assert.False(vm.IsRunning);
        Assert.Empty(store.ListMeetings(workspace.Id).Meetings);
        Assert.Equal(Localizer.Instance["status.idle"], vm.Status);
        Assert.True(vm.StartCommand.CanExecute(null), "Start never came back after an abandoned start.");
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ARelayThatCannotBeCreatedStillOpensTheRoomWithoutAQrCode()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _) = Demo(gate.Task);
        vm.RelayEnabled = true;
        vm.RelayPublisherFactory = _ =>
            Task.FromException<IRelayPublisher>(new HttpRequestException("gateway unreachable"));

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);
        gate.SetResult();
        await starting;

        Assert.True(vm.IsRunning);
        Assert.False(vm.HasJoinInfo);
        Assert.Contains("gateway unreachable", vm.JoinError);
        await vm.StopCommand.ExecuteAsync(null);
    }
}
