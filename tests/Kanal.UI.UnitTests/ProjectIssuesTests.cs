using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Core.Diagnostics;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class ProjectIssuesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-issues-" + Guid.NewGuid().ToString("N"));

    private string Registry => Path.Combine(_root, "workspaces.json");

    private WorkspaceStore Store() => new(Registry);

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

    private static Localizer L => Localizer.Instance;

    private static string EmptyMeetingFolder(Workspace project, string name)
    {
        var path = Path.Combine(project.RootPath, "meetings", name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string DamagedRecord(Workspace project, string name)
    {
        var folder = EmptyMeetingFolder(project, name);
        var file = Path.Combine(folder, WorkspaceStore.MeetingFileName);
        File.WriteAllText(file, "{ not json");
        return file;
    }

    private void DamageARowOfTheProjectList()
    {
        var json = JsonNode.Parse(File.ReadAllText(Registry))!.AsObject();
        json.Single(p => p.Key.Equals("workspaces", StringComparison.OrdinalIgnoreCase))
            .Value!.AsArray().Add(new JsonObject { ["id"] = "half-a-row" });
        File.WriteAllText(Registry, json.ToJsonString());
    }

    private static WorkspaceSidebarViewModel Opened(WorkspaceStore store, Workspace project)
    {
        var vm = new WorkspaceSidebarViewModel(store);
        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == project.Id);
        return vm;
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    [Fact]
    public void TheBannerCountsOnlyTheIssuesOfTheCurrentProject()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var lambda = store.CreateWorkspace("Lambda", Folder("lambda")).Workspace!;
        var clean = store.CreateWorkspace("Clean", Folder("clean")).Workspace!;
        EmptyMeetingFolder(kappa, "a");
        EmptyMeetingFolder(kappa, "b");
        EmptyMeetingFolder(lambda, "c");

        var vm = Opened(store, kappa);
        Assert.Equal(2, vm.Issues.Count);
        Assert.True(vm.HasIssues);
        Assert.Equal(L.Format("workspace.issues.many", 2), vm.IssueCountText);

        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == lambda.Id);
        Assert.Equal(Path.Combine(lambda.RootPath, "meetings", "c"), Assert.Single(vm.Issues).Subject);
        Assert.Equal(L["workspace.issues.one"], vm.IssueCountText);

        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == clean.Id);
        Assert.Empty(vm.Issues);
        Assert.False(vm.HasIssues);
    }

    [Fact]
    public void EveryProjectWithIssuesIsMarkedEvenWhenItIsNotTheCurrentOne()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var lambda = store.CreateWorkspace("Lambda", Folder("lambda")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        EmptyMeetingFolder(lambda, "c");
        Directory.Delete(gone.RootPath, recursive: true);

        var vm = Opened(store, kappa);

        Assert.Equal(
            new[] { gone.Id, lambda.Id }.Order(StringComparer.Ordinal),
            vm.ProjectIssueStates.Keys.Order(StringComparer.Ordinal));
        Assert.False(vm.HasIssues);
        Assert.True(vm.ProjectIssueStates[lambda.Id]);
        Assert.False(vm.ProjectIssueStates[gone.Id]);
    }

    [Fact]
    public void AMissingProjectFolderBelongsToThatProjectAndNamesIt()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var gone = store.CreateWorkspace("Lambda", Folder("gone")).Workspace!;
        Directory.Delete(gone.RootPath, recursive: true);

        var vm = Opened(store, kappa);
        Assert.Empty(vm.Issues);

        var issue = Assert.Single(vm.IssuesOf(vm.Workspaces.Single(w => w.Id == gone.Id)));
        Assert.Equal(gone.RootPath, issue.Subject);
        Assert.Equal(L.Format("workspace.issues.projectmissing", "Lambda"), issue.Cause);
        Assert.False(issue.IsAlarm);
    }

    [Fact]
    public void TheCurrentProjectWhoseFolderIsGoneIsCountedOnce()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var vm = Opened(store, kappa);
        Directory.Delete(kappa.RootPath, recursive: true);

        vm.Refresh();

        Assert.Equal(kappa.RootPath, Assert.Single(vm.Issues).Subject);
        Assert.False(vm.IssuesAreAlarm);
    }

    [Fact]
    public void ADamagedRecordRaisesTheAlarmAndAMissingFolderOnlyAWarning()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var vm = Opened(store, kappa);
        Directory.Delete(Path.Combine(kappa.RootPath, "meetings"), recursive: true);
        vm.Refresh();
        Assert.False(vm.IssuesAreAlarm);
        Assert.Equal(L["workspace.issues.meetingsmissing"], Assert.Single(vm.Issues).Cause);

        Directory.CreateDirectory(Path.Combine(kappa.RootPath, "meetings"));
        var file = DamagedRecord(kappa, "damaged");
        vm.Refresh();

        var issue = Assert.Single(vm.Issues);
        Assert.True(vm.IssuesAreAlarm);
        Assert.True(issue.IsAlarm);
        Assert.Equal(StoreProblemKind.RecordDamaged, issue.Kind);
        Assert.Equal(Path.GetDirectoryName(file), issue.Subject);
        Assert.Equal(Path.GetDirectoryName(file), issue.Location);
        Assert.Equal(L["workspace.issues.damaged"], issue.Cause);
    }

    [Fact]
    public void ADamagedRowOfTheProjectListBelongsToTheCurrentProject()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var lambda = store.CreateWorkspace("Lambda", Folder("lambda")).Workspace!;
        var vm = Opened(store, kappa);
        DamageARowOfTheProjectList();

        vm.Refresh();

        var issue = Assert.Single(vm.Issues);
        Assert.Equal(Registry, issue.Subject);
        Assert.Equal(L["workspace.issues.projectlist"], issue.Cause);
        Assert.Equal(Registry, issue.Location);
        Assert.Contains(kappa.Id, vm.ProjectIssueStates.Keys);

        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == lambda.Id);
        Assert.Equal(Registry, Assert.Single(vm.Issues).Subject);
        Assert.Contains(lambda.Id, vm.ProjectIssueStates.Keys);
        Assert.DoesNotContain(kappa.Id, vm.ProjectIssueStates.Keys);
        Assert.Equal("", vm.ProblemNote);
    }

    [AvaloniaFact]
    public void AProjectListThatCannotBeReadAtAllIsNamedInTheNoteBecauseNoSettingsCanOpen()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Registry, "{ not json");
        var before = L.Current;
        try
        {
            L.Current = "en";
            var vm = new WorkspaceSidebarViewModel(Store());

            var issue = Assert.Single(vm.Issues);
            Assert.Equal(L["workspace.issues.projectlistunreadable"], issue.Cause);
            Assert.False(vm.ShowIssuesCommand.CanExecute(null));
            Assert.Contains(L["workspace.issues.projectlistunreadable"], vm.ProblemNote);
            Assert.Contains(Registry, vm.ProblemNote);

            L.Current = "de";
            Assert.Contains(L["workspace.issues.projectlistunreadable"], vm.ProblemNote);

            vm.ProblemNote = "left over from an earlier failure";
            L.Current = "pl";
            Assert.Equal("left over from an earlier failure", vm.ProblemNote);
        }
        finally
        {
            L.Current = before;
        }
    }

    [Fact]
    public void AMeetingFolderWithoutARecordIsNamedAsSuch()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var folder = EmptyMeetingFolder(kappa, "a");

        var issue = Assert.Single(Opened(store, kappa).Issues);

        Assert.Equal(StoreProblemKind.RecordMissing, issue.Kind);
        Assert.Equal(folder, issue.Location);
        Assert.Equal(L["workspace.issues.norecord"], issue.Cause);
        Assert.True(issue.IsAlarm);
    }

    [Fact]
    public void ACopiedMeetingFolderIsNamedAsACopy()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var meeting = store.CreateMeeting(kappa.Id, "Tooling review").Meeting!;
        var copy = EmptyMeetingFolder(kappa, "copy");
        File.Copy(
            Path.Combine(store.MeetingFolder(kappa.Id, meeting.Id)!, WorkspaceStore.MeetingFileName),
            Path.Combine(copy, WorkspaceStore.MeetingFileName));

        var issue = Assert.Single(Opened(store, kappa).Issues);

        Assert.Equal(StoreProblemKind.Misfiled, issue.Kind);
        Assert.Equal(copy, issue.Location);
        Assert.Equal(L["workspace.issues.misfiled"], issue.Cause);
    }

    [Fact]
    public void AListWhoseOnlyRowIsDamagedNamesTheRowAndNotTheWholeList()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Registry, """{ "schemaVersion": 1, "workspaces": [ { "id": "half-a-row" } ] }""");

        var issue = Assert.Single(new WorkspaceSidebarViewModel(Store()).Issues);

        Assert.Equal(StoreProblemKind.RegistryRowDamaged, issue.Kind);
        Assert.Equal(L["workspace.issues.projectlist"], issue.Cause);
    }

    [Fact]
    public void AnIssueOfAKindThisVersionDoesNotKnowGetsTheGenericCause()
    {
        var issue = ProjectIssueViewModel.For(
            new StoreProblem((StoreProblemKind)999, "/somewhere", "later kind"), owner: null);

        Assert.Equal(L["workspace.issues.other"], issue.Cause);
        Assert.True(issue.IsAlarm);
    }

    [Fact]
    public void ARecordInAFormatThisVersionCannotReadIsExplained()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var folder = EmptyMeetingFolder(kappa, "future");
        File.WriteAllText(Path.Combine(folder, WorkspaceStore.MeetingFileName), """{ "schemaVersion": 9 }""");

        var vm = Opened(store, kappa);

        Assert.Equal(L["workspace.issues.format"], Assert.Single(vm.Issues).Cause);
    }

    [Fact]
    public void ListingIssuesLeavesTheNoteForOtherActionsEmpty()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        EmptyMeetingFolder(kappa, "a");

        var vm = Opened(store, kappa);

        Assert.Single(vm.Issues);
        Assert.Equal("", vm.ProblemNote);
    }

    [Fact]
    public void EachIssueIsLoggedOncePerListing()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var lambda = store.CreateWorkspace("Lambda", Folder("lambda")).Workspace!;
        var mine = EmptyMeetingFolder(kappa, "a");
        var theirs = EmptyMeetingFolder(lambda, "b");
        var vm = Opened(store, kappa);
        var previous = Log.Sink;
        var sink = new RecordingSink();
        Log.Install(sink);
        try
        {
            vm.Refresh();
        }
        finally
        {
            Log.Install(previous);
        }

        Assert.Single(sink.Lines, line => line.Contains(mine, StringComparison.Ordinal));
        Assert.Single(sink.Lines, line => line.Contains(theirs, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void TheCausesAndTheCountFollowALanguageSwitch()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        DamagedRecord(kappa, "damaged");
        var before = L.Current;
        try
        {
            L.Current = "en";
            var vm = Opened(store, kappa);
            var issue = Assert.Single(vm.Issues);
            Assert.Equal(L["workspace.issues.damaged"], issue.Cause);
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var rowRaised = new List<string?>();
            issue.PropertyChanged += (_, e) => rowRaised.Add(e.PropertyName);

            L.Current = "de";
            Assert.Contains(nameof(issue.Cause), rowRaised);

            Assert.Equal(L["workspace.issues.damaged"], issue.Cause);
            Assert.Equal(L["workspace.issues.one"], vm.IssueCountText);
            Assert.Contains(nameof(vm.IssueCountText), raised);
        }
        finally
        {
            L.Current = before;
        }
    }

    [Fact]
    public async Task DetailsOpensTheSettingsOfTheCurrentProjectAtItsIssues()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        store.CreateWorkspace("Lambda", Folder("lambda"));
        var subject = EmptyMeetingFolder(kappa, "a");
        var vm = Opened(store, kappa);
        ProjectSettingsViewModel? shown = null;
        vm.ShowProjectSettings = settings =>
        {
            shown = settings;
            return Task.CompletedTask;
        };

        await vm.ShowIssuesCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal(kappa.Id, shown.Project.Id);
        Assert.True(shown.OpenAtIssues);
        Assert.True(shown.HasIssues);
        Assert.Equal(subject, Assert.Single(shown.Issues).Subject);
    }

    [Fact]
    public async Task TheGearOpensTheSettingsOfAnotherProjectWithItsOwnIssues()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var lambda = store.CreateWorkspace("Lambda", Folder("lambda")).Workspace!;
        EmptyMeetingFolder(kappa, "a");
        var vm = Opened(store, kappa);
        ProjectSettingsViewModel? shown = null;
        vm.ShowProjectSettings = settings =>
        {
            shown = settings;
            return Task.CompletedTask;
        };

        await vm.OpenProjectSettingsCommand.ExecuteAsync(vm.Workspaces.Single(w => w.Id == lambda.Id));

        Assert.NotNull(shown);
        Assert.False(shown.HasIssues);
        Assert.False(shown.OpenAtIssues);
    }

    [Fact]
    public void WithNoProjectThereAreNoDetailsToOpen()
    {
        var vm = new WorkspaceSidebarViewModel(Store());

        Assert.False(vm.ShowIssuesCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void TheBannerShowsTheCountAndHidesWhenTheProjectHasNoIssues()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var clean = store.CreateWorkspace("Clean", Folder("clean")).Workspace!;
        EmptyMeetingFolder(kappa, "a");
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.Sidebar.SelectedWorkspace = vm.Sidebar.Workspaces.Single(w => w.Id == kappa.Id);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var banner = Named<Border>(window, "IssuesBanner");
        Assert.True(banner.IsEffectivelyVisible);
        Assert.Equal(L["workspace.issues.one"], Named<TextBlock>(window, "IssuesCount").Text);
        Assert.Contains("alarm", banner.Classes);
        Assert.False(Named<TextBlock>(window, "WorkspaceProblem").IsVisible);

        vm.Sidebar.SelectedWorkspace = vm.Sidebar.Workspaces.Single(w => w.Id == clean.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.False(banner.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void TheDropDownMarksOnlyTheProjectsWithIssues()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var lambda = store.CreateWorkspace("Lambda", Folder("lambda")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        EmptyMeetingFolder(lambda, "c");
        Directory.Delete(gone.RootPath, recursive: true);
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.Sidebar.SelectedWorkspace = vm.Sidebar.Workspaces.Single(w => w.Id == kappa.Id);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var picker = Named<ComboBox>(window, "WorkspacePicker");
        picker.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        var marks = picker.GetLogicalDescendants().OfType<Control>()
            .Where(c => c.Name == "ProjectIssueMark").ToList();

        Control Mark(Workspace project) => marks.Single(m => ((Workspace)m.DataContext!).Id == project.Id);
        Assert.Equal(3, marks.Count);
        Assert.True(Mark(lambda).IsVisible);
        Assert.Contains("alarm", Mark(lambda).Classes);
        Assert.True(Mark(gone).IsVisible);
        Assert.DoesNotContain("alarm", Mark(gone).Classes);
        Assert.False(Mark(kappa).IsVisible);
        Assert.All(marks, mark => Assert.False(string.IsNullOrWhiteSpace(
            Avalonia.Automation.AutomationProperties.GetName(mark))));

        picker.IsDropDownOpen = false;
        window.Close();
    }

    [AvaloniaFact]
    public void TheIssuesSectionIsHiddenForAProjectWithoutIssues()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var window = new ProjectSettingsWindow(new ProjectSettingsViewModel(store, kappa, recording: false));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(Named<StackPanel>(window, "IssuesSection").IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void TheIssuesSectionListsEachCauseWithItsSelectablePathAboveTheDangerZone()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var file = DamagedRecord(kappa, "damaged");
        var sidebar = Opened(store, kappa);
        var window = new ProjectSettingsWindow(new ProjectSettingsViewModel(
            store, kappa, recording: false, sidebar.IssuesOf(kappa)));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var section = Named<StackPanel>(window, "IssuesSection");
        Assert.True(section.IsVisible);
        var path = section.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "IssuePath");
        Assert.Equal(Path.GetDirectoryName(file), path.Text);
        var cause = section.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "IssueCause");
        Assert.Equal(L["workspace.issues.damaged"], cause.Text);

        var page = Assert.IsType<StackPanel>(section.Parent);
        Assert.True(page.Children.IndexOf(section) < page.Children.IndexOf(Named<TextBlock>(window, "DangerLabel")));

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyTheWindowOpenedAtItsIssuesScrollsToThem(bool openAtIssues)
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        foreach (var name in Enumerable.Range(0, 30).Select(i => $"broken-{i:00}"))
            EmptyMeetingFolder(kappa, name);
        var sidebar = Opened(store, kappa);
        var window = new ProjectSettingsWindow(new ProjectSettingsViewModel(
            store, kappa, recording: false, sidebar.IssuesOf(kappa), openAtIssues));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroller = Named<ScrollViewer>(window, "SettingsScroller");
        Assert.Equal(openAtIssues, scroller.Offset.Y > 0);

        window.Close();
    }

    [Theory]
    [InlineData(728, 1.0, 680)]
    [InlineData(1456, 2.0, 680)]
    [InlineData(728, 2.0, 316)]
    [InlineData(1040, 1.0, 720)]
    public void TheSettingsWindowNeverGrowsPastTheWorkArea(int workAreaPixels, double scaling, double expected) =>
        Assert.Equal(expected, ProjectSettingsWindow.FittedMaxHeight(720, workAreaPixels, scaling));

    private sealed class RecordingSink : ILogSink
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }

        public void Write(LogLevel level, string category, string message, Exception? error)
        {
            lock (_lines)
                _lines.Add($"{category}: {message}");
        }
    }
}
