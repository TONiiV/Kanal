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

        Assert.Equal(["NewProject", "ImportRecord"], Reachable(flyout));

        window.Close();
    }

    [AvaloniaFact]
    public void EachMeetingCarriesItsOwnImportExportAndDelete()
    {
        var (window, _) = Shown();
        var sidebar = window.GetLogicalDescendants().OfType<WorkspaceSidebarView>().Single();

        var ellipsis = sidebar.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.Name == "MeetingMenu").Distinct().Single();

        Assert.Equal(
            ["ImportIntoMeeting", "ExportMeeting", "DeleteMeeting"], Reachable(Opened(ellipsis)));

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
}
