using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Workspaces;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class MeetingFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-files-" + Guid.NewGuid().ToString("N"));

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

    private (WorkspaceStore Store, Workspace Workspace) Opened(params string[] titles)
    {
        var store = Store();
        var workspace = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        foreach (var title in titles)
            store.CreateMeeting(workspace.Id, title);
        return (store, workspace);
    }

    private string Dropped(string name, string text = "x")
    {
        var path = Path.Combine(Folder("inbox"), name);
        File.WriteAllText(path, text);
        return path;
    }

    private static MeetingRecord Meeting(WorkspaceStore store, Workspace workspace, string title) =>
        store.ListMeetings(workspace.Id).Meetings.Single(m => m.Title == title);

    private static IEnumerable<string> Names(MeetingFilesViewModel files) =>
        files.Entries.Select(e => e.Label);

    [Fact]
    public void TheTreeShowsWhatIsInTheMeetingFolder()
    {
        var (store, workspace) = Opened("Delivery call");
        var meeting = Meeting(store, workspace, "Delivery call");
        var folder = store.MeetingFolder(workspace.Id, meeting.Id)!;
        File.WriteAllText(Path.Combine(folder, "transcript.md"), "**S01** (de): Guten Tag");
        Directory.CreateDirectory(Path.Combine(folder, "attachments"));
        File.WriteAllText(Path.Combine(folder, "attachments", "KX-4402.pdf"), "drawing");

        var files = new MeetingFilesViewModel(store, () => Task.FromResult<string?>(null));
        files.Show(meeting);

        Assert.Equal(
            ["attachments/", "KX-4402.pdf", "meeting.json", "transcript.md"],
            Names(files));
        Assert.Equal([0, 1, 0, 0], files.Entries.Select(e => e.Depth));
        Assert.False(files.IsEmpty);
    }

    [Fact]
    public void WithNoMeetingChosenThereIsNothingToShowImportOrOpen()
    {
        var (store, _) = Opened();
        var files = new MeetingFilesViewModel(store, () => Task.FromResult<string?>(null));

        Assert.True(files.IsEmpty);
        Assert.False(files.HasMeeting);
        Assert.False(files.ImportCommand.CanExecute(null));
        Assert.False(files.OpenFolderCommand.CanExecute(null));
        Assert.Equal(Kanal.Host.Localization.Localizer.Instance["files.nomeeting"], files.EmptyNote);
    }

    [Fact]
    public async Task AnImportedFileLandsInAttachmentsAndAppearsInTheTree()
    {
        var (store, workspace) = Opened("Delivery call");
        var meeting = Meeting(store, workspace, "Delivery call");
        var files = new MeetingFilesViewModel(
            store, () => Task.FromResult<string?>(Dropped("quotation.pdf", "78 EUR")));
        files.Show(meeting);

        await files.ImportCommand.ExecuteAsync(null);

        var landed = Path.Combine(
            store.MeetingFolder(workspace.Id, meeting.Id)!, "attachments", "quotation.pdf");
        Assert.Equal("78 EUR", File.ReadAllText(landed));
        Assert.Equal(["attachments/", "quotation.pdf", "meeting.json"], Names(files));
        Assert.Equal("", files.ProblemNote);
    }

    [Fact]
    public async Task ImportingASecondFileOfTheSameNameKeepsTheFirst()
    {
        var (store, workspace) = Opened("Delivery call");
        var meeting = Meeting(store, workspace, "Delivery call");
        var files = new MeetingFilesViewModel(
            store, () => Task.FromResult<string?>(Dropped("quotation.pdf", "78 EUR")));
        files.Show(meeting);
        await files.ImportCommand.ExecuteAsync(null);

        var second = Path.Combine(Folder("other-inbox"), "quotation.pdf");
        File.WriteAllText(second, "91 EUR");
        var again = new MeetingFilesViewModel(store, () => Task.FromResult<string?>(second));
        again.Show(meeting);
        await again.ImportCommand.ExecuteAsync(null);

        var attachments = Path.Combine(store.MeetingFolder(workspace.Id, meeting.Id)!, "attachments");
        Assert.Equal("78 EUR", File.ReadAllText(Path.Combine(attachments, "quotation.pdf")));
        Assert.Equal("91 EUR", File.ReadAllText(Path.Combine(attachments, "quotation (2).pdf")));
    }

    [Fact]
    public async Task ChoosingNoFileLeavesTheFolderAsItWas()
    {
        var (store, workspace) = Opened("Delivery call");
        var meeting = Meeting(store, workspace, "Delivery call");
        var files = new MeetingFilesViewModel(store, () => Task.FromResult<string?>(null));
        files.Show(meeting);

        await files.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["meeting.json"], Names(files));
        Assert.False(Directory.Exists(
            Path.Combine(store.MeetingFolder(workspace.Id, meeting.Id)!, "attachments")));
    }

    [Fact]
    public void OpenFolderHandsTheOpenerTheFolderOfTheMeetingOnShow()
    {
        var (store, workspace) = Opened("Delivery call", "Tooling review");
        string? opened = null;
        var files = new MeetingFilesViewModel(
            store, () => Task.FromResult<string?>(null), openFolder: path => opened = path);
        files.Show(Meeting(store, workspace, "Delivery call"));
        var shown = Meeting(store, workspace, "Tooling review");
        files.Show(shown);

        files.OpenFolderCommand.Execute(null);

        Assert.Equal(store.MeetingFolder(workspace.Id, shown.Id), opened);
        Assert.Equal("", files.ProblemNote);
    }

    [Fact]
    public void AFolderDeletedOutsideKanalIsReportedInTheTabRatherThanRecreated()
    {
        var (store, workspace) = Opened("Delivery call");
        var meeting = Meeting(store, workspace, "Delivery call");
        var folder = store.MeetingFolder(workspace.Id, meeting.Id)!;
        string? opened = null;
        var files = new MeetingFilesViewModel(
            store, () => Task.FromResult<string?>(null), openFolder: path => opened = path);
        files.Show(meeting);
        Directory.Delete(folder, recursive: true);

        files.OpenFolderCommand.Execute(null);

        Assert.Null(opened);
        Assert.Equal(
            Kanal.Host.Localization.Localizer.Instance["workspace.folderunavailable"], files.ProblemNote);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void AMissingFolderNoteDoesNotFollowTheOperatorToTheNextMeeting()
    {
        var (store, workspace) = Opened("Delivery call", "Tooling review");
        var gone = Meeting(store, workspace, "Delivery call");
        var files = new MeetingFilesViewModel(
            store, () => Task.FromResult<string?>(null), openFolder: _ => { });
        files.Show(gone);
        Directory.Delete(store.MeetingFolder(workspace.Id, gone.Id)!, recursive: true);
        files.OpenFolderCommand.Execute(null);
        Assert.NotEqual("", files.ProblemNote);

        files.Show(Meeting(store, workspace, "Tooling review"));

        Assert.Equal("", files.ProblemNote);
    }

    [Fact]
    public async Task AFailedImportNoteDoesNotFollowTheOperatorToTheNextMeeting()
    {
        var (store, workspace) = Opened("Delivery call", "Tooling review");
        var missing = Path.Combine(_root, "never-written.pdf");
        var files = new MeetingFilesViewModel(store, () => Task.FromResult<string?>(missing));
        files.Show(Meeting(store, workspace, "Delivery call"));
        await files.ImportCommand.ExecuteAsync(null);
        Assert.NotEqual("", files.ProblemNote);

        files.Show(Meeting(store, workspace, "Tooling review"));

        Assert.Equal("", files.ProblemNote);
    }

    [AvaloniaFact]
    public void SwitchingMeetingSwitchesWhatTheFileTabShows()
    {
        var (store, workspace) = Opened("Delivery call", "Tooling review");
        foreach (var title in new[] { "Delivery call", "Tooling review" })
        {
            var record = Meeting(store, workspace, title);
            File.WriteAllText(
                Path.Combine(store.MeetingFolder(workspace.Id, record.Id)!, $"{title}.md"), title);
        }

        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Title == "Delivery call");
        Assert.Contains("Delivery call.md", Names(vm.Files));
        Assert.DoesNotContain("Tooling review.md", Names(vm.Files));

        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Title == "Tooling review");

        Assert.Contains("Tooling review.md", Names(vm.Files));
        Assert.DoesNotContain("Delivery call.md", Names(vm.Files));
    }

    [AvaloniaFact]
    public void TheFileTabImportsThroughTheSameChooserAsTheRestOfTheWorkspace()
    {
        var (store, workspace) = Opened("Delivery call");
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.Sidebar.ChooseFileToImport = () => Task.FromResult<string?>(Dropped("KX-4402.pdf", "drawing"));
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();

        vm.Files.ImportCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var meeting = Meeting(store, workspace, "Delivery call");
        Assert.True(File.Exists(Path.Combine(
            store.MeetingFolder(workspace.Id, meeting.Id)!, "attachments", "KX-4402.pdf")));
    }
}
