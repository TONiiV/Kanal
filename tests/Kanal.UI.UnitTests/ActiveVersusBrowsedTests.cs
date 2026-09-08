using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Meetings;
using Kanal.Core.Models;
using Kanal.Core.Room;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// Clicking a record in the sidebar used to set the selection and nothing else: the body stayed
/// on the live meeting while the title row followed the record, and the next generated name was
/// written onto whichever old record the operator happened to be looking at.
/// </summary>
public class ActiveVersusBrowsedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-browse-" + Guid.NewGuid().ToString("N"));

    private sealed class Titler(string title) : IMeetingTitler
    {
        public Task<string?> SuggestAsync(IReadOnlyList<string> lines, CancellationToken ct) =>
            Task.FromResult<string?>(title);
    }

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

    private WorkspaceStore Store() => new(Path.Combine(_root, "workspaces.json"));

    private Workspace Opened(WorkspaceStore store)
    {
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        return store.CreateWorkspace("ACME", folder).Workspace!;
    }

    private static MainViewModel Demo(WorkspaceStore store, IMeetingTitler? titler = null)
    {
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
        if (titler is not null)
            vm.PlanFilter = plan => plan with { Titler = titler };
        return vm;
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

    private static Utterance Said(string id, string tag, string lang, string text) =>
        new(id, tag, 0, 1000, lang, text, 1, UtteranceState.Final, false, 1.0,
            new Dictionary<string, string> { ["zh"] = $"[zh] {text}", ["de"] = $"[de] {text}" });

    /// <summary>A record that already holds a finished meeting, as #115 leaves them on disk.</summary>
    private MeetingRecord Stored(WorkspaceStore store, Workspace workspace, string title)
    {
        var record = store.CreateMeeting(workspace.Id, title).Meeting!;
        var path = Path.Combine(store.MeetingFolder(workspace.Id, record.Id)!, TranscriptLog.FileName);
        using (var log = new TranscriptLogWriter(path, _ => { }))
        {
            log.Append(Said("u1", "S1", "de", "Toleranz bei KX-4402"));
            log.Append(Said("u2", "S2", "zh", "交期确认"));
        }

        var held = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        return store.SaveMeeting(record with
        {
            StartedAt = held,
            EndedAt = held.AddMinutes(40),
            TranscriptPath = path,
            Languages = ["zh", "de"],
        }).Meeting!;
    }

    private static MeetingItemViewModel Row(MainViewModel vm, string id) =>
        vm.Sidebar.Meetings.Single(m => m.Id == id);

    [AvaloniaFact]
    public async Task AGeneratedTitleLandsOnTheActiveMeetingNotTheRecordBeingBrowsed()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = Demo(store, new Titler("Toleranzprüfung"));

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1500);
        var active = vm.Sidebar.SelectedMeeting!.Id;

        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);
        await vm.Titling.OfferAsync([.. Enumerable.Repeat("line", 6)]);
        Dispatcher.UIThread.RunJobs();
        await vm.StopCommand.ExecuteAsync(null);

        var meetings = store.ListMeetings(workspace.Id).Meetings.ToDictionary(m => m.Id);
        Assert.Equal("Toleranzprüfung", meetings[active].Title);
        Assert.Equal("Vorbesprechung", meetings[older.Id].Title);
    }

    [AvaloniaFact]
    public void ChoosingARecordShowsWhatWasSaidInIt()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = Demo(store);

        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);

        Assert.True(vm.HasColumns);
        Assert.Equal(["zh", "de"], vm.ShownColumns.Select(c => c.Language));
        var german = vm.ShownColumns.Single(c => c.Language == "de");
        Assert.Equal(
            ["Toleranz bei KX-4402", "[de] 交期确认"],
            german.Bubbles.Select(b => b.Text));
        Assert.All(german.Bubbles, b => Assert.False(b.IsPartial));
        Assert.All(german.Bubbles, b => Assert.False(b.IsLive));
    }

    [AvaloniaFact]
    public async Task BrowsingSwitchesTheBodyAndComingBackRestoresTheLiveOne()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = Demo(store);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(2000);
        var live = vm.Columns.Single(c => c.Language == "de").Bubbles.Count;
        Assert.True(live > 0, "the demo meeting produced nothing to compare against.");

        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);
        Assert.NotSame(vm.Columns, vm.ShownColumns);
        Assert.Equal(2, vm.ShownColumns.Single(c => c.Language == "de").Bubbles.Count);
        Assert.Equal("Vorbesprechung", vm.MeetingTitle);

        vm.ReturnToActiveMeetingCommand.Execute(null);
        Assert.Same(vm.Columns, vm.ShownColumns);
        Assert.True(vm.Columns.Single(c => c.Language == "de").Bubbles.Count >= live);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task TheBannerOnlyShowsWhileTheBodyIsNotTheMeetingBeingRecorded()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = Demo(store);

        Assert.False(vm.ShowRecordingBanner);
        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);
        Assert.False(vm.ShowRecordingBanner);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1200);
        var active = vm.Sidebar.SelectedMeeting!;
        Assert.False(vm.ShowRecordingBanner);

        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);
        Assert.True(vm.ShowRecordingBanner);
        Assert.Contains(active.Title, vm.RecordingBannerText);

        vm.ReturnToActiveMeetingCommand.Execute(null);
        Assert.Same(active, vm.Sidebar.SelectedMeeting);
        Assert.False(vm.ShowRecordingBanner);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(vm.ShowRecordingBanner);
    }

    [AvaloniaFact]
    public async Task TheMeetingBeingRecordedStaysMarkedInTheSidebar()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = Demo(store);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1200);
        var active = vm.Sidebar.SelectedMeeting!;

        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);
        Assert.True(active.IsRecording);
        Assert.False(Row(vm, older.Id).IsRecording);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(active.IsRecording);
    }

    [AvaloniaFact]
    public async Task RenamingASpeakerWhileBrowsingLandsOnTheMeetingBeingRecorded()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = Demo(store);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(2000);
        var speaker = vm.Speakers.First();
        var tag = speaker.Tag;

        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);
        speaker.Name = "Frau Bauer";
        speaker.RenameCommand.Execute(null);
        await PumpAsync(300);

        Assert.All(
            vm.Columns.SelectMany(c => c.Bubbles).Where(b => b.SpeakerTag == tag),
            b => Assert.Equal("Frau Bauer", b.SpeakerName));
        Assert.DoesNotContain(
            "Frau Bauer", vm.ShownColumns.SelectMany(c => c.Bubbles).Select(b => b.SpeakerName));

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task ARecordWithNothingInItSaysSoRatherThanShowingTheLiveMeeting()
    {
        var store = Store();
        var workspace = Opened(store);
        var empty = store.CreateMeeting(workspace.Id, "Leer").Meeting!;
        var vm = Demo(store);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(2000);

        vm.Sidebar.SelectedMeeting = Row(vm, empty.Id);
        Assert.Empty(vm.ShownColumns);
        Assert.False(vm.HasColumns);
        Assert.True(vm.ShowNoStoredTranscript);
        Assert.False(vm.ShowStartHint);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AnExportWhileBrowsingIsStillOfTheMeetingThatWasRecorded()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = Demo(store, new Titler("Toleranzprüfung"));

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1500);
        await vm.Titling.OfferAsync([.. Enumerable.Repeat("line", 6)]);
        Dispatcher.UIThread.RunJobs();
        await vm.StopCommand.ExecuteAsync(null);

        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);
        string? offered = null;
        vm.ChooseExportPath = (_, name) => { offered = name; return Task.FromResult<string?>(null); };
        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        Assert.Equal("Toleranzprüfung.md", offered);
        Assert.Contains("Toleranzprüfung", vm.BuildMarkdownExport());
    }

    /// <summary>The body is a viewer while it is not the session's own record.</summary>
    [AvaloniaFact]
    public async Task TheTitleRowCannotBeEditedWhileAnotherRecordIsBeingBrowsed()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = Demo(store);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1200);
        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);

        vm.BeginRenameTitleCommand.Execute(null);

        Assert.False(vm.IsRenamingTitle);
        Assert.False(vm.CanRegenerateTitle);
        Assert.Equal("Vorbesprechung", vm.MeetingTitle);

        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// A record read back a week later has to look like the meeting that was held: the colours
    /// are handed out in the order the speakers first spoke, exactly as the room hands them out.
    /// </summary>
    [Fact]
    public void AReopenedTranscriptGivesEachSpeakerTheColourTheyHadInTheRoom()
    {
        var columns = StoredTranscript.Columns(
            [Said("u1", "S1", "de", "eins"), Said("u2", "S2", "zh", "二"), Said("u3", "S1", "de", "drei")],
            []);

        var german = columns.Single(c => c.Language == "de");
        Assert.Equal(
            [RoomState.Palette[0], RoomState.Palette[1], RoomState.Palette[0]],
            german.Bubbles.Select(b => b.SpeakerColor));
    }

    [AvaloniaFact]
    public void WithNoRecordChosenTheBodyStillExplainsHowToStart()
    {
        var vm = Demo(Store());

        Assert.True(vm.ShowStartHint);
        Assert.False(vm.ShowNoStoredTranscript);
        Assert.Equal(Localizer.Instance["meeting.untitled"], vm.MeetingTitle);
    }
}
