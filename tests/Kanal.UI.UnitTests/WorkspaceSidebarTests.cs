using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Workspaces;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class WorkspaceSidebarTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-sidebar-" + Guid.NewGuid().ToString("N"));

    private WorkspaceStore Store() => new(Path.Combine(_root, "workspaces.json"));

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
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

    private WorkspaceSidebarViewModel Opened(WorkspaceStore store, params string[] titles)
    {
        var workspace = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        foreach (var title in titles)
            store.CreateMeeting(workspace.Id, title);
        var vm = new WorkspaceSidebarViewModel(store);
        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == workspace.Id);
        return vm;
    }

    [Fact]
    public void SearchNarrowsTheListWithoutTouchingWhatIsOnDisk()
    {
        var store = Store();
        var vm = Opened(store, "Tooling review", "Delivery call", "Tooling handover");

        vm.Search = "tooling";

        Assert.Equal(["Tooling handover", "Tooling review"], vm.Meetings.Select(m => m.Title).Order());
        Assert.Equal(3, store.ListMeetings(vm.SelectedWorkspace!.Id).Meetings.Count);

        vm.Search = "";
        Assert.Equal(3, vm.Meetings.Count);

        // An empty list under a search is a search that found nothing, not a workspace with nothing.
        vm.Search = "no such meeting";
        Assert.Equal(Kanal.Host.Localization.Localizer.Instance["workspace.nomatches"], vm.EmptyNote);
        vm.Search = "";
        Assert.Equal(Kanal.Host.Localization.Localizer.Instance["workspace.nomeetings"], vm.EmptyNote);
    }

    [Fact]
    public void CreatingAMeetingIsItsOwnActionRatherThanAModeOfSearch()
    {
        var vm = Opened(Store(), "Delivery call");
        vm.Search = "nothing matches this";
        Assert.Empty(vm.Meetings);

        vm.NewMeetingCommand.Execute(null);

        Assert.Equal("", vm.Search);
        Assert.Equal(2, vm.Meetings.Count);
        Assert.NotNull(vm.SelectedMeeting);
    }

    [Fact]
    public void WithNoWorkspaceOpenTheMeetingActionsAreUnavailableRatherThanFailing()
    {
        var vm = new WorkspaceSidebarViewModel(Store());

        Assert.Null(vm.SelectedWorkspace);
        Assert.False(vm.NewMeetingCommand.CanExecute(null));
        Assert.False(vm.ImportMeetingRecordCommand.CanExecute(null));
    }

    [Fact]
    public void AMeetingThatCannotBeReadDoesNotHideTheOnesThatCan()
    {
        var store = Store();
        var vm = Opened(store, "Delivery call");
        var meetings = Path.Combine(vm.SelectedWorkspace!.RootPath, "meetings");
        Directory.CreateDirectory(Path.Combine(meetings, "not-a-meeting"));

        vm.Refresh();

        Assert.Equal("Delivery call", Assert.Single(vm.Meetings).Title);
        Assert.NotEqual("", vm.ProblemNote);
    }

    [Fact]
    public async Task ExportingAMeetingWritesWhereTheOperatorChose()
    {
        var store = Store();
        var vm = Opened(store, "Delivery call");
        var meeting = vm.Meetings.Single();
        var folder = store.MeetingFolder(vm.SelectedWorkspace!.Id, meeting.Id)!;
        File.WriteAllText(Path.Combine(folder, "transcript.md"), "**S01** (de): Guten Tag");
        store.SaveMeeting(meeting.Record with { TranscriptPath = Path.Combine(folder, "transcript.md") });
        vm.Refresh();

        var target = Path.Combine(Folder("out"), "delivery.md");
        vm.ChooseExportPath = _ => Task.FromResult<string?>(target);

        await vm.Meetings.Single().ExportCommand.ExecuteAsync(null);

        Assert.Equal("**S01** (de): Guten Tag", File.ReadAllText(target));
    }

    [Fact]
    public async Task ImportingIntoAMeetingPutsTheFileInThatMeetingsFolder()
    {
        var store = Store();
        var vm = Opened(store, "Delivery call");
        var source = Path.Combine(Folder("inbox"), "notes.md");
        File.WriteAllText(source, "**S01** (pl): Dzień dobry");
        vm.ChooseFileToImport = () => Task.FromResult<string?>(source);

        await vm.Meetings.Single().ImportCommand.ExecuteAsync(null);

        var stored = store.ListMeetings(vm.SelectedWorkspace!.Id).Meetings.Single();
        Assert.NotNull(stored.TranscriptPath);
        Assert.Equal("**S01** (pl): Dzień dobry", File.ReadAllText(stored.TranscriptPath!));
        Assert.StartsWith(
            store.MeetingFolder(stored.WorkspaceId, stored.Id)!, stored.TranscriptPath!);
    }

    [Fact]
    public async Task ImportingARecordAddsAMeetingToTheOpenWorkspace()
    {
        var store = Store();
        var vm = Opened(store);
        var source = Path.Combine(Folder("inbox"), "Kickoff.md");
        File.WriteAllText(source, "**S01** (zh): 你好");
        vm.ChooseFileToImport = () => Task.FromResult<string?>(source);

        await vm.ImportMeetingRecordCommand.ExecuteAsync(null);

        var imported = Assert.Single(vm.Meetings);
        Assert.Equal("Kickoff", imported.Title);
        Assert.Equal("**S01** (zh): 你好", File.ReadAllText(imported.Record.TranscriptPath!));
    }

    [Fact]
    public async Task ANewProjectIsOnlyAddedOnceAFolderIsChosen()
    {
        var vm = new WorkspaceSidebarViewModel(Store());
        vm.ChooseWorkspaceFolder = () => Task.FromResult<string?>(null);

        await vm.NewProjectCommand.ExecuteAsync(null);
        Assert.Empty(vm.Workspaces);

        var folder = Folder("lambda");
        vm.ChooseWorkspaceFolder = () => Task.FromResult<string?>(folder);
        await vm.NewProjectCommand.ExecuteAsync(null);

        // The store resolves the path it is handed, so compare on the name it derives from it.
        Assert.Equal("lambda", Assert.Single(vm.Workspaces).Name);
        Assert.Same(vm.Workspaces[0], vm.SelectedWorkspace);
    }

    private (Window Window, MainViewModel Vm) Shown()
    {
        var store = Store();
        var workspace = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        store.CreateMeeting(workspace.Id, "Delivery call");

        var vm = TestViewModels.Hermetic(workspaces: () => store);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static Control Named(Control root, string name) =>
        root.GetLogicalDescendants().OfType<Control>()
            .Where(control => control.Name == name).Distinct().Single();

    private static MenuFlyout Opened(Button owner)
    {
        var flyout = Assert.IsType<MenuFlyout>(owner.Flyout);
        flyout.ShowAt(owner);
        Dispatcher.UIThread.RunJobs();
        return flyout;
    }

    private static IEnumerable<string> Reachable(MenuFlyout flyout) =>
        flyout.Items.OfType<MenuItem>().Select(item => item.Name ?? "");

    [AvaloniaFact]
    public void TheSidebarReadsBrandThenSearchThenWorkspaceThenMeetings()
    {
        var (window, _) = Shown();
        var sidebar = window.GetLogicalDescendants().OfType<WorkspaceSidebarView>().Single();

        var spoken = new List<string>();
        foreach (var name in new[]
                 { "MeetingSearch", "NewMeeting", "WorkspacePicker", "WorkspaceAdd", "MeetingList", "Settings" })
        {
            var control = Named(sidebar, name);
            Assert.True(control.Focusable || control is ItemsControl, $"{name} cannot be reached by keyboard.");
            var spokenName = Avalonia.Automation.AutomationProperties.GetName(control);
            Assert.False(string.IsNullOrWhiteSpace(spokenName), $"{name} has no accessible name.");
            spoken.Add(spokenName!);
        }

        // A name borrowed from a neighbour reads as that neighbour, and a non-blank assertion
        // alone cannot tell the two apart.
        Assert.Equal(spoken.Count, spoken.Distinct().Count());

        window.Close();
    }

    [AvaloniaFact]
    public void TheAddMenuOffersANewProjectAndAnImportedRecord()
    {
        var (window, _) = Shown();
        var sidebar = window.GetLogicalDescendants().OfType<WorkspaceSidebarView>().Single();

        var flyout = Opened((Button)Named(sidebar, "WorkspaceAdd"));

        Assert.Equal(["NewProject", "ImportRecord", "ImportBundle"], Reachable(flyout));

        window.Close();
    }

    [AvaloniaFact]
    public void EachMeetingCarriesItsOwnImportExportBundleAndDelete()
    {
        var (window, _) = Shown();
        var sidebar = window.GetLogicalDescendants().OfType<WorkspaceSidebarView>().Single();

        var ellipsis = sidebar.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.Name == "MeetingMenu").Distinct().Single();

        Assert.Equal(
            ["ImportIntoMeeting", "ExportMeeting", "ExportBundle", "DeleteMeeting"],
            Reachable(Opened(ellipsis)));

        window.Close();
    }

    [Fact]
    public async Task DeletingAMeetingTakesTheTranscriptAndTheRecordingWithIt()
    {
        var store = Store();
        var vm = Opened(store, "Delivery call");
        var meeting = vm.Meetings.Single();
        var folder = store.MeetingFolder(vm.SelectedWorkspace!.Id, meeting.Id)!;
        var transcript = Path.Combine(folder, "transcript.jsonl");
        var audio = Path.Combine(folder, "audio.wav");
        File.WriteAllText(transcript, """{"text":"Guten Tag"}""");
        File.WriteAllBytes(audio, [0x52, 0x49, 0x46, 0x46]);
        store.SaveMeeting(meeting.Record with { TranscriptPath = transcript, AudioPath = audio });
        vm.ConfirmDeleteMeeting = _ => Task.FromResult(true);

        await meeting.DeleteCommand.ExecuteAsync(null);

        Assert.False(File.Exists(transcript));
        Assert.False(File.Exists(audio));
        Assert.False(Directory.Exists(folder));
        Assert.Empty(vm.Meetings);
        Assert.Empty(store.ListMeetings(vm.SelectedWorkspace!.Id).Meetings);
    }

    [Fact]
    public async Task AMeetingIsOnlyDeletedOnceTheOperatorHasSaidSoASecondTime()
    {
        var store = Store();
        var vm = Opened(store, "Delivery call");
        var meeting = vm.Meetings.Single();
        var folder = store.MeetingFolder(vm.SelectedWorkspace!.Id, meeting.Id)!;

        // No one to ask: an unwired confirmation must read as a refusal, never as consent.
        await meeting.DeleteCommand.ExecuteAsync(null);
        Assert.True(Directory.Exists(folder));

        var asked = 0;
        vm.ConfirmDeleteMeeting = _ =>
        {
            asked++;
            return Task.FromResult(false);
        };

        await meeting.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(1, asked);
        Assert.True(Directory.Exists(folder));
        Assert.Single(vm.Meetings);
        Assert.Single(store.ListMeetings(vm.SelectedWorkspace!.Id).Meetings);
    }

    [Fact]
    public async Task TheMeetingBeingRecordedIsTheOneThatCannotBeDeleted()
    {
        var store = Store();
        var vm = Opened(store, "Delivery call", "Kickoff");
        var live = vm.Meetings.Single(m => m.Title == "Delivery call");
        var idle = vm.Meetings.Single(m => m.Title == "Kickoff");
        vm.ConfirmDeleteMeeting = _ => Task.FromResult(true);

        vm.RecordingMeetingId = live.Id;

        Assert.False(live.DeleteCommand.CanExecute(null));
        Assert.True(idle.DeleteCommand.CanExecute(null));

        // Disabled in the menu is not the same as guarded: the command is reachable from a
        // keyboard and from a test, and the meeting still being spoken into is not deletable.
        await live.DeleteCommand.ExecuteAsync(null);
        Assert.True(Directory.Exists(store.MeetingFolder(vm.SelectedWorkspace!.Id, live.Id)!));

        // The list is rebuilt on every search keystroke, and the rebuilt rows must know too.
        vm.Refresh();
        Assert.False(vm.Meetings.Single(m => m.Title == "Delivery call").DeleteCommand.CanExecute(null));

        vm.RecordingMeetingId = null;
        Assert.True(vm.Meetings.Single(m => m.Title == "Delivery call").DeleteCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task StartingAMeetingMarksTheRecordItIsBeingSpokenInto()
    {
        var store = Store();
        var workspace = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        store.CreateMeeting(workspace.Id, "Delivery call");
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == Kanal.Host.Services.PipelineModeId.Demo);
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(vm.Sidebar.SelectedMeeting!.Id, vm.Sidebar.RecordingMeetingId);
        Assert.False(vm.Sidebar.Meetings.Single().DeleteCommand.CanExecute(null));

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Null(vm.Sidebar.RecordingMeetingId);
        Assert.True(vm.Sidebar.Meetings.Single().DeleteCommand.CanExecute(null));
    }

    private static void Bundled(WorkspaceSidebarViewModel vm, MeetingItemViewModel meeting, string at, bool audio)
    {
        var folder = vm.FolderOf(meeting.Record)!;
        File.WriteAllText(Path.Combine(folder, "transcript.jsonl"),
            """{"id":"u1","speakerTag":"S1","tStartMs":0,"srcLang":"de","srcText":"Guten Tag","revision":1,"state":"Final","codeSwitch":false,"speakerConfidence":1,"translations":{}}""");
        var pcm = new byte[120_000];
        new Random(4402).NextBytes(pcm);
        File.WriteAllBytes(Path.Combine(folder, "audio.wav"), pcm);
        vm.SaveRecord(meeting.Record with
        {
            Languages = ["de", "zh"],
            TranscriptPath = Path.Combine(folder, "transcript.jsonl"),
            AudioPath = Path.Combine(folder, "audio.wav"),
        });

        vm.ConfirmExportBundle = _ => Task.FromResult<bool?>(audio);
        vm.ChooseExportPath = _ => Task.FromResult<string?>(at);
    }

    [Fact]
    public async Task TheAudioCheckboxDecidesWhetherTheRecordingLeavesWithTheBundle()
    {
        var store = Store();
        var vm = Opened(store, "Delivery call");
        var meeting = vm.Meetings.Single();
        var light = Path.Combine(Folder("outbox"), "light" + MeetingBundle.Extension);
        Bundled(vm, meeting, light, audio: false);

        await meeting.ExportBundleCommand.ExecuteAsync(null);

        Assert.DoesNotContain("audio.wav", Names(light));
        Assert.Contains("transcript.md", Names(light));
        Assert.Contains("manifest.json", Names(light));

        var heavy = Path.Combine(Folder("outbox"), "heavy" + MeetingBundle.Extension);
        Bundled(vm, meeting, heavy, audio: true);
        await meeting.ExportBundleCommand.ExecuteAsync(null);
        Assert.Contains("audio.wav", Names(heavy));
    }

    [Fact]
    public async Task NothingIsWrittenUntilTheExportDialogIsAccepted()
    {
        var store = Store();
        var vm = Opened(store, "Delivery call");
        var meeting = vm.Meetings.Single();
        var target = Path.Combine(Folder("outbox"), "cancelled" + MeetingBundle.Extension);
        Bundled(vm, meeting, target, audio: false);
        vm.ConfirmExportBundle = _ => Task.FromResult<bool?>(null);

        await meeting.ExportBundleCommand.ExecuteAsync(null);

        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ASecondImportOfTheSameBundleOffersOnlySkipOrSaveAsNew()
    {
        // No third member: an overwrite button would promise merge semantics nothing implements,
        // and one misclick would take an hour of recording with it (decision 23).
        Assert.Equal(
            [BundleImportChoice.Skip, BundleImportChoice.SaveAsNew],
            Enum.GetValues<BundleImportChoice>());

        var store = Store();
        var vm = Opened(store, "Delivery call");
        var meeting = vm.Meetings.Single();
        var bundle = Path.Combine(Folder("outbox"), "delivery" + MeetingBundle.Extension);
        Bundled(vm, meeting, bundle, audio: false);
        await meeting.ExportBundleCommand.ExecuteAsync(null);
        vm.ChooseFileToImport = () => Task.FromResult<string?>(bundle);

        var asked = 0;
        vm.ChooseImportChoice = _ =>
        {
            asked++;
            return Task.FromResult(BundleImportChoice.Skip);
        };
        await vm.ImportBundleCommand.ExecuteAsync(null);

        Assert.Equal(1, asked);
        Assert.Single(vm.Meetings);

        vm.ChooseImportChoice = _ => Task.FromResult(BundleImportChoice.SaveAsNew);
        await vm.ImportBundleCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Meetings.Count);
        Assert.Equal(["Delivery call", "Delivery call (2)"], vm.Meetings.Select(m => m.Title).Order());
    }

    [Fact]
    public async Task ABundleFromAnotherWorkspaceArrivesAsItsOwnRecord()
    {
        var store = Store();
        var vm = Opened(store, "Delivery call");
        var meeting = vm.Meetings.Single();
        var bundle = Path.Combine(Folder("outbox"), "delivery" + MeetingBundle.Extension);
        Bundled(vm, meeting, bundle, audio: false);
        await meeting.ExportBundleCommand.ExecuteAsync(null);

        var elsewhere = new WorkspaceSidebarViewModel(store);
        var other = store.CreateWorkspace("Supplier", Folder("supplier")).Workspace!;
        elsewhere.Refresh();
        elsewhere.SelectedWorkspace = elsewhere.Workspaces.Single(w => w.Id == other.Id);
        elsewhere.ChooseFileToImport = () => Task.FromResult<string?>(bundle);

        await elsewhere.ImportBundleCommand.ExecuteAsync(null);

        var arrived = Assert.Single(elsewhere.Meetings);
        Assert.Equal(meeting.Id, arrived.Id);
        Assert.Equal("Delivery call", arrived.Title);
        Assert.Equal(["de", "zh"], arrived.Record.Languages);
        Assert.True(File.Exists(Path.Combine(elsewhere.FolderOf(arrived.Record)!, "transcript.md")));
    }

    private static IReadOnlyList<string> Names(string bundle)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(bundle);
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    [AvaloniaFact]
    public void TheExportDialogLeavesTheRecordingBehindUntilItIsTicked()
    {
        var window = new ExportBundleWindow("Delivery call");
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var audio = (CheckBox)Named(window, "IncludeAudio");
        Assert.False(audio.IsChecked);
        Assert.Equal(["Cancel", "Export"], Pressable(window));

        window.Close();
    }

    [AvaloniaFact]
    public void TheDuplicateImportDialogOffersNothingButSkipAndSaveAsNew()
    {
        var window = new ImportBundleWindow("Delivery call");
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["SaveAsNew", "Skip"], Pressable(window));

        window.Close();
    }

    // A CheckBox is a Button too, and it is not one of the ways out of the dialog.
    private static IEnumerable<string?> Pressable(Window window) =>
        window.GetLogicalDescendants().OfType<Button>()
            .Where(b => b is not ToggleButton).Select(b => b.Name).Order();
}
