using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Meetings;
using Kanal.Core.Models;
using Kanal.Core.Workspaces;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// Enlarging one language column to the whole transcript area is presentation only: the room, the
/// recording and the translation tasks never hear about it, and the columns that go out of sight
/// keep their instances so scroll position and live-follow survive the round trip.
/// </summary>
public class LanguageFocusTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-focus-" + Guid.NewGuid().ToString("N"));

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

    private static async Task PumpUntilAsync(Func<bool> condition, int timeoutMs = 15_000)
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

    private (MainViewModel Vm, MeetingRecord Older) DemoWithAStoredRecord()
    {
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        var workspace = store.CreateWorkspace("ACME", folder).Workspace!;

        var record = store.CreateMeeting(workspace.Id, "Vorbesprechung").Meeting!;
        var path = Path.Combine(store.MeetingFolder(workspace.Id, record.Id)!, TranscriptLog.FileName);
        using (var log = new TranscriptLogWriter(path, _ => { }))
        {
            log.Append(new Utterance("u1", "S1", 0, 1000, "de", "Toleranz bei KX-4402", 1,
                UtteranceState.Final, false, 1.0,
                new Dictionary<string, string> { ["zh"] = "KX-4402 的公差" }));
        }

        var older = store.SaveMeeting(record with
        {
            StartedAt = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 9, 1, 9, 40, 0, TimeSpan.Zero),
            TranscriptPath = path,
            Languages = ["zh", "de"],
        }).Meeting!;

        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
        return (vm, older);
    }

    private static MeetingItemViewModel Row(MainViewModel vm, string id) =>
        vm.Sidebar.Meetings.Single(m => m.Id == id);

    [AvaloniaFact]
    public async Task EnlargingOneColumnHidesTheOthersWithoutTouchingTheRoom()
    {
        var (vm, _) = DemoWithAStoredRecord();
        await vm.StartCommand.ExecuteAsync(null);
        var german = vm.Columns.Single(c => c.Language == "de");

        vm.ToggleColumnFocus(german);

        Assert.True(vm.IsLanguageFocused);
        Assert.Equal("de", vm.FocusedLanguage);
        Assert.Equal([false, true, false], vm.Columns.Select(c => c.IsShown));
        Assert.Equal(["zh", "de", "pl"], vm.Columns.Select(c => c.Language));
        Assert.Equal(["zh", "de", "pl"], vm.SelectedLanguages.Select(o => o.Code));
        Assert.True(vm.IsRunning);
        Assert.Same(german, vm.Columns[1]);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task EnlargingAgainRestoresEveryColumnAndKeepsTheirBubbles()
    {
        var (vm, _) = DemoWithAStoredRecord();
        await vm.StartCommand.ExecuteAsync(null);
        await PumpUntilAsync(() => vm.Columns.All(c => c.Bubbles.Count > 0));
        var german = vm.Columns.Single(c => c.Language == "de");
        var bubbles = german.Bubbles.ToList();

        vm.ToggleColumnFocus(german);
        vm.ToggleColumnFocus(german);

        Assert.False(vm.IsLanguageFocused);
        Assert.Null(vm.FocusedLanguage);
        Assert.All(vm.Columns, c => Assert.True(c.IsShown));
        Assert.Equal(bubbles, german.Bubbles.Take(bubbles.Count));

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task EnlargingAnotherColumnMovesTheFocusInsteadOfStackingIt()
    {
        var (vm, _) = DemoWithAStoredRecord();
        await vm.StartCommand.ExecuteAsync(null);

        vm.ToggleColumnFocus(vm.Columns[0]);
        vm.ToggleColumnFocus(vm.Columns[2]);

        Assert.Equal("pl", vm.FocusedLanguage);
        Assert.Equal([false, false, true], vm.Columns.Select(c => c.IsShown));

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task TheLiveColumnsKeepReceivingWhileOneIsEnlarged()
    {
        var (vm, _) = DemoWithAStoredRecord();
        await vm.StartCommand.ExecuteAsync(null);
        await PumpUntilAsync(() => vm.Columns.All(c => c.Bubbles.Count > 0));
        var hidden = vm.Columns[0];

        vm.ToggleColumnFocus(vm.Columns[1]);
        var mark = hidden.Bubbles.Count;

        await PumpUntilAsync(() => hidden.Bubbles.Count > mark);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task SwitchingMeetingClearsTheFocusOfTheOneLeft()
    {
        var (vm, older) = DemoWithAStoredRecord();
        await vm.StartCommand.ExecuteAsync(null);
        var active = vm.Sidebar.RecordingMeetingId!;
        vm.ToggleColumnFocus(vm.Columns[1]);

        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);

        Assert.False(vm.IsLanguageFocused);
        Assert.All(vm.ShownColumns, c => Assert.True(c.IsShown));
        Assert.All(vm.Columns, c => Assert.True(c.IsShown));

        vm.Sidebar.SelectedMeeting = Row(vm, active);
        Assert.All(vm.Columns, c => Assert.True(c.IsShown));

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public void AStoredRecordCanBeEnlargedAndIsLeftSplitWhenReopened()
    {
        var (vm, older) = DemoWithAStoredRecord();
        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);
        var chinese = vm.ShownColumns.Single(c => c.Language == "zh");

        vm.ToggleColumnFocus(chinese);

        Assert.Equal("zh", vm.FocusedLanguage);
        Assert.Equal([true, false], vm.ShownColumns.Select(c => c.IsShown));

        vm.Sidebar.SelectedMeeting = null;
        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);

        Assert.False(vm.IsLanguageFocused);
        Assert.All(vm.ShownColumns, c => Assert.True(c.IsShown));
    }

    [AvaloniaFact]
    public async Task ANewRoomWithoutTheFocusedLanguageComesBackSplit()
    {
        var (vm, _) = DemoWithAStoredRecord();
        await vm.StartCommand.ExecuteAsync(null);
        vm.ToggleColumnFocus(vm.Columns.Single(c => c.Language == "de"));
        await vm.StopCommand.ExecuteAsync(null);

        vm.LanguageOptions.Single(o => o.Code == "de").IsSelected = false;
        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsLanguageFocused);
        Assert.Equal(["zh", "pl"], vm.Columns.Select(c => c.Language));
        Assert.All(vm.Columns, c => Assert.True(c.IsShown));

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task ANewRoomAlwaysStartsSplitEvenWhenTheLanguageIsStillThere()
    {
        var (vm, _) = DemoWithAStoredRecord();
        await vm.StartCommand.ExecuteAsync(null);
        vm.ToggleColumnFocus(vm.Columns[1]);
        await vm.StopCommand.ExecuteAsync(null);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsLanguageFocused);
        Assert.All(vm.Columns, c => Assert.True(c.IsShown));

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task OnlyAColumnOfTheBodyOnScreenCanTakeTheFocus()
    {
        var (vm, older) = DemoWithAStoredRecord();
        await vm.StartCommand.ExecuteAsync(null);
        var live = vm.Columns[0];
        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);

        vm.ToggleColumnFocus(live);

        Assert.False(vm.IsLanguageFocused);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AColumnOffersToEnlargeOnlyWhileThereIsSomethingToEnlargeAgainst()
    {
        var (vm, _) = DemoWithAStoredRecord();
        vm.LanguageOptions.Single(o => o.Code == "de").IsSelected = false;
        vm.LanguageOptions.Single(o => o.Code == "pl").IsSelected = false;
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Single(vm.Columns);
        Assert.False(vm.Columns[0].CanFocus);
        vm.ToggleColumnFocus(vm.Columns[0]);
        Assert.False(vm.IsLanguageFocused);

        await vm.StopCommand.ExecuteAsync(null);

        vm.LanguageOptions.Single(o => o.Code == "de").IsSelected = true;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.All(vm.Columns, c => Assert.True(c.CanFocus));

        vm.ToggleColumnFocus(vm.Columns[0]);
        Assert.True(vm.Columns[0].CanFocus);
        Assert.True(vm.Columns[0].IsFocused);
        Assert.False(vm.Columns[1].IsFocused);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task MovingAColumnWhileOneIsEnlargedKeepsTheFocus()
    {
        var (vm, _) = DemoWithAStoredRecord();
        await vm.StartCommand.ExecuteAsync(null);
        vm.ToggleColumnFocus(vm.Columns[1]);

        vm.MoveColumn(1, 0);

        Assert.Equal("de", vm.FocusedLanguage);
        Assert.Equal([true, false, false], vm.Columns.Select(c => c.IsShown));

        await vm.StopCommand.ExecuteAsync(null);
    }
}
