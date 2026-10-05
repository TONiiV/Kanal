using Kanal.Core.Models;
using Kanal.Core.Room;
using Kanal.Core.Workspaces;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class BrowsedRulerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-browsed-ruler-" + Guid.NewGuid().ToString("N"));

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

    private static Utterance Said(string id, string tag, long at, string lang, string text) =>
        new(id, tag, at, at + 1000, lang, text, 1, UtteranceState.Final, false, 1.0,
            new Dictionary<string, string> { ["zh"] = $"[zh] {text}", ["de"] = $"[de] {text}" });

    private (MainViewModel Vm, MeetingRecord Record) Browsing(params Utterance[] said)
    {
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        var workspace = store.CreateWorkspace("ACME", folder).Workspace!;
        var record = store.CreateMeeting(workspace.Id, "Vorbesprechung").Meeting!;
        var path = Path.Combine(store.MeetingFolder(workspace.Id, record.Id)!, TranscriptLog.FileName);
        using (var log = new TranscriptLogWriter(path, _ => { }))
        {
            foreach (var utterance in said)
                log.Append(utterance);
        }

        record = store.SaveMeeting(record with
        {
            StartedAt = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            TranscriptPath = path,
            Languages = ["zh", "de"],
        }).Meeting!;

        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Id == record.Id);
        return (vm, record);
    }

    [Fact]
    public void ABrowsedMeetingHasARulerBuiltFromItsStoredTurns()
    {
        var (vm, _) = Browsing(
            Said("u1", "S1", 0, "de", "Toleranz bei KX-4402"),
            Said("u2", "S1", 4000, "de", "und die Lieferung"),
            Said("u3", "S2", 9000, "zh", "交期确认"));

        Assert.True(vm.IsBrowsingRecord);
        Assert.Equal(["u1", "u3"], vm.ShownRuler.Ticks.Select(t => t.AnchorUtteranceId));
        Assert.Equal(["0:00", "0:09"], vm.ShownRuler.Ticks.Select(t => t.TimeLabel));
        Assert.NotEqual(vm.ShownRuler.Ticks[0].SpeakerColor, vm.ShownRuler.Ticks[1].SpeakerColor);
        Assert.Empty(vm.Ruler.Ticks);
    }

    [Fact]
    public void JumpingFromABrowsedTickMarksTheStoredLine()
    {
        var (vm, _) = Browsing(
            Said("u1", "S1", 0, "de", "Toleranz bei KX-4402"),
            Said("u2", "S2", 9000, "zh", "交期确认"));

        vm.ShownRuler.Jump(vm.ShownRuler.Ticks[1]);

        var marked = vm.ShownColumns.SelectMany(c => c.Bubbles).Where(b => b.IsJumpTarget);
        Assert.All(marked, b => Assert.Equal("u2", b.UtteranceId));
        Assert.NotEmpty(marked);
    }

    [Fact]
    public void LeavingTheBrowsedMeetingGivesTheLiveRulerBack()
    {
        var (vm, _) = Browsing(Said("u1", "S1", 0, "de", "Toleranz bei KX-4402"));

        vm.Sidebar.SelectedMeeting = null;

        Assert.Same(vm.Ruler, vm.ShownRuler);
    }
}
