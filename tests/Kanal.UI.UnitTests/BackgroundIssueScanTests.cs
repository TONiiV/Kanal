using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Workspaces;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class BackgroundIssueScanTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-scan-" + Guid.NewGuid().ToString("N"));

    private WorkspaceStore Store() => new(Path.Combine(_root, "workspaces.json"));

    private Workspace Project(WorkspaceStore store, string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return store.CreateWorkspace(name, folder).Workspace!;
    }

    private static string UnfiledFolder(Workspace project, string name)
    {
        var path = Path.Combine(project.RootPath, "meetings", name);
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

    private sealed class HeldScans
    {
        private readonly List<(Action Scan, TaskCompletionSource Done)> _held = new();

        public int Count => _held.Count;

        public Task Hold(Action scan)
        {
            var done = new TaskCompletionSource();
            _held.Add((scan, done));
            return done.Task;
        }

        public void Finish(int index)
        {
            _held[index].Scan();
            _held[index].Done.SetResult();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void TheBannerOfTheCurrentProjectShowsBeforeTheScanOfTheOthersEnds()
    {
        var store = Store();
        var kappa = Project(store, "Kappa");
        var lambda = Project(store, "Lambda");
        UnfiledFolder(kappa, "a");
        UnfiledFolder(lambda, "b");
        var scans = new HeldScans();

        var vm = new WorkspaceSidebarViewModel(store, offUiThread: scans.Hold);

        Assert.Equal(kappa.Id, vm.SelectedWorkspace!.Id);
        Assert.True(vm.HasIssues);
        Assert.Single(vm.Issues);
        Assert.Equal([kappa.Id], vm.ProjectIssueStates.Keys);
        Assert.Equal(1, scans.Count);
    }

    [AvaloniaFact]
    public void TheDropDownMarksTheOtherProjectsWhenTheScanEnds()
    {
        var store = Store();
        var kappa = Project(store, "Kappa");
        var lambda = Project(store, "Lambda");
        var clean = Project(store, "Clean");
        var damaged = Project(store, "Damaged");
        UnfiledFolder(lambda, "b");
        File.WriteAllText(
            Path.Combine(UnfiledFolder(damaged, "c"), WorkspaceStore.MeetingFileName), "{ not json");
        var scans = new HeldScans();
        var vm = new WorkspaceSidebarViewModel(store, offUiThread: scans.Hold);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        scans.Finish(0);

        Assert.Equal(
            new Dictionary<string, bool> { [lambda.Id] = true, [damaged.Id] = true },
            vm.ProjectIssueStates.ToDictionary(p => p.Key, p => p.Value));
        Assert.DoesNotContain(clean.Id, vm.ProjectIssueStates.Keys);
        Assert.DoesNotContain(kappa.Id, vm.ProjectIssueStates.Keys);
        Assert.Contains(nameof(vm.ProjectIssueStates), changed);
        Assert.False(vm.HasIssues);
    }

    [AvaloniaFact]
    public void AScanThatEndsAfterANewerRefreshIsDiscarded()
    {
        var store = Store();
        Project(store, "Kappa");
        var lambda = Project(store, "Lambda");
        var folder = UnfiledFolder(lambda, "b");
        var scans = new HeldScans();
        var vm = new WorkspaceSidebarViewModel(store, offUiThread: scans.Hold);
        Directory.Delete(folder);
        vm.Refresh();

        scans.Finish(1);
        Assert.Empty(vm.ProjectIssueStates);

        UnfiledFolder(lambda, "b");
        scans.Finish(0);

        Assert.Empty(vm.ProjectIssueStates);
    }

    [AvaloniaFact]
    public void TheMarksOfTheLastScanStayUntilTheScanOfARefreshEnds()
    {
        var store = Store();
        Project(store, "Kappa");
        var lambda = Project(store, "Lambda");
        var folder = UnfiledFolder(lambda, "b");
        var scans = new HeldScans();
        var vm = new WorkspaceSidebarViewModel(store, offUiThread: scans.Hold);
        scans.Finish(0);
        Directory.Delete(folder);

        vm.Refresh();

        Assert.Equal([lambda.Id], vm.ProjectIssueStates.Keys);

        scans.Finish(1);

        Assert.Empty(vm.ProjectIssueStates);
    }

    [AvaloniaFact]
    public void AProjectWhoseFolderIsMissingIsMarkedAtOnceAndNeverScanned()
    {
        var store = Store();
        Project(store, "Kappa");
        var lambda = Project(store, "Lambda");
        Directory.Delete(lambda.RootPath, recursive: true);
        var scans = new HeldScans();

        var vm = new WorkspaceSidebarViewModel(store, offUiThread: scans.Hold);

        Assert.False(vm.ProjectIssueStates[lambda.Id]);
        var issue = Assert.Single(vm.IssuesOf(lambda));
        Assert.Equal(StoreProblemKind.FolderMissing, issue.Kind);
        Assert.Equal(0, scans.Count);
    }

    [AvaloniaFact]
    public void AScanThatFailsLeavesTheBannerAndTheMarksAsTheyWere()
    {
        var store = Store();
        var kappa = Project(store, "Kappa");
        var lambda = Project(store, "Lambda");
        UnfiledFolder(kappa, "a");
        UnfiledFolder(lambda, "b");

        var vm = new WorkspaceSidebarViewModel(
            store, offUiThread: _ => Task.FromException(new IOException("share lost")));
        Dispatcher.UIThread.RunJobs();

        Assert.Single(vm.Issues);
        Assert.Equal([kappa.Id], vm.ProjectIssueStates.Keys);
    }
}
