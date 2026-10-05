using Avalonia.Headless.XUnit;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class MeetingGroupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 14, 40, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 5)));

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-groups-" + Guid.NewGuid().ToString("N"));

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

    private WorkspaceSidebarViewModel Opened(params (string Title, TimeSpan Ago)[] meetings)
    {
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var folder = Path.Combine(_root, "kappa");
        Directory.CreateDirectory(folder);
        var workspace = store.CreateWorkspace("Kappa", folder).Workspace!;
        foreach (var (title, ago) in meetings)
        {
            var made = store.CreateMeeting(workspace.Id, title).Meeting!;
            store.SaveMeeting(made with { CreatedAt = Now - ago });
        }

        var vm = new WorkspaceSidebarViewModel(store, clock: () => Now);
        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == workspace.Id);
        return vm;
    }

    private static readonly TimeSpan SinceMidnight = Now.TimeOfDay;

    [Fact]
    public void MeetingsAreSetUnderTodayYesterdayAndRecentByTheDayTheyWereMade()
    {
        var vm = Opened(
            ("this morning", TimeSpan.FromHours(1)),
            ("last evening", SinceMidnight + TimeSpan.FromHours(1)),
            ("three days ago", SinceMidnight + TimeSpan.FromDays(2)),
            ("last month", TimeSpan.FromDays(30)));

        Assert.Equal(
            [MeetingAge.Today, MeetingAge.Yesterday, MeetingAge.Recent],
            vm.MeetingGroups.Select(g => g.Age));
        Assert.Equal(["this morning"], vm.MeetingGroups[0].Items.Select(m => m.Title));
        Assert.Equal(["last evening"], vm.MeetingGroups[1].Items.Select(m => m.Title));
        Assert.Equal(["three days ago", "last month"], vm.MeetingGroups[2].Items.Select(m => m.Title));
        Assert.Equal(4, vm.Meetings.Count);
    }

    [Fact]
    public void AGroupWithNoMeetingsIsNotShown()
    {
        var vm = Opened(("last month", TimeSpan.FromDays(30)));

        Assert.Equal(MeetingAge.Recent, Assert.Single(vm.MeetingGroups).Age);

        vm.Search = "nothing matches this";
        Assert.Empty(vm.MeetingGroups);
    }

    [Fact]
    public void EveryGroupStartsOpenAndFoldsAndUnfoldsOnItsOwn()
    {
        var vm = Opened(
            ("this morning", TimeSpan.FromHours(1)),
            ("last month", TimeSpan.FromDays(30)));

        Assert.All(vm.MeetingGroups, g => Assert.True(g.IsExpanded));

        vm.MeetingGroups[1].ToggleCommand.Execute(null);

        Assert.True(vm.MeetingGroups[0].IsExpanded);
        Assert.False(vm.MeetingGroups[1].IsExpanded);
    }

    [Fact]
    public void AFoldedGroupStaysFoldedThroughSearchAndRefresh()
    {
        var vm = Opened(
            ("this morning", TimeSpan.FromHours(1)),
            ("last month", TimeSpan.FromDays(30)));
        vm.MeetingGroups[1].ToggleCommand.Execute(null);

        vm.Search = "last";
        vm.Search = "";
        vm.Refresh();

        Assert.False(vm.MeetingGroups.Single(g => g.Age == MeetingAge.Recent).IsExpanded);
    }

    [Fact]
    public void SelectingAMeetingOpensTheGroupItIsIn()
    {
        var vm = Opened(
            ("this morning", TimeSpan.FromHours(1)),
            ("last month", TimeSpan.FromDays(30)));
        var recent = vm.MeetingGroups[1];
        recent.ToggleCommand.Execute(null);

        vm.Select(recent.Items[0].Id);

        Assert.True(recent.IsExpanded);
    }

    [Fact]
    public void OnlyTheGroupHoldingTheSelectedMeetingShowsASelection()
    {
        var vm = Opened(
            ("this morning", TimeSpan.FromHours(1)),
            ("last month", TimeSpan.FromDays(30)));
        var (today, recent) = (vm.MeetingGroups[0], vm.MeetingGroups[1]);

        today.Selected = today.Items[0];
        Assert.Same(today.Items[0], vm.SelectedMeeting);

        vm.SelectedMeeting = recent.Items[0];
        Assert.Null(today.Selected);
        Assert.Same(recent.Items[0], recent.Selected);

        today.Selected = null;
        Assert.Same(recent.Items[0], vm.SelectedMeeting);
    }

    [AvaloniaFact]
    public void TheGroupNamesAreLocalisedAndFollowTheLanguage()
    {
        var vm = Opened(("this morning", TimeSpan.FromHours(1)));
        var today = vm.MeetingGroups[0];
        var before = Localizer.Instance.Current;
        try
        {
            Localizer.Instance.Current = "zh";
            Assert.Equal("今天", today.Title);
            Localizer.Instance.Current = "en";
            Assert.Equal("Today", today.Title);
        }
        finally
        {
            Localizer.Instance.Current = before;
        }
    }
}
