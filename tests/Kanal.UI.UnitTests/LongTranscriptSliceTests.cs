using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Core.Room;
using Kanal.Core.Workspaces;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class LongTranscriptSliceTests : IDisposable
{
    private const int Rows = 40;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-slices-" + Guid.NewGuid().ToString("N"));

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

    private static Utterance Said(string id, int n) =>
        new($"{id}{n}", "S01", n * 4000L, n * 4000L + 3500, "de", $"{id} Punkt {n}", 1, UtteranceState.Final, false, 1.0,
            new Dictionary<string, string> { ["zh"] = $"{id} 第{n}条", ["de"] = $"{id} Punkt {n}" });

    private (WorkspaceStore Store, MeetingRecord First, MeetingRecord Second) TwoLongMeetings()
    {
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        var workspace = store.CreateWorkspace("ACME", folder).Workspace!;
        return (store, Stored(store, workspace, "Erste", "a"), Stored(store, workspace, "Zweite", "b"));
    }

    private static MeetingRecord Stored(WorkspaceStore store, Workspace workspace, string title, string id)
    {
        var record = store.CreateMeeting(workspace.Id, title).Meeting!;
        var path = Path.Combine(store.MeetingFolder(workspace.Id, record.Id)!, TranscriptLog.FileName);
        using (var log = new TranscriptLogWriter(path, _ => { }))
        {
            for (var n = 0; n < Rows; n++)
                log.Append(Said(id, n));
        }

        return store.SaveMeeting(record with
        {
            StartedAt = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            TranscriptPath = path,
            Languages = ["zh", "de"],
        }).Meeting!;
    }

    // A yield the test finishes by hand, one at a time.
    private sealed class HeldYields
    {
        private readonly Queue<TaskCompletionSource> _held = new();

        public int Count => _held.Count;

        public Task Hold()
        {
            var turn = new TaskCompletionSource();
            _held.Enqueue(turn);
            return turn.Task;
        }

        public void Release()
        {
            _held.Dequeue().SetResult();
            Dispatcher.UIThread.RunJobs();
        }

        public int ReleaseAll()
        {
            var released = 0;
            while (_held.Count > 0)
            {
                Release();
                released++;
            }

            return released;
        }
    }

    private static void Open(MainViewModel vm, MeetingRecord record) =>
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Id == record.Id);

    [AvaloniaFact]
    public void ALongTranscriptArrivesInSlicesAndEveryRowIsShownAtTheEnd()
    {
        var (store, first, _) = TwoLongMeetings();
        var yields = new HeldYields();
        var vm = TestViewModels.Hermetic(workspaces: () => store, yieldToUi: yields.Hold);

        Open(vm, first);

        Assert.False(vm.IsLoadingRecord);
        Assert.Equal(2, vm.ShownColumns.Count);
        Assert.All(vm.ShownColumns, column => Assert.InRange(column.Bubbles.Count, 1, Rows - 1));
        Assert.Equal(1, yields.Count);

        var slices = yields.ReleaseAll();

        Assert.True(slices > 1, "the rows should take more than one slice");
        Assert.All(vm.ShownColumns, column => Assert.Equal(Rows, column.Bubbles.Count));
        Assert.Equal(Rows, vm.ShownRuler.Ticks.Count);
        Assert.Equal(
            Enumerable.Range(0, Rows).Select(n => $"a{n}"),
            vm.ShownColumns[0].Bubbles.Select(b => b.UtteranceId));
        Assert.Equal(
            Enumerable.Range(0, Rows).Select(n => $"a{n}"),
            vm.ShownRuler.Ticks.Select(t => t.AnchorUtteranceId));
    }

    [AvaloniaFact]
    public void OpeningAnotherMeetingEndsTheFillOfTheFirstOne()
    {
        var (store, first, second) = TwoLongMeetings();
        var yields = new HeldYields();
        var vm = TestViewModels.Hermetic(workspaces: () => store, yieldToUi: yields.Hold);
        Open(vm, first);
        var firstColumns = vm.ShownColumns.ToList();

        Open(vm, second);
        var added = 0;
        foreach (var column in firstColumns)
            column.Bubbles.CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                    added++;
            };
        yields.ReleaseAll();

        Assert.Equal(0, added);
        Assert.DoesNotContain(vm.ShownColumns[0], firstColumns);
        Assert.All(vm.ShownColumns, column => Assert.Equal(Rows, column.Bubbles.Count));
        Assert.Equal(
            Enumerable.Range(0, Rows).Select(n => $"b{n}"),
            vm.ShownRuler.Ticks.Select(t => t.AnchorUtteranceId));
        Assert.Equal(
            Enumerable.Range(0, Rows).Select(n => $"b Punkt {n}"),
            vm.ShownRuler.Ticks.Select(t => t.Preview));
    }

    [AvaloniaFact]
    public void AnOldMeetingIsTakenOutInSlices()
    {
        var (store, first, second) = TwoLongMeetings();
        var yields = new HeldYields();
        var vm = TestViewModels.Hermetic(workspaces: () => store, yieldToUi: yields.Hold);
        Open(vm, first);
        yields.ReleaseAll();
        var old = vm.ShownColumns.ToList();

        Open(vm, second);

        Assert.True(vm.IsLoadingRecord);
        Assert.All(old, column => Assert.True(column.IsShown));
        Assert.All(old, column => Assert.InRange(column.Bubbles.Count, 1, Rows - 1));
        Assert.Equal(old, vm.ShownColumns);

        yields.ReleaseAll();

        Assert.False(vm.IsLoadingRecord);
        Assert.All(old, column => Assert.Empty(column.Bubbles));
        Assert.DoesNotContain(vm.ShownColumns[0], old);
        Assert.All(vm.ShownColumns, column => Assert.Equal(Rows, column.Bubbles.Count));
    }

    [AvaloniaFact]
    public void AReservedRulerLaysOutEveryTickForTheFinalCountAsTheyArrive()
    {
        var ruler = new TranscriptRulerViewModel(tag => (tag, tag, "#336699"));
        ruler.Reserve(200);

        for (var n = 0; n < 100; n++)
            ruler.Observe(Said("u", n));

        var reserved = ruler.StripHeight / 200;
        Assert.All(ruler.Ticks, tick => Assert.Equal(reserved, tick.Pitch));

        ruler.Release();

        var whole = ruler.StripHeight / 100;
        Assert.All(ruler.Ticks, tick => Assert.Equal(whole, tick.Pitch));
        Assert.Equal(whole * 100, ruler.TrackLength);
    }

    [AvaloniaFact]
    public void ARulerWithNoReserveLaysOutAsBefore()
    {
        var ruler = new TranscriptRulerViewModel(tag => (tag, tag, "#336699"));

        for (var n = 0; n < 100; n++)
            ruler.Observe(Said("u", n));

        var pitch = Math.Min(TranscriptRulerViewModel.MaxPitch, ruler.StripHeight / 100);
        Assert.All(ruler.Ticks, tick => Assert.Equal(pitch, tick.Pitch));
        Assert.Equal(pitch * 100, ruler.TrackLength);
    }
}
