using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class ResolveIssuesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 12, 0, TimeSpan.FromHours(2));

    private static readonly DateTime MeetingEnded = new(2026, 10, 9, 7, 30, 4, DateTimeKind.Utc);

    private static string LocalMinute(DateTime utc) =>
        new DateTimeOffset(utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static readonly string RecoveredTitleTime = LocalMinute(MeetingEnded.AddSeconds(-4));

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-resolve-" + Guid.NewGuid().ToString("N"));

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

    private static string MeetingFolder(Workspace project, string name)
    {
        var path = Path.Combine(project.RootPath, "meetings", name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string DamagedMeeting(Workspace project, string name, DateTime? ended = null)
    {
        var folder = MeetingFolder(project, name);
        var transcript = Path.Combine(folder, TranscriptLog.FileName);
        using (var writer = new TranscriptLogWriter(transcript, _ => { }))
            writer.Append(new Utterance(
                "u1", "S1", 0, 4000, "zh", "你好", 1, UtteranceState.Final, false, 1.0,
                new Dictionary<string, string> { ["de"] = "Hallo" }));
        File.SetLastWriteTimeUtc(transcript, ended ?? MeetingEnded);
        File.WriteAllText(Path.Combine(folder, WorkspaceStore.MeetingFileName), "{ not json");
        return folder;
    }

    private static string CopiedMeeting(WorkspaceStore store, Workspace project, string original, string copyName)
    {
        var meeting = store.CreateMeeting(project.Id, original).Meeting!;
        var copy = MeetingFolder(project, copyName);
        File.Copy(
            Path.Combine(store.MeetingFolder(project.Id, meeting.Id)!, WorkspaceStore.MeetingFileName),
            Path.Combine(copy, WorkspaceStore.MeetingFileName));
        return copy;
    }

    private void DamageARowOfTheProjectList()
    {
        var json = JsonNode.Parse(File.ReadAllText(Registry))!.AsObject();
        json.Single(p => p.Key.Equals("workspaces", StringComparison.OrdinalIgnoreCase))
            .Value!.AsArray().Add(new JsonObject { ["id"] = "half-a-row" });
        File.WriteAllText(Registry, json.ToJsonString());
    }

    // The store resolves links in a root, and on macOS the temp folder under /var is one.
    private static string CanonicalBeside(Workspace sibling, params string[] parts) =>
        Path.Combine([Path.GetDirectoryName(sibling.RootPath)!, .. parts]);

    private static WorkspaceSidebarViewModel Opened(WorkspaceStore store, Workspace project)
    {
        var vm = new WorkspaceSidebarViewModel(store, clock: () => Now);
        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == project.Id);
        return vm;
    }

    private static async Task<ProjectSettingsViewModel> Settings(
        WorkspaceSidebarViewModel vm, Workspace project, Func<ProjectSettingsViewModel, Task> act)
    {
        ProjectSettingsViewModel? shown = null;
        vm.ShowProjectSettings = async settings =>
        {
            shown = settings;
            await act(settings);
        };

        await vm.OpenProjectSettingsCommand.ExecuteAsync(vm.Workspaces.Single(w => w.Id == project.Id));
        return Assert.IsType<ProjectSettingsViewModel>(shown);
    }

    private static Task<ProjectSettingsViewModel> Resolved(WorkspaceSidebarViewModel vm, Workspace project) =>
        Settings(vm, project, settings =>
        {
            settings.ResolveCommand.Execute(settings.Issues.Single(i => i.ShowsResolve));
            return Task.CompletedTask;
        });

    [Fact]
    public async Task ResolvingADamagedRecordRecoversItUnderALocalizedTitle()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var folder = DamagedMeeting(kappa, "7f3a0000000000a1");
        var vm = Opened(store, kappa);
        Assert.Single(vm.Issues);

        var settings = await Resolved(vm, kappa);

        var title = L.Format("workspace.issues.recoveredtitle", RecoveredTitleTime);
        Assert.Empty(vm.Issues);
        Assert.False(vm.HasIssues);
        Assert.Equal(title, Assert.Single(vm.Meetings).Title);
        var row = Assert.Single(settings.Issues);
        Assert.True(row.IsResolved);
        Assert.False(row.IsRefused);
        Assert.False(row.ShowsResolve);
        Assert.Equal(L.Format("workspace.issues.recovered", title), row.Outcome);
        Assert.True(File.Exists(Path.Combine(folder, WorkspaceStore.MeetingFileName + ".damaged")));
    }

    [Fact]
    public async Task TwoMeetingsRecoveredInOneMinuteAreTitledByWhenTheyHappened()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var morning = new DateTime(2026, 10, 8, 7, 0, 4, DateTimeKind.Utc);
        var afternoon = new DateTime(2026, 10, 8, 13, 45, 4, DateTimeKind.Utc);
        DamagedMeeting(kappa, "7f3a0000000000b1", morning);
        DamagedMeeting(kappa, "7f3a0000000000b2", afternoon);
        var vm = Opened(store, kappa);

        await Settings(vm, kappa, settings =>
        {
            foreach (var issue in settings.Issues.ToList())
                settings.ResolveCommand.Execute(issue);
            return Task.CompletedTask;
        });

        Assert.Equal(
            new[] { morning, afternoon }
                .Select(t => L.Format("workspace.issues.recoveredtitle", LocalMinute(t.AddSeconds(-4))))
                .Order(),
            vm.Meetings.Select(m => m.Title).Order());
    }

    [Fact]
    public async Task AFolderThatCannotBeRebuiltIsMovedAsideAndTheRowSaysWhere()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        MeetingFolder(kappa, "7f3a0000000000a2");
        var vm = Opened(store, kappa);

        var settings = await Resolved(vm, kappa);

        var moved = Path.Combine("kanal-set-aside", "7f3a0000000000a2");
        Assert.True(Directory.Exists(Path.Combine(kappa.RootPath, moved)));
        Assert.Empty(vm.Issues);
        Assert.Equal(L.Format("workspace.issues.setaside", moved), Assert.Single(settings.Issues).Outcome);
    }

    [Fact]
    public async Task ResolvingACopiedFolderListsItAsASeparateMeeting()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        CopiedMeeting(store, kappa, "Tooling review", "7f3a0000000000a3");
        var vm = Opened(store, kappa);

        var settings = await Resolved(vm, kappa);

        Assert.Empty(vm.Issues);
        Assert.Equal(["Tooling review", "Tooling review 2"], vm.Meetings.Select(m => m.Title).Order());
        Assert.Equal(
            L.Format("workspace.issues.reidentified", "Tooling review 2"), Assert.Single(settings.Issues).Outcome);
    }

    [Fact]
    public async Task ResolvingAMissingMeetingsFolderCreatesIt()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        Directory.Delete(Path.Combine(kappa.RootPath, "meetings"), recursive: true);
        var vm = Opened(store, kappa);
        Assert.Single(vm.Issues);

        var settings = await Resolved(vm, kappa);

        Assert.True(Directory.Exists(Path.Combine(kappa.RootPath, "meetings")));
        Assert.False(vm.HasIssues);
        Assert.Equal(L["workspace.issues.meetingscreated"], Assert.Single(settings.Issues).Outcome);
    }

    [Fact]
    public async Task ResolvingADamagedRowOfTheProjectListRemovesOnlyThatRow()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        store.CreateWorkspace("Lambda", Folder("lambda"));
        DamageARowOfTheProjectList();
        var vm = Opened(store, kappa);
        Assert.Single(vm.Issues);

        var settings = await Resolved(vm, kappa);

        Assert.Empty(vm.Issues);
        Assert.Empty(store.ListWorkspaces().Problems);
        Assert.Equal(2, vm.Workspaces.Count);
        Assert.Equal(L["workspace.issues.rowsremoved"], Assert.Single(settings.Issues).Outcome);
    }

    [Fact]
    public async Task OneResolvedIssueDropsTheBannerCountByOne()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        MeetingFolder(kappa, "7f3a0000000000a4");
        MeetingFolder(kappa, "7f3a0000000000a5");
        var vm = Opened(store, kappa);
        Assert.Equal(L.Format("workspace.issues.many", 2), vm.IssueCountText);

        var settings = await Settings(vm, kappa, settings =>
        {
            settings.ResolveCommand.Execute(settings.Issues[0]);
            return Task.CompletedTask;
        });

        Assert.Equal(L["workspace.issues.one"], vm.IssueCountText);
        Assert.Equal(2, settings.Issues.Count);
        Assert.True(settings.Issues[0].IsResolved);
        Assert.True(settings.Issues[1].ShowsResolve);
    }

    [Fact]
    public async Task ChoosingTheNewFolderOfAMissingProjectPointsTheProjectThere()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        var moved = Folder("moved");
        Directory.Move(gone.RootPath, Path.Combine(moved, "gone"));
        var target = Path.Combine(moved, "gone");
        var vm = Opened(store, kappa);
        Assert.Contains(gone.Id, vm.ProjectIssueStates.Keys);

        var settings = await Settings(vm, gone, async settings =>
        {
            var issue = Assert.Single(settings.Issues);
            Assert.True(issue.ShowsRelocate);
            Assert.False(issue.ShowsResolve);
            settings.ChooseProjectFolder = () => Task.FromResult<string?>(target);
            await settings.RelocateCommand.ExecuteAsync(issue);
        });

        var stored = CanonicalBeside(kappa, "moved", "gone");
        Assert.DoesNotContain(gone.Id, vm.ProjectIssueStates.Keys);
        Assert.Equal(stored, vm.Workspaces.Single(w => w.Id == gone.Id).RootPath);
        var row = Assert.Single(settings.Issues);
        Assert.True(row.IsResolved);
        Assert.Equal(L.Format("workspace.issues.relocated", stored), row.Outcome);
    }

    [Fact]
    public async Task ARelocatedProjectKeepsItsIconFileOnSave()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        var image = Path.Combine(Folder("pictures"), "logo.png");
        File.WriteAllBytes(image, new byte[16]);
        store.SetWorkspaceIconFile(gone.Id, image);
        var target = Path.Combine(Folder("moved"), "gone");
        Directory.Move(gone.RootPath, target);
        var vm = Opened(store, kappa);

        var settings = await Settings(vm, gone, async settings =>
        {
            settings.ChooseProjectFolder = () => Task.FromResult<string?>(target);
            await settings.RelocateCommand.ExecuteAsync(settings.Issues.Single());
            settings.SaveCommand.Execute(null);
        });

        Assert.Equal("", settings.Problem);
        Assert.Equal(Path.Combine(CanonicalBeside(kappa, "moved", "gone"), "kanal-icon.png"), settings.ImagePath);
        Assert.Equal("kanal-icon.png", store.ListWorkspaces().Workspaces.Single(w => w.Id == gone.Id).IconFile);
    }

    [Fact]
    public async Task NoProjectLeavesTheListWhileAnotherRecords()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        var recorded = store.CreateMeeting(kappa.Id, "Delivery call").Meeting!;
        Directory.Delete(gone.RootPath, recursive: true);
        var vm = Opened(store, kappa);
        vm.RecordingMeetingId = recorded.Id;

        await Settings(vm, gone, async settings =>
        {
            var issue = settings.Issues.Single();
            Assert.True(settings.RelocateCommand.CanExecute(issue));
            Assert.False(settings.RemoveFromListCommand.CanExecute(issue));
            settings.ConfirmListRemoval = _ => Task.FromResult(true);
            await settings.RemoveFromListCommand.ExecuteAsync(issue);
        });

        Assert.Equal(2, store.ListWorkspaces().Workspaces.Count);
    }

    [Fact]
    public async Task AFolderOfAnotherProjectIsRefusedWithItsReasonInTheRow()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        var other = Folder("other");
        new WorkspaceStore(Path.Combine(_root, "elsewhere.json")).CreateWorkspace("Other", other);
        Directory.Delete(gone.RootPath, recursive: true);
        var vm = Opened(store, kappa);

        var settings = await Settings(vm, gone, async settings =>
        {
            settings.ChooseProjectFolder = () => Task.FromResult<string?>(other);
            await settings.RelocateCommand.ExecuteAsync(settings.Issues.Single());
        });

        var row = Assert.Single(settings.Issues);
        Assert.True(row.IsRefused);
        Assert.False(row.IsResolved);
        Assert.True(row.ShowsRelocate);
        Assert.Equal(L["workspace.issues.refused.otherproject"], row.Outcome);
        Assert.Contains(gone.Id, vm.ProjectIssueStates.Keys);
        Assert.Equal(gone.RootPath, store.ListWorkspaces().Workspaces.Single(w => w.Id == gone.Id).RootPath);
    }

    [Fact]
    public async Task AFolderThatCameBackIsNotReplacedByAnother()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        var aside = Path.Combine(_root, "gone-unplugged");
        Directory.Move(gone.RootPath, aside);
        var vm = Opened(store, kappa);
        var elsewhere = Folder("elsewhere");

        var settings = await Settings(vm, gone, async settings =>
        {
            Directory.Move(aside, gone.RootPath);
            settings.ChooseProjectFolder = () => Task.FromResult<string?>(elsewhere);
            await settings.RelocateCommand.ExecuteAsync(settings.Issues.Single());
            settings.ConfirmListRemoval = _ => Task.FromResult(true);
            await settings.RemoveFromListCommand.ExecuteAsync(settings.Issues.Single());
        });

        var row = Assert.Single(settings.Issues);
        Assert.True(row.IsRefused);
        Assert.False(row.ShowsRelocate);
        Assert.Equal(L["workspace.issues.refused.oldfolder"], row.Outcome);
        Assert.Equal(2, store.ListWorkspaces().Workspaces.Count);
        Assert.Equal(gone.RootPath, store.ListWorkspaces().Workspaces.Single(w => w.Id == gone.Id).RootPath);
    }

    [Fact]
    public async Task ACancelledFolderChoiceChangesNothing()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        Directory.Delete(gone.RootPath, recursive: true);
        var vm = Opened(store, kappa);

        var settings = await Settings(vm, gone, async settings =>
        {
            await settings.RelocateCommand.ExecuteAsync(settings.Issues.Single());
            settings.ChooseProjectFolder = () => Task.FromResult<string?>(null);
            await settings.RelocateCommand.ExecuteAsync(settings.Issues.Single());
        });

        Assert.Equal("", Assert.Single(settings.Issues).Outcome);
        Assert.Contains(gone.Id, vm.ProjectIssueStates.Keys);
    }

    [Fact]
    public async Task AFailedResolveShowsItsReasonAndKeepsTheIssue()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var meetings = Path.Combine(kappa.RootPath, "meetings");
        Directory.Delete(meetings, recursive: true);
        File.WriteAllText(meetings, "a file where the folder belongs");
        var vm = Opened(store, kappa);

        var settings = await Resolved(vm, kappa);

        var row = Assert.Single(settings.Issues);
        Assert.True(row.IsRefused);
        Assert.True(row.ShowsResolve);
        Assert.Equal(L["workspace.issues.refused.unwritable"], row.Outcome);
        Assert.Single(vm.Issues);
        Assert.Equal("a file where the folder belongs", File.ReadAllText(meetings));
    }

    [Fact]
    public async Task RemovingAMissingProjectFromTheListAsksFirstAndKeepsEveryFile()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        Directory.Delete(gone.RootPath, recursive: true);
        var vm = Opened(store, kappa);
        string? asked = null;
        var finished = false;

        await Settings(vm, gone, async settings =>
        {
            settings.Finished += () => finished = true;
            await settings.RemoveFromListCommand.ExecuteAsync(settings.Issues.Single());
            Assert.Equal(2, store.ListWorkspaces().Workspaces.Count);

            settings.ConfirmListRemoval = name =>
            {
                asked = name;
                return Task.FromResult(false);
            };
            await settings.RemoveFromListCommand.ExecuteAsync(settings.Issues.Single());
            Assert.Equal(2, store.ListWorkspaces().Workspaces.Count);

            settings.ConfirmListRemoval = _ => Task.FromResult(true);
            await settings.RemoveFromListCommand.ExecuteAsync(settings.Issues.Single());
        });

        Assert.Equal("Gone", asked);
        Assert.True(finished);
        Assert.Equal(kappa.Id, Assert.Single(vm.Workspaces).Id);
        Assert.Empty(vm.ProjectIssueStates);
        Assert.True(Directory.Exists(kappa.RootPath));
    }

    [Fact]
    public async Task NoActionIsOfferedForTheProjectThatRecords()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var recorded = store.CreateMeeting(kappa.Id, "Delivery call").Meeting!;
        var folder = MeetingFolder(kappa, "7f3a0000000000a6");
        var vm = Opened(store, kappa);
        vm.RecordingMeetingId = recorded.Id;

        var settings = await Settings(vm, kappa, settings =>
        {
            var issue = settings.Issues.Single();
            Assert.False(settings.CanResolve);
            Assert.False(issue.ShowsResolve);
            Assert.False(settings.ResolveCommand.CanExecute(issue));
            settings.ResolveCommand.Execute(issue);
            return Task.CompletedTask;
        });

        Assert.Equal(L["workspace.issues.recording"], settings.ResolveBlockedNote);
        Assert.True(Directory.Exists(folder));
        Assert.Single(vm.Issues);
    }

    [Fact]
    public async Task TheRecordingProjectOffersNoActionAfterTheOperatorSwitchesAway()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var lambda = store.CreateWorkspace("Lambda", Folder("lambda")).Workspace!;
        var recorded = store.CreateMeeting(kappa.Id, "Delivery call").Meeting!;
        MeetingFolder(kappa, "7f3a0000000000c1");
        var vm = Opened(store, kappa);
        vm.RecordingMeetingId = recorded.Id;
        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == lambda.Id);

        var settings = await Settings(vm, kappa, _ => Task.CompletedTask);

        Assert.False(settings.CanResolve);
        Assert.False(Assert.Single(settings.Issues).ShowsResolve);
    }

    [Fact]
    public async Task AnotherProjectCanBeRepairedWhileOneRecords()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var lambda = store.CreateWorkspace("Lambda", Folder("lambda")).Workspace!;
        var recorded = store.CreateMeeting(kappa.Id, "Delivery call").Meeting!;
        Directory.Delete(Path.Combine(lambda.RootPath, "meetings"), recursive: true);
        var vm = Opened(store, kappa);
        vm.RecordingMeetingId = recorded.Id;
        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == lambda.Id);

        var settings = await Resolved(vm, lambda);

        Assert.True(settings.CanResolve);
        Assert.True(Directory.Exists(Path.Combine(lambda.RootPath, "meetings")));
    }

    [Fact]
    public async Task ARecordThatCannotBeOpenedNowIsExplainedAndOffersNoAction()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var meeting = store.CreateMeeting(kappa.Id, "Delivery call").Meeting!;
        var folder = store.MeetingFolder(kappa.Id, meeting.Id)!;

        ProjectSettingsViewModel settings;
        using (new FileStream(
            Path.Combine(folder, WorkspaceStore.MeetingFileName), FileMode.Open, FileAccess.Read, FileShare.None))
            settings = await Settings(Opened(store, kappa), kappa, _ => Task.CompletedTask);

        var issue = Assert.Single(settings.Issues);
        Assert.Equal(StoreProblemKind.Unreadable, issue.Kind);
        Assert.Equal(folder, issue.Location);
        Assert.Equal(L["workspace.issues.unreachable"], issue.Cause);
        Assert.False(issue.ShowsResolve);
        Assert.False(issue.ShowsRelocate);
    }

    [Fact]
    public async Task ARecordInAnUnsupportedFormatOffersNoAction()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var folder = MeetingFolder(kappa, "7f3a0000000000a7");
        File.WriteAllText(Path.Combine(folder, WorkspaceStore.MeetingFileName), """{ "schemaVersion": 9 }""");
        var vm = Opened(store, kappa);

        var settings = await Settings(vm, kappa, _ => Task.CompletedTask);

        var issue = Assert.Single(settings.Issues);
        Assert.False(issue.ShowsResolve);
        Assert.False(issue.ShowsRelocate);
    }

    [Fact]
    public async Task AFolderOfAnotherListedProjectIsRefusedAsTaken()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        Directory.Delete(gone.RootPath, recursive: true);
        var vm = Opened(store, kappa);

        var settings = await Settings(vm, gone, async settings =>
        {
            settings.ChooseProjectFolder = () => Task.FromResult<string?>(kappa.RootPath);
            await settings.RelocateCommand.ExecuteAsync(settings.Issues.Single());
        });

        Assert.Equal(L["workspace.issues.refused.taken"], Assert.Single(settings.Issues).Outcome);
    }

    [AvaloniaFact]
    public void TheEnglishBaseTitleNamesTheMeetingStart()
    {
        var before = L.Current;
        try
        {
            L.Current = "en";
            Assert.Equal(
                $"Recovered meeting {RecoveredTitleTime}",
                L.Format("workspace.issues.recoveredtitle", RecoveredTitleTime));
        }
        finally
        {
            L.Current = before;
        }
    }

    [Theory]
    [InlineData("workspace.issues.unreachable", 0)]
    [InlineData("workspace.issues.resolve", 0)]
    [InlineData("workspace.issues.choosefolder", 0)]
    [InlineData("workspace.issues.recording", 0)]
    [InlineData("workspace.issues.recoveredtitle", 1)]
    [InlineData("workspace.issues.recovered", 1)]
    [InlineData("workspace.issues.reidentified", 1)]
    [InlineData("workspace.issues.setaside", 1)]
    [InlineData("workspace.issues.relocated", 1)]
    [InlineData("workspace.issues.meetingscreated", 0)]
    [InlineData("workspace.issues.rowsremoved", 0)]
    [InlineData("workspace.issues.refused.missing", 0)]
    [InlineData("workspace.issues.refused.unwritable", 0)]
    [InlineData("workspace.issues.refused.otherproject", 0)]
    [InlineData("workspace.issues.refused.taken", 0)]
    [InlineData("workspace.issues.refused.oldfolder", 0)]
    [InlineData("workspace.issues.refused.other", 0)]
    public void EveryResolveStringIsWrittenInAllFourLanguages(string key, int placeholders)
    {
        foreach (var code in new[] { "en", "zh", "de", "pl" })
        {
            Assert.True(Strings.Tables[code].TryGetValue(key, out var text), $"{code} misses {key}");
            Assert.Equal(placeholders > 0, text!.Contains("{0}", StringComparison.Ordinal));
        }
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void TheRowOfADamagedRecordShowsResolveAndThenItsOutcome()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        DamagedMeeting(kappa, "7f3a0000000000a8");
        var vm = Opened(store, kappa);
        ProjectSettingsWindow? window = null;
        vm.ShowProjectSettings = settings =>
        {
            window = new ProjectSettingsWindow(settings);
            window.Show();
            return Task.CompletedTask;
        };
        vm.ShowIssuesCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var resolve = Named<Button>(window!, "Resolve");
        Assert.True(resolve.IsEffectivelyVisible);
        Assert.False(Named<StackPanel>(window!, "RelocateActions").IsVisible);
        Assert.False(Named<TextBlock>(window!, "IssueOutcome").IsVisible);
        Assert.False(Named<TextBlock>(window!, "ResolveBlocked").IsVisible);

        resolve.Command!.Execute(resolve.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.False(resolve.IsVisible);
        Assert.Contains("resolved", Named<Avalonia.Controls.Shapes.Path>(window!, "IssueIcon").Classes);
        var outcome = Named<TextBlock>(window!, "IssueOutcome");
        Assert.True(outcome.IsVisible);
        Assert.Equal(
            L.Format("workspace.issues.recovered", L.Format("workspace.issues.recoveredtitle", RecoveredTitleTime)),
            outcome.Text);

        window!.Close();
    }

    [AvaloniaFact]
    public void TheRowOfAMissingProjectFolderOffersTheTwoActions()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var gone = store.CreateWorkspace("Gone", Folder("gone")).Workspace!;
        Directory.Delete(gone.RootPath, recursive: true);
        var vm = Opened(store, kappa);
        var window = new ProjectSettingsWindow(new ProjectSettingsViewModel(
            store, vm.Workspaces.Single(w => w.Id == gone.Id), recording: false,
            vm.IssuesOf(vm.Workspaces.Single(w => w.Id == gone.Id))));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(Named<StackPanel>(window, "RelocateActions").IsEffectivelyVisible);
        Assert.Equal(L["workspace.issues.choosefolder"], Named<Button>(window, "ChooseFolder").Content);
        Assert.Equal(L["workspace.remove.listonly"], Named<Button>(window, "RemoveFromList").Content);
        Assert.False(Named<Button>(window, "Resolve").IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void TheRecordingNoteShowsInTheIssuesSection()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        MeetingFolder(kappa, "7f3a0000000000a9");
        var vm = Opened(store, kappa);
        var window = new ProjectSettingsWindow(new ProjectSettingsViewModel(
            store, kappa, recording: true, vm.IssuesOf(kappa), projectRecording: true));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var note = Named<TextBlock>(window, "ResolveBlocked");
        Assert.True(note.IsEffectivelyVisible);
        Assert.Equal(L["workspace.issues.recording"], note.Text);
        Assert.False(Named<Button>(window, "Resolve").IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void TheListOnlyConfirmationOffersNoDeletion()
    {
        var window = new RemoveProjectWindow("Gone", listOnly: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var buttons = window.GetLogicalDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();
        Assert.Equal(["Keep", "ListOnly"], buttons.Select(b => b.Name).Order());
        Assert.False(Named<TextBlock>(window, "DeleteFilesBody").IsVisible);
        Assert.True(Named<Button>(window, "Keep").IsDefault);

        window.Close();
    }
}
