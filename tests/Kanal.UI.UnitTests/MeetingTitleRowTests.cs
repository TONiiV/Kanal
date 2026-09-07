using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class MeetingTitleRowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-title-" + Guid.NewGuid().ToString("N"));

    private WorkspaceStore Store()
    {
        Directory.CreateDirectory(Path.Combine(_root, "kappa"));
        return new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
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

    private MainViewModel WithMeeting(string title)
    {
        var store = Store();
        var workspace = store.CreateWorkspace("Kappa", Path.Combine(_root, "kappa")).Workspace!;
        store.CreateMeeting(workspace.Id, title);
        return TestViewModels.Hermetic(workspaces: () => store);
    }

    [Fact]
    public void WithNothingChosenTheRowStillNamesTheMeetingRatherThanShowingNothing()
    {
        var vm = TestViewModels.Hermetic();

        Assert.Equal(Localizer.Instance["meeting.untitled"], vm.MeetingTitle);
    }

    [Fact]
    public void ChoosingAMeetingRetitlesTheCentreColumn()
    {
        var vm = WithMeeting("Werkzeugübergabe");

        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();

        Assert.Equal("Werkzeugübergabe", vm.MeetingTitle);

        vm.Sidebar.SelectedMeeting = null;
        Assert.Equal(Localizer.Instance["meeting.untitled"], vm.MeetingTitle);
    }

    [Fact]
    public void ALoadedRoomNamesTheRowUntilARecordIsChosenOverIt()
    {
        var vm = WithMeeting("Werkzeugübergabe");

        // Cleared when the next room is opened, not when this one stops: the transcript stays on
        // screen after Stop, and a row titled "New meeting" over it would name the wrong thing.
        vm.LoadedRoomId = "kanal-2026-09-07-a1";
        Assert.Equal("kanal-2026-09-07-a1", vm.MeetingTitle);

        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();
        Assert.Equal("Werkzeugübergabe", vm.MeetingTitle);
    }

    [AvaloniaFact]
    public void TheTitleSharesItsRowWithTheFlagsAndNoProjectHeaderSitsAboveIt()
    {
        var vm = WithMeeting("Werkzeugübergabe");
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var room = window.GetLogicalDescendants().OfType<MeetingRoomView>().Single();
        var title = Named(room, "MeetingTitle");
        var flags = Named(room, "RoomLanguages");

        // The title sits in its own group beside the note now; what has to stay true is that the
        // group and the flags are on one row, and that row is the top of the transcript.
        var row = (Control)flags.Parent!;
        Assert.Contains(row, title.GetLogicalAncestors());
        Assert.Same(room.Content, row.Parent);

        window.Close();
    }

    private static Control Named(Control root, string name) =>
        root.GetLogicalDescendants().OfType<Control>()
            .Where(control => control.Name == name).Distinct().Single();
}
