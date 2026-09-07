using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Meetings;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class MeetingTitleGenerationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-titling-" + Guid.NewGuid().ToString("N"));

    private sealed class Titler : IMeetingTitler
    {
        public string? Next { get; set; } = "Tolerance review";

        public int Calls { get; private set; }

        public Task<string?> SuggestAsync(IReadOnlyList<string> lines, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Next);
        }
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

    private (MainViewModel Vm, WorkspaceStore Store, Workspace Workspace) WithMeeting(
        string title, IMeetingTitler? titler)
    {
        Directory.CreateDirectory(Path.Combine(_root, "kappa"));
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var workspace = store.CreateWorkspace("Kappa", Path.Combine(_root, "kappa")).Workspace!;
        store.CreateMeeting(workspace.Id, title);
        var vm = TestViewModels.Hermetic(workspaces: () => store, titler: titler);
        return (vm, store, workspace);
    }

    [Fact]
    public void ARenameSurvivesTheAppBeingClosed()
    {
        var (vm, store, workspace) = WithMeeting("New meeting", null);
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();

        vm.BeginRenameTitleCommand.Execute(null);
        vm.TitleDraft = "  Werkzeugübergabe  ";
        vm.CommitRenameTitleCommand.Execute(null);

        Assert.Equal("Werkzeugübergabe", vm.MeetingTitle);
        Assert.False(vm.IsRenamingTitle);
        Assert.Equal("Werkzeugübergabe", store.ListMeetings(workspace.Id).Meetings.Single().Title);
    }

    [Fact]
    public void AbandoningARenameLeavesTheTitleAlone()
    {
        var (vm, _, _) = WithMeeting("Werkzeugübergabe", null);
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();

        vm.BeginRenameTitleCommand.Execute(null);
        Assert.Equal("Werkzeugübergabe", vm.TitleDraft);
        vm.TitleDraft = "half-typed";
        vm.CancelRenameTitleCommand.Execute(null);

        Assert.Equal("Werkzeugübergabe", vm.MeetingTitle);
        Assert.False(vm.IsRenamingTitle);
    }

    [Fact]
    public async Task RegeneratingReplacesAHandTypedNameBecauseTheOperatorAskedForIt()
    {
        var titler = new Titler();
        var (vm, store, workspace) = WithMeeting("New meeting", titler);
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();

        vm.BeginRenameTitleCommand.Execute(null);
        vm.TitleDraft = "Werkzeugübergabe";
        vm.CommitRenameTitleCommand.Execute(null);

        await vm.RegenerateTitleCommand.ExecuteAsync(null);

        Assert.Equal(1, titler.Calls);
        Assert.Equal("Tolerance review", vm.MeetingTitle);
        Assert.Equal("Tolerance review", store.ListMeetings(workspace.Id).Meetings.Single().Title);
    }

    [Fact]
    public async Task AFailedNamingSaysSoInsteadOfLeavingTheRowBlank()
    {
        var titler = new Titler { Next = null };
        var (vm, _, _) = WithMeeting("New meeting", titler);
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();

        await vm.RegenerateTitleCommand.ExecuteAsync(null);

        Assert.Equal("New meeting", vm.MeetingTitle);
        Assert.Equal(Localizer.Instance["title.failed"], vm.TitleNote);
    }

    [Fact]
    public void WithNoLocalModelTheNamingControlIsAbsentRatherThanDead()
    {
        var (without, _, _) = WithMeeting("New meeting", null);
        Assert.False(without.CanNameMeeting);

        var (with, _, _) = WithMeeting("New meeting", new Titler());
        Assert.True(with.CanNameMeeting);
    }

    [AvaloniaFact]
    public void TheTitleRowCarriesTheEditorAndTheNamingControl()
    {
        var (vm, _, _) = WithMeeting("Werkzeugübergabe", new Titler());
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var room = window.GetLogicalDescendants().OfType<MeetingRoomView>().Single();
        var title = Named<Border>(room, "MeetingTitleField");
        var editor = Named<TextBox>(room, "MeetingTitleEditor");
        var regenerate = Named<Button>(room, "RegenerateTitle");

        Assert.True(title.IsVisible);
        Assert.False(editor.IsVisible);
        Assert.True(regenerate.IsVisible);

        vm.BeginRenameTitleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(title.IsVisible);
        Assert.True(editor.IsVisible);

        window.Close();
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>()
            .Where(control => control.Name == name).Distinct().Single();
}
