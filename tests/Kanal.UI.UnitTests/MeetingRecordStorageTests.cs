using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Workspaces;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// A meeting that was actually held has to end up in a meeting record. Before this, the live
/// transcript reached disk only if the operator remembered to press Export, the recording went to
/// a different folder under the room id, and a hundred meetings left the workspace empty.
/// </summary>
public class MeetingRecordStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-record-" + Guid.NewGuid().ToString("N"));

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

    private static MainViewModel Demo(WorkspaceStore store, Func<DateTimeOffset>? utcNow = null)
    {
        var vm = TestViewModels.Hermetic(workspaces: () => store, utcNow: utcNow);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
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

    private static MeetingRecord Only(WorkspaceStore store, string workspaceId) =>
        Assert.Single(store.ListMeetings(workspaceId).Meetings);

    [AvaloniaFact]
    public async Task TheTranscriptIsOnDiskBeforeTheMeetingEnds()
    {
        var store = Store();
        var workspace = Opened(store);
        var vm = Demo(store);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(2500);

        // Read while the room is still open: a crash at this moment must not cost the hour.
        var running = Only(store, workspace.Id);
        Assert.EndsWith(TranscriptLog.FileName, running.TranscriptPath);
        Assert.NotEmpty(TranscriptLog.Read(running.TranscriptPath!));

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task WhenTheMeetingBeganAndEndedIsWrittenOntoTheRecord()
    {
        var store = Store();
        var workspace = Opened(store);
        var clock = new DateTimeOffset(2026, 9, 8, 14, 30, 0, TimeSpan.Zero);
        var vm = Demo(store, () => clock);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1500);

        var started = Only(store, workspace.Id);
        Assert.Equal(clock, started.StartedAt);
        Assert.Null(started.EndedAt);

        clock = clock.AddMinutes(48);
        await vm.StopCommand.ExecuteAsync(null);

        var ended = Only(store, workspace.Id);
        Assert.Equal(clock, ended.EndedAt);
        Assert.Equal(["zh", "de", "pl"], ended.Languages);
    }

    /// <summary>
    /// The room id is a bearer capability bound for a public Realtime topic. It has no business
    /// naming a folder, a file, or the name a save dialog offers.
    /// </summary>
    [AvaloniaFact]
    public async Task NoRoomIdReachesTheFileSystem()
    {
        var store = Store();
        var workspace = Opened(store);
        var vm = Demo(store);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(2000);
        var roomId = vm.LoadedRoomId;
        await vm.StopCommand.ExecuteAsync(null);

        string? offered = null;
        vm.ChooseExportPath = (_, name) => { offered = name; return Task.FromResult<string?>(null); };
        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        Assert.NotEmpty(roomId);
        Assert.DoesNotContain(roomId, offered);
        var record = Only(store, workspace.Id);
        Assert.DoesNotContain(roomId, record.TranscriptPath);
        Assert.All(
            Directory.GetFileSystemEntries(workspace.RootPath, "*", SearchOption.AllDirectories),
            entry => Assert.DoesNotContain(roomId, entry));
    }

    /// <summary>
    /// The binding rule: an untouched record is the one the meeting is written into, so an
    /// operator who made a record for today's call gets that record, not a second one beside it.
    /// </summary>
    [AvaloniaFact]
    public async Task AnUntouchedRecordIsTheOneTheMeetingIsWrittenInto()
    {
        var store = Store();
        var workspace = Opened(store);
        var vm = Demo(store);
        vm.Sidebar.NewMeetingCommand.Execute(null);
        var chosen = vm.Sidebar.SelectedMeeting!.Id;

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1500);
        await vm.StopCommand.ExecuteAsync(null);

        var record = Only(store, workspace.Id);
        Assert.Equal(chosen, record.Id);
        Assert.NotNull(record.StartedAt);
    }

    /// <summary>
    /// One record, one meeting: the second Start gets its own, because writing into a record that
    /// already holds an hour of speech would overwrite it.
    /// </summary>
    [AvaloniaFact]
    public async Task ASecondMeetingNeverWritesOverTheFirst()
    {
        var store = Store();
        var workspace = Opened(store);
        var vm = Demo(store);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1500);
        await vm.StopCommand.ExecuteAsync(null);
        var first = Only(store, workspace.Id);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1500);
        await vm.StopCommand.ExecuteAsync(null);

        var meetings = store.ListMeetings(workspace.Id).Meetings;
        Assert.Equal(2, meetings.Count);
        Assert.NotEmpty(TranscriptLog.Read(first.TranscriptPath!));
        Assert.Equal(2, meetings.Select(m => Path.GetDirectoryName(m.TranscriptPath)).Distinct().Count());
    }

    /// <summary>
    /// The first launch creates a workspace without asking, so no workspace open means the folder
    /// went away — an unplugged drive must not cancel the meeting. The host holds it and says
    /// plainly that nothing is being kept.
    /// </summary>
    [AvaloniaFact]
    public async Task WithNoWorkspaceTheMeetingRunsAndSaysItIsNotBeingKept()
    {
        var store = Store();
        var vm = Demo(store);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1000);

        Assert.True(vm.IsRunning);
        Assert.Contains(
            Kanal.Host.Localization.Localizer.Instance["status.notsaved"], vm.Status);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.Empty(store.ListWorkspaces().Workspaces);
    }
}

/// <summary>
/// Both artefacts of a meeting live in the meeting's own folder. A playable recording next to a
/// transcript somewhere else is how an hour of speech gets separated from what was said in it.
/// </summary>
public class MeetingFolderTests
{
    private static readonly PipelineMode Live = PipelineMode.Of(PipelineModeId.CloudCloud);

    [Fact]
    public void TheRecordingAndTheTranscriptShareOneFolder()
    {
        var folder = Path.Combine("workspace", "meetings", "a1b2c3");

        var audio = MainViewModel.RecordingPathFor(
            Live, CaptureProfileId.InRoom, new AppSettings(), folder);

        Assert.Equal(Path.Combine(folder, WorkspaceStore.AudioFileName), audio);
        Assert.Equal(folder, Path.GetDirectoryName(Path.Combine(folder, TranscriptLog.FileName)));
    }

    [Fact]
    public void WithoutARecordThereIsNowhereToRecordTo()
    {
        Assert.Null(MainViewModel.RecordingPathFor(
            Live, CaptureProfileId.InRoom, new AppSettings(), meetingFolder: null));
    }
}
