using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class ProjectSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-project-" + Guid.NewGuid().ToString("N"));

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

    private (WorkspaceSidebarViewModel Vm, Workspace Kappa, Workspace Lambda) TwoProjects(WorkspaceStore store)
    {
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var lambda = store.CreateWorkspace("Lambda", Folder("lambda")).Workspace!;
        store.CreateMeeting(kappa.Id, "Delivery call");
        var vm = new WorkspaceSidebarViewModel(store);
        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == kappa.Id);
        return (vm, kappa, lambda);
    }

    private static async Task<ProjectSettingsViewModel> Opened(
        WorkspaceSidebarViewModel vm, Workspace project, Func<ProjectSettingsViewModel, Task> act)
    {
        ProjectSettingsViewModel? shown = null;
        vm.ShowProjectSettings = async settings =>
        {
            shown = settings;
            await act(settings);
        };

        await vm.OpenProjectSettingsCommand.ExecuteAsync(project);
        return Assert.IsType<ProjectSettingsViewModel>(shown);
    }

    [Fact]
    public async Task TheGearOpensTheSettingsOfItsOwnRowWithoutSelectingIt()
    {
        var (vm, kappa, lambda) = TwoProjects(Store());

        var shown = await Opened(vm, lambda, _ => Task.CompletedTask);

        Assert.Equal(lambda.Id, shown.Project.Id);
        Assert.Equal("Lambda", shown.Name);
        Assert.Equal(kappa.Id, vm.SelectedWorkspace!.Id);
    }

    [Fact]
    public async Task SaveRenamesTheProjectAndSetsItsIcon()
    {
        var store = Store();
        var (vm, kappa, _) = TwoProjects(store);

        await Opened(vm, kappa, settings =>
        {
            settings.Name = "  Kappa tooling ";
            settings.ChooseGlyphCommand.Execute("people");
            settings.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });

        var stored = store.ListWorkspaces().Workspaces.Single(w => w.Id == kappa.Id);
        Assert.Equal("Kappa tooling", stored.Name);
        Assert.Equal("people", stored.IconGlyph);
        Assert.Equal(stored, vm.SelectedWorkspace);
        Assert.Equal("Delivery call", Assert.Single(vm.Meetings).Title);
    }

    [Fact]
    public async Task ClosingWithoutSaveChangesNothing()
    {
        var store = Store();
        var (vm, kappa, _) = TwoProjects(store);
        var image = Path.Combine(Folder("pictures"), "logo.png");
        File.WriteAllBytes(image, new byte[64]);

        var settings = await Opened(vm, kappa, async settings =>
        {
            settings.Name = "Something else";
            settings.ChooseImageFile = () => Task.FromResult<string?>(image);
            await settings.ChooseImageCommand.ExecuteAsync(null);
        });

        Assert.Equal("logo.png", settings.ImageFileName);
        Assert.Equal(kappa, store.ListWorkspaces().Workspaces.Single(w => w.Id == kappa.Id));
        Assert.Empty(Directory.EnumerateFiles(kappa.RootPath, "kanal-icon.*"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyNameCannotBeSaved(string blank)
    {
        var (vm, kappa, _) = TwoProjects(Store());

        var settings = await Opened(vm, kappa, settings =>
        {
            settings.Name = blank;
            return Task.CompletedTask;
        });

        Assert.False(settings.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task AChosenImageIsCopiedIntoTheProjectOnSave()
    {
        var store = Store();
        var (vm, kappa, _) = TwoProjects(store);
        var image = Path.Combine(Folder("pictures"), "logo.png");
        File.WriteAllBytes(image, new byte[64]);

        await Opened(vm, kappa, async settings =>
        {
            settings.ChooseImageFile = () => Task.FromResult<string?>(image);
            await settings.ChooseImageCommand.ExecuteAsync(null);
            settings.SaveCommand.Execute(null);
        });

        Assert.True(File.Exists(Path.Combine(kappa.RootPath, "kanal-icon.png")));
        Assert.Equal("kanal-icon.png", vm.SelectedWorkspace!.IconFile);
    }

    [Fact]
    public async Task AnImageOverTheLimitIsRefusedWhenItIsChosen()
    {
        var (vm, kappa, _) = TwoProjects(Store());
        var image = Path.Combine(Folder("pictures"), "poster.png");
        File.WriteAllBytes(image, new byte[WorkspaceStore.MaxIconBytes + 1]);

        var settings = await Opened(vm, kappa, async settings =>
        {
            settings.ChooseImageFile = () => Task.FromResult<string?>(image);
            await settings.ChooseImageCommand.ExecuteAsync(null);
        });

        Assert.Null(settings.ImagePath);
        Assert.Equal(Localizer.Instance["workspace.icon.toolarge"], settings.Problem);
    }

    [Fact]
    public async Task AFailedIconStepLeavesTheNameUnchanged()
    {
        var store = Store();
        var (vm, kappa, _) = TwoProjects(store);
        var image = Path.Combine(Folder("pictures"), "logo.png");
        File.WriteAllBytes(image, new byte[64]);

        var settings = await Opened(vm, kappa, async settings =>
        {
            settings.ChooseImageFile = () => Task.FromResult<string?>(image);
            await settings.ChooseImageCommand.ExecuteAsync(null);
            settings.Name = "Kappa tooling";
            File.Delete(image);
            settings.SaveCommand.Execute(null);
        });

        Assert.Equal("Kappa", store.ListWorkspaces().Workspaces.Single(w => w.Id == kappa.Id).Name);
        Assert.False(settings.Changed);
        Assert.Equal(Localizer.Instance["workspace.icon.missing"], settings.Problem);
    }

    [Theory]
    [InlineData("logo.gif", 64, "workspace.icon.badtype")]
    [InlineData("logo.png", 2 * 1024 * 1024 + 1, "workspace.icon.toolarge")]
    public async Task AnIconTheStoreRefusesIsExplainedInTheInterfaceLanguage(string name, int bytes, string key)
    {
        var (vm, kappa, _) = TwoProjects(Store());
        var image = Path.Combine(Folder("pictures"), name);
        File.WriteAllBytes(image, new byte[bytes]);

        var settings = await Opened(vm, kappa, settings =>
        {
            settings.ImagePath = image;
            settings.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });

        Assert.Equal(Localizer.Instance[key], settings.Problem);
    }

    [Fact]
    public async Task ARefusedRemovalIsExplainedInTheInterfaceLanguage()
    {
        var store = Store();
        var (vm, kappa, _) = TwoProjects(store);
        File.Delete(Path.Combine(kappa.RootPath, WorkspaceStore.WorkspaceFileName));

        var settings = await Opened(vm, kappa, async settings =>
        {
            settings.ConfirmRemoval = _ => Task.FromResult<ProjectRemoval?>(ProjectRemoval.DeleteFiles);
            await settings.RemoveCommand.ExecuteAsync(null);
        });

        Assert.Equal(Localizer.Instance["workspace.settings.failed"], settings.Problem);
        Assert.True(Directory.Exists(Path.Combine(kappa.RootPath, "meetings")));
    }

    [AvaloniaFact]
    public void TheRenameItemOfAMeetingRowMenuGivesTheNameFieldTheCursor()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        store.CreateMeeting(kappa.Id, "Delivery call");
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var menu = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "MeetingMenu");
        menu.Flyout!.ShowAt(menu);
        Dispatcher.UIThread.RunJobs();
        var rename = ((MenuFlyout)menu.Flyout).Items.OfType<MenuItem>().Single(m => m.Name == "RenameMeeting");
        var at = rename.TranslatePoint(new Point(rename.Bounds.Width / 2, rename.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var editor = window.GetLogicalDescendants().OfType<TextBox>()
            .Single(b => b.Name == "MeetingRowEditor" && b.IsVisible);
        Assert.True(vm.Sidebar.Meetings.Single().IsRenaming);
        Assert.True(editor.IsFocused, "FOCUS " + window.FocusManager!.GetFocusedElement() + " vis " + editor.IsEffectivelyVisible + " " + editor.Bounds + " " + editor.Focusable + " active " + window.IsActive);

        window.Close();
    }

    [Fact]
    public async Task TheResetActionShowsOnlyForAnIconThatIsNotTheDefault()
    {
        var store = Store();
        var (vm, kappa, _) = TwoProjects(store);
        store.SetWorkspaceIcon(kappa.Id, "cloud");
        vm.Refresh();

        await Opened(vm, vm.SelectedWorkspace!, settings =>
        {
            Assert.True(settings.IsCustomIcon);
            settings.ResetIconCommand.Execute(null);
            Assert.False(settings.IsCustomIcon);
            settings.SaveCommand.Execute(null);
            return Task.CompletedTask;
        });

        Assert.Null(store.ListWorkspaces().Workspaces.Single(w => w.Id == kappa.Id).IconGlyph);
    }

    [Fact]
    public async Task RemovingFromTheListKeepsTheFilesAndSelectsAnotherProject()
    {
        var store = Store();
        var (vm, kappa, lambda) = TwoProjects(store);

        await Opened(vm, kappa, async settings =>
        {
            settings.ConfirmRemoval = _ => Task.FromResult<ProjectRemoval?>(ProjectRemoval.ListOnly);
            await settings.RemoveCommand.ExecuteAsync(null);
        });

        Assert.Equal(lambda.Id, Assert.Single(vm.Workspaces).Id);
        Assert.Equal(lambda.Id, vm.SelectedWorkspace!.Id);
        Assert.True(File.Exists(Path.Combine(kappa.RootPath, WorkspaceStore.WorkspaceFileName)));
        Assert.Equal(kappa.Id, store.OpenWorkspace(kappa.RootPath).Workspace!.Id);
    }

    [Fact]
    public async Task RemovingWithTheFilesDeletesTheMeetingsAndSelectsAnotherProject()
    {
        var store = Store();
        var (vm, kappa, lambda) = TwoProjects(store);

        await Opened(vm, kappa, async settings =>
        {
            settings.ConfirmRemoval = _ => Task.FromResult<ProjectRemoval?>(ProjectRemoval.DeleteFiles);
            await settings.RemoveCommand.ExecuteAsync(null);
        });

        Assert.False(Directory.Exists(kappa.RootPath));
        Assert.Equal(lambda.Id, vm.SelectedWorkspace!.Id);
    }

    [Fact]
    public async Task RemovingTheLastProjectLeavesTheEmptyState()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var vm = new WorkspaceSidebarViewModel(store);

        await Opened(vm, kappa, async settings =>
        {
            settings.ConfirmRemoval = _ => Task.FromResult<ProjectRemoval?>(ProjectRemoval.ListOnly);
            await settings.RemoveCommand.ExecuteAsync(null);
        });

        Assert.Empty(vm.Workspaces);
        Assert.False(vm.HasWorkspace);
    }

    [Fact]
    public async Task ACancelledOrUnwiredConfirmationRemovesNothing()
    {
        var store = Store();
        var (vm, kappa, _) = TwoProjects(store);

        await Opened(vm, kappa, async settings =>
        {
            await settings.RemoveCommand.ExecuteAsync(null);
            settings.ConfirmRemoval = _ => Task.FromResult<ProjectRemoval?>(null);
            await settings.RemoveCommand.ExecuteAsync(null);
        });

        Assert.Equal(2, store.ListWorkspaces().Workspaces.Count);
        Assert.Equal(kappa.Id, vm.SelectedWorkspace!.Id);
    }

    [Fact]
    public async Task NoProjectIsRemovedWhileAMeetingIsRecording()
    {
        var store = Store();
        var (vm, _, lambda) = TwoProjects(store);
        vm.RecordingMeetingId = vm.Meetings.Single().Id;
        var asked = false;

        var settings = await Opened(vm, lambda, async settings =>
        {
            settings.ConfirmRemoval = _ =>
            {
                asked = true;
                return Task.FromResult<ProjectRemoval?>(ProjectRemoval.DeleteFiles);
            };
            Assert.False(settings.RemoveCommand.CanExecute(null));
            await settings.RemoveCommand.ExecuteAsync(null);
        });

        Assert.False(asked);
        Assert.False(settings.CanRemove);
        Assert.Equal(Localizer.Instance["workspace.remove.recording"], settings.RemoveBlockedNote);
        Assert.Equal(2, store.ListWorkspaces().Workspaces.Count);
        Assert.True(Directory.Exists(lambda.RootPath));
    }

    [AvaloniaFact]
    public void TheRemovalDialogNeverMakesTheDestructiveChoiceTheDefault()
    {
        var window = new RemoveProjectWindow("Kappa");
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var buttons = window.GetLogicalDescendants().OfType<Button>().ToList();
        var keep = buttons.Single(b => b.Name == "Keep");
        Assert.True(keep.IsDefault);
        Assert.True(keep.IsCancel);
        Assert.Contains("accent", keep.Classes);
        Assert.All(buttons.Where(b => b != keep), b => Assert.False(b.IsDefault));
        Assert.Equal(["DeleteFiles", "Keep", "ListOnly"], buttons.Select(b => b.Name).Order());

        window.Close();
    }

    [AvaloniaFact]
    public void TheSettingsWindowShowsTheProjectItWasOpenedFor()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var window = new ProjectSettingsWindow(new ProjectSettingsViewModel(store, kappa, recording: false));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var name = window.GetLogicalDescendants().OfType<TextBox>().Single(b => b.Name == "ProjectName");
        Assert.Equal("Kappa", name.Text);
        Assert.False(window.GetLogicalDescendants().OfType<TabControl>().Any());

        window.Close();
    }

    [AvaloniaFact]
    public void ClickingTheGearInTheDropdownOpensThatProjectAndKeepsTheSelection()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var lambda = store.CreateWorkspace("Lambda", Folder("lambda")).Workspace!;
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.Sidebar.SelectedWorkspace = vm.Sidebar.Workspaces.Single(w => w.Id == kappa.Id);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Workspace? opened = null;
        vm.Sidebar.ShowProjectSettings = settings =>
        {
            opened = settings.Project;
            return Task.CompletedTask;
        };

        var picker = window.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "WorkspacePicker");
        picker.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        var gears = picker.GetLogicalDescendants().OfType<Button>()
            .Where(b => b.Name == "ProjectSettings").ToList();
        Assert.Equal(2, gears.Count);
        Assert.All(gears, gear => Assert.False(string.IsNullOrWhiteSpace(
            Avalonia.Automation.AutomationProperties.GetName(gear))));
        var gear = gears.Single(g => ((Workspace)g.DataContext!).Id == lambda.Id);

        var at = gear.TranslatePoint(new Point(gear.Bounds.Width / 2, gear.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(lambda.Id, opened?.Id);
        Assert.Equal(kappa.Id, vm.Sidebar.SelectedWorkspace!.Id);
        Assert.False(picker.IsDropDownOpen);

        window.Close();
    }

    [AvaloniaFact]
    public async Task RenamingTheOpenProjectKeepsTheMeetingThatIsOpen()
    {
        var store = Store();
        var kappa = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        store.CreateMeeting(kappa.Id, "Delivery call");
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var meeting = vm.Sidebar.Meetings.Single();
        vm.Sidebar.SelectedMeeting = meeting;
        vm.Sidebar.ShowProjectSettings = settings =>
        {
            settings.Name = "Kappa tooling";
            settings.SaveCommand.Execute(null);
            return Task.CompletedTask;
        };

        await vm.Sidebar.OpenProjectSettingsCommand.ExecuteAsync(vm.Sidebar.SelectedWorkspace);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Kappa tooling", vm.Sidebar.SelectedWorkspace!.Name);
        Assert.Equal(meeting.Id, vm.Sidebar.SelectedMeeting?.Id);

        window.Close();
    }
}
