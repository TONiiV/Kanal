using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Core.Room;
using Kanal.Core.Workspaces;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class BrowsedRulerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-browsed-ruler-" + Guid.NewGuid().ToString("N"));

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

    private static Utterance Said(string id, string tag, long at, string lang, string text) =>
        new(id, tag, at, at + 1000, lang, text, 1, UtteranceState.Final, false, 1.0,
            new Dictionary<string, string> { ["zh"] = $"[zh] {text}", ["de"] = $"[de] {text}" });

    private (WorkspaceStore Store, Workspace Workspace) NewWorkspace()
    {
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        return (store, store.CreateWorkspace("ACME", folder).Workspace!);
    }

    private static MeetingRecord Stored(
        WorkspaceStore store, Workspace workspace, string title, params Utterance[] said)
    {
        var record = store.CreateMeeting(workspace.Id, title).Meeting!;
        var path = Path.Combine(store.MeetingFolder(workspace.Id, record.Id)!, TranscriptLog.FileName);
        using (var log = new TranscriptLogWriter(path, _ => { }))
        {
            foreach (var utterance in said)
                log.Append(utterance);
        }

        return store.SaveMeeting(record with
        {
            StartedAt = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            TranscriptPath = path,
            Languages = ["zh", "de"],
        }).Meeting!;
    }

    private (MainViewModel Vm, MeetingRecord Record) Browsing(params Utterance[] said)
    {
        var (store, workspace) = NewWorkspace();
        var record = Stored(store, workspace, "Vorbesprechung", said);
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Id == record.Id);
        return (vm, record);
    }

    private sealed class HeldReads
    {
        private readonly List<(Action Read, TaskCompletionSource Done)> _held = new();

        public Task Hold(Action read)
        {
            var done = new TaskCompletionSource();
            _held.Add((read, done));
            return done.Task;
        }

        public void Finish(int index)
        {
            _held[index].Read();
            _held[index].Done.SetResult();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Open(MainViewModel vm, MeetingRecord record) =>
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Id == record.Id);

    [AvaloniaFact]
    public void AStoredMeetingShowsItIsLoadingUntilItsTranscriptHasBeenRead()
    {
        var (store, workspace) = NewWorkspace();
        var record = Stored(store, workspace, "Vorbesprechung", Said("u1", "S1", 0, "de", "Toleranz bei KX-4402"));
        var reads = new HeldReads();
        var vm = TestViewModels.Hermetic(workspaces: () => store, offUiThread: reads.Hold);

        Open(vm, record);

        Assert.True(vm.IsLoadingRecord);
        Assert.Empty(vm.ShownColumns);
        Assert.False(vm.ShowNoStoredTranscript);

        reads.Finish(0);

        Assert.False(vm.IsLoadingRecord);
        Assert.Equal(["zh", "de"], vm.ShownColumns.Select(c => c.Language));
        Assert.Equal(["u1"], vm.ShownRuler.Ticks.Select(t => t.AnchorUtteranceId));
    }

    [AvaloniaFact]
    public void AnOlderReadNeverReplacesTheMeetingSelectedAfterIt()
    {
        var (store, workspace) = NewWorkspace();
        var first = Stored(store, workspace, "Vorbesprechung", Said("u1", "S1", 0, "de", "Toleranz bei KX-4402"));
        var second = Stored(store, workspace, "Abnahme", Said("v1", "S2", 0, "zh", "交期确认"));
        var reads = new HeldReads();
        var vm = TestViewModels.Hermetic(workspaces: () => store, offUiThread: reads.Hold);

        Open(vm, first);
        Open(vm, second);
        Open(vm, first);
        reads.Finish(2);
        var shown = vm.ShownColumns.ToList();

        reads.Finish(1);
        reads.Finish(0);

        Assert.False(vm.IsLoadingRecord);
        Assert.Equal(shown, vm.ShownColumns);
        Assert.Equal(["u1"], vm.ShownRuler.Ticks.Select(t => t.AnchorUtteranceId));
    }

    [AvaloniaFact]
    public async Task ARecordReadOnAWorkerThreadIsShownFromTheUiThread()
    {
        var (store, workspace) = NewWorkspace();
        var record = Stored(store, workspace, "Vorbesprechung", Said("u1", "S1", 0, "de", "Toleranz bei KX-4402"));
        var vm = TestViewModels.Hermetic(workspaces: () => store, offUiThread: work => Task.Run(work));
        var onUiThread = new List<bool>();
        vm.BrowsedRuler.Ticks.CollectionChanged += (_, _) => onUiThread.Add(Dispatcher.UIThread.CheckAccess());

        Open(vm, record);
        var deadline = Environment.TickCount64 + 15_000;
        while (vm.IsLoadingRecord && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.False(vm.IsLoadingRecord);
        Assert.Equal(["zh", "de"], vm.ShownColumns.Select(c => c.Language));
        Assert.NotEmpty(onUiThread);
        Assert.All(onUiThread, Assert.True);
    }

    [AvaloniaFact]
    public void LeavingARecordStillBeingReadShowsTheLiveBodyAtOnce()
    {
        var (store, workspace) = NewWorkspace();
        var record = Stored(store, workspace, "Vorbesprechung", Said("u1", "S1", 0, "de", "Toleranz bei KX-4402"));
        var reads = new HeldReads();
        var vm = TestViewModels.Hermetic(workspaces: () => store, offUiThread: reads.Hold);
        Open(vm, record);

        vm.Sidebar.SelectedMeeting = null;

        Assert.False(vm.IsLoadingRecord);
        Assert.Same(vm.Columns, vm.ShownColumns);

        reads.Finish(0);

        Assert.Same(vm.Columns, vm.ShownColumns);
        Assert.Same(vm.Ruler, vm.ShownRuler);
        Assert.Empty(vm.BrowsedRuler.Ticks);
    }

    [AvaloniaFact]
    public void ABrowsedMeetingHasARulerBuiltFromItsStoredTurns()
    {
        var (vm, _) = Browsing(
            Said("u1", "S1", 0, "de", "Toleranz bei KX-4402"),
            Said("u2", "S1", 4000, "de", "und die Lieferung"),
            Said("u3", "S2", 9000, "zh", "交期确认"));

        Assert.True(vm.IsBrowsingRecord);
        Assert.Equal(["u1", "u3"], vm.ShownRuler.Ticks.Select(t => t.AnchorUtteranceId));
        Assert.Equal(["0:00", "0:09"], vm.ShownRuler.Ticks.Select(t => t.TimeLabel));
        Assert.NotEqual(vm.ShownRuler.Ticks[0].SpeakerColor, vm.ShownRuler.Ticks[1].SpeakerColor);
        Assert.Empty(vm.Ruler.Ticks);
    }

    [AvaloniaFact]
    public void JumpingFromABrowsedTickMarksTheStoredLine()
    {
        var (vm, _) = Browsing(
            Said("u1", "S1", 0, "de", "Toleranz bei KX-4402"),
            Said("u2", "S2", 9000, "zh", "交期确认"));

        vm.ShownRuler.Jump(vm.ShownRuler.Ticks[1]);

        var marked = vm.ShownColumns.SelectMany(c => c.Bubbles).Where(b => b.IsJumpTarget);
        Assert.All(marked, b => Assert.Equal("u2", b.UtteranceId));
        Assert.NotEmpty(marked);
    }

    [Fact]
    public void LeavingTheBrowsedMeetingGivesTheLiveRulerBack()
    {
        var (vm, _) = Browsing(Said("u1", "S1", 0, "de", "Toleranz bei KX-4402"));

        vm.Sidebar.SelectedMeeting = null;

        Assert.Same(vm.Ruler, vm.ShownRuler);
    }
}
