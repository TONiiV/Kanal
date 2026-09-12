using System.IO.Compression;
using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

public class MeetingBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-bundle-" + Guid.NewGuid().ToString("N"));

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

    private WorkspaceStore Store(string machine) =>
        new(Path.Combine(_root, machine, "workspaces.json"));

    private (WorkspaceStore Store, Workspace Workspace) Opened(string machine)
    {
        var store = Store(machine);
        var folder = Path.Combine(_root, machine, "Kanal");
        Directory.CreateDirectory(folder);
        return (store, store.CreateWorkspace("Kanal", folder).Workspace!);
    }

    private static MeetingRecord Recorded(WorkspaceStore store, Workspace workspace, string title)
    {
        var meeting = store.CreateMeeting(workspace.Id, title).Meeting!;
        var folder = store.MeetingFolder(workspace.Id, meeting.Id)!;
        File.WriteAllText(
            Path.Combine(folder, TranscriptLog.FileName),
            """
            {"id":"u1","speakerTag":"S1","tStartMs":0,"tEndMs":1200,"srcLang":"de","srcText":"Die Toleranz ist 0,05 mm","revision":1,"state":"Final","codeSwitch":false,"speakerConfidence":0.9,"translations":{"zh":"公差是 0,05 毫米"}}
            {"id":"u2","speakerTag":"S2","tStartMs":1300,"tEndMs":2000,"srcLang":"zh","srcText":"KX-4402 什么时候到","revision":1,"state":"Final","codeSwitch":false,"speakerConfidence":0.9,"translations":{"de":"Wann kommt KX-4402"}}
            """);
        // Noise, not zeroes: a silent buffer deflates to nothing and would hide a bundle that
        // carries the recording it promised to leave out.
        var pcm = new byte[200_000];
        new Random(4402).NextBytes(pcm);
        File.WriteAllBytes(Path.Combine(folder, WorkspaceStore.AudioFileName), pcm);
        Directory.CreateDirectory(Path.Combine(folder, "attachments"));
        File.WriteAllText(Path.Combine(folder, "attachments", "quotation.pdf"), "offer");

        return store.SaveMeeting(meeting with
        {
            StartedAt = new DateTimeOffset(2026, 9, 8, 14, 30, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 9, 8, 15, 12, 0, TimeSpan.Zero),
            Languages = ["de", "zh"],
            TranscriptPath = Path.Combine(folder, TranscriptLog.FileName),
            AudioPath = Path.Combine(folder, WorkspaceStore.AudioFileName),
        }).Meeting!;
    }

    private string Bundle(string name) => Path.Combine(_root, name + MeetingBundle.Extension);

    private static IReadOnlyList<string> Entries(string bundle)
    {
        using var zip = ZipFile.OpenRead(bundle);
        return [.. zip.Entries.Select(e => e.FullName).Order()];
    }

    [Fact]
    public void ABundleRebuildsTheSameRecordInAnotherWorkspace()
    {
        var (here, mine) = Opened("laptop");
        var meeting = Recorded(here, mine, "Kanal 2026-09-08 14:30");
        var bundle = Bundle("delivery");

        Assert.Null(MeetingBundle.Write(here, meeting, includeAudio: true, bundle));

        var (there, theirs) = Opened("supplier");
        var imported = MeetingBundle.Import(there, theirs.Id, bundle, asNewRecord: false);

        Assert.Null(imported.Problem);
        var record = imported.Meeting!;
        Assert.Equal(meeting.Id, record.Id);
        Assert.Equal(meeting.Title, record.Title);
        Assert.Equal(meeting.Languages, record.Languages);
        Assert.Equal(meeting.StartedAt, record.StartedAt);
        Assert.Equal(meeting.EndedAt, record.EndedAt);

        var folder = there.MeetingFolder(theirs.Id, record.Id)!;
        Assert.Equal(
            File.ReadAllText(meeting.TranscriptPath!),
            File.ReadAllText(Path.Combine(folder, TranscriptLog.FileName)));
        Assert.True(File.Exists(Path.Combine(folder, WorkspaceStore.AudioFileName)));
        Assert.Equal("offer", File.ReadAllText(Path.Combine(folder, "attachments", "quotation.pdf")));

        var markdown = File.ReadAllText(Path.Combine(folder, MeetingBundle.TranscriptFileName));
        Assert.Contains("Die Toleranz ist 0,05 mm", markdown);
        Assert.Contains("公差是 0,05 毫米", markdown);

        // Listed, not merely written: a record the sidebar cannot show did not arrive.
        Assert.Contains(there.ListMeetings(theirs.Id).Meetings, m => m.Id == meeting.Id);
    }

    [Fact]
    public void ABundleWithoutAudioIsTheSizeOfItsTranscript()
    {
        var (store, workspace) = Opened("laptop");
        var meeting = Recorded(store, workspace, "Delivery call");

        Assert.Null(MeetingBundle.Write(store, meeting, includeAudio: false, Bundle("light")));
        Assert.Null(MeetingBundle.Write(store, meeting, includeAudio: true, Bundle("heavy")));

        Assert.DoesNotContain(WorkspaceStore.AudioFileName, Entries(Bundle("light")));
        Assert.Contains(WorkspaceStore.AudioFileName, Entries(Bundle("heavy")));
        Assert.True(
            new FileInfo(Bundle("light")).Length * 4 < new FileInfo(Bundle("heavy")).Length,
            "The transcript-only bundle is not measurably smaller than the one carrying an hour of audio.");
    }

    [Fact]
    public void SavingAsANewRecordKeepsTheOneAlreadyThere()
    {
        var (here, mine) = Opened("laptop");
        var meeting = Recorded(here, mine, "Delivery call");
        var bundle = Bundle("delivery");
        MeetingBundle.Write(here, meeting, includeAudio: false, bundle);

        var (there, theirs) = Opened("supplier");
        var first = MeetingBundle.Import(there, theirs.Id, bundle, asNewRecord: false).Meeting!;
        var second = MeetingBundle.Import(there, theirs.Id, bundle, asNewRecord: true).Meeting!;

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("Delivery call", first.Title);
        Assert.Equal("Delivery call (2)", second.Title);
        Assert.Equal(2, there.ListMeetings(theirs.Id).Meetings.Count);
        Assert.True(File.Exists(
            Path.Combine(there.MeetingFolder(theirs.Id, second.Id)!, TranscriptLog.FileName)));
    }

    [Fact]
    public void ABundleCannotWriteOutsideTheMeetingFolder()
    {
        var (here, mine) = Opened("laptop");
        var meeting = Recorded(here, mine, "Delivery call");
        var bundle = Bundle("tampered");
        MeetingBundle.Write(here, meeting, includeAudio: false, bundle);

        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open());
            writer.Write("not yours");
        }

        var (there, theirs) = Opened("supplier");
        var imported = MeetingBundle.Import(there, theirs.Id, bundle, asNewRecord: false);

        Assert.Null(imported.Problem);
        var folder = there.MeetingFolder(theirs.Id, imported.Meeting!.Id)!;
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "escaped.txt")));
    }

    [Fact]
    public void AFileThatIsNotABundleIsRefusedRatherThanImportedEmpty()
    {
        var (store, workspace) = Opened("laptop");
        var loose = Path.Combine(_root, "notes.txt");
        File.WriteAllText(loose, "just notes");

        var imported = MeetingBundle.Import(store, workspace.Id, loose, asNewRecord: false);

        Assert.Null(imported.Meeting);
        Assert.Equal(StoreProblemKind.Unreadable, imported.Problem!.Kind);
        Assert.Empty(store.ListMeetings(workspace.Id).Meetings);
    }
}
