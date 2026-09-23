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

        var started = new DateTimeOffset(2026, 9, 8, 14, 30, 0, TimeSpan.Zero);
        var ended = new DateTimeOffset(2026, 9, 8, 15, 12, 0, TimeSpan.Zero);
        return store.SaveMeeting(meeting with
        {
            StartedAt = started,
            EndedAt = ended,
            Languages = ["de", "zh"],
            Segments =
            [
                new(Path.Combine(folder, TranscriptLog.FileName),
                    Path.Combine(folder, WorkspaceStore.AudioFileName), started, ended),
            ],
        }).Meeting!;
    }

    private static MeetingRecord Continued(WorkspaceStore store, Workspace workspace, string title)
    {
        var meeting = Recorded(store, workspace, title);
        var folder = store.MeetingFolder(workspace.Id, meeting.Id)!;
        File.WriteAllText(
            Path.Combine(folder, MeetingSegment.TranscriptFileName(2)),
            """
            {"id":"u1","speakerTag":"S1","tStartMs":0,"tEndMs":900,"srcLang":"de","srcText":"Nach der Pause: Liefertermin","revision":1,"state":"Final","codeSwitch":false,"speakerConfidence":0.9,"translations":{"zh":"休息之后：交期"}}
            """);
        var pcm = new byte[200_000];
        new Random(4403).NextBytes(pcm);
        File.WriteAllBytes(Path.Combine(folder, MeetingSegment.AudioFileName(2)), pcm);

        var resumed = meeting.EndedAt!.Value.AddMinutes(15);
        return store.SaveMeeting(meeting with
        {
            EndedAt = resumed.AddMinutes(20),
            Segments =
            [
                .. meeting.Segments,
                new(Path.Combine(folder, MeetingSegment.TranscriptFileName(2)),
                    Path.Combine(folder, MeetingSegment.AudioFileName(2)), resumed, resumed.AddMinutes(20)),
            ],
        }).Meeting!;
    }

    private string Crafted(string name, string manifest, params (string Entry, string Text)[] entries)
    {
        Directory.CreateDirectory(_root);
        var path = Bundle(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries.Prepend((MeetingBundle.ManifestFileName, manifest)))
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }

        return path;
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
            File.ReadAllText(meeting.Segments[0].TranscriptPath),
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
    public void EveryRunTravelsAndItsRecordingOnlyWhenTicked()
    {
        var (here, mine) = Opened("laptop");
        var meeting = Continued(here, mine, "Delivery call");

        Assert.Null(MeetingBundle.Write(here, meeting, includeAudio: false, Bundle("light")));
        Assert.Null(MeetingBundle.Write(here, meeting, includeAudio: true, Bundle("heavy")));

        Assert.Contains("transcript-2.jsonl", Entries(Bundle("light")));
        Assert.DoesNotContain(Entries(Bundle("light")), e => e.EndsWith(".wav", StringComparison.Ordinal));
        Assert.Contains("audio.wav", Entries(Bundle("heavy")));
        Assert.Contains("audio-2.wav", Entries(Bundle("heavy")));

        var (there, theirs) = Opened("supplier");
        var imported = MeetingBundle.Import(there, theirs.Id, Bundle("heavy"), asNewRecord: false);

        Assert.Null(imported.Problem);
        var record = Assert.Single(there.ListMeetings(theirs.Id).Meetings);
        Assert.Equal(meeting.Id, record.Id);
        Assert.Equal(2, record.Segments.Count);
        var folder = there.MeetingFolder(theirs.Id, record.Id)!;
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(meeting.Segments[i].StartedAt, record.Segments[i].StartedAt);
            Assert.Equal(meeting.Segments[i].EndedAt, record.Segments[i].EndedAt);
            Assert.Equal(folder, Path.GetDirectoryName(record.Segments[i].TranscriptPath));
            Assert.Equal(
                File.ReadAllBytes(meeting.Segments[i].TranscriptPath),
                File.ReadAllBytes(record.Segments[i].TranscriptPath));
            Assert.Equal(
                File.ReadAllBytes(meeting.Segments[i].AudioPath!),
                File.ReadAllBytes(record.Segments[i].AudioPath!));
        }

        var markdown = File.ReadAllText(Path.Combine(folder, MeetingBundle.TranscriptFileName));
        Assert.True(
            markdown.IndexOf("Die Toleranz ist 0,05 mm", StringComparison.Ordinal)
            < markdown.IndexOf("Nach der Pause: Liefertermin", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunWhoseRecordingWasLeftBehindArrivesWithoutOne()
    {
        var (here, mine) = Opened("laptop");
        var meeting = Continued(here, mine, "Delivery call");
        MeetingBundle.Write(here, meeting, includeAudio: false, Bundle("light"));

        var (there, theirs) = Opened("supplier");
        var record = MeetingBundle.Import(there, theirs.Id, Bundle("light"), asNewRecord: false).Meeting!;

        Assert.Equal(2, record.Segments.Count);
        Assert.All(record.Segments, run => Assert.Null(run.AudioPath));
    }

    [Fact]
    public void ARunWhoseTranscriptDidNotTravelKeepsItsPlace()
    {
        var bundle = Crafted(
            "gap",
            $$"""
            {"schemaVersion":{{WorkspaceStore.MeetingSchemaVersion}},"id":"a1b2c3d4e5f60718",
             "title":"Delivery call","languages":["de"],
             "segments":[{"transcript":"transcript.jsonl","audio":null},
                         {"transcript":"transcript-2.jsonl","audio":null}]}
            """,
            ("transcript-2.jsonl", ""));
        var (store, workspace) = Opened("supplier");

        var record = MeetingBundle.Import(store, workspace.Id, bundle, asNewRecord: false).Meeting!;

        Assert.Equal(
            [TranscriptLog.FileName, "transcript-2.jsonl"],
            record.Segments.Select(s => Path.GetFileName(s.TranscriptPath)));
    }

    [Fact]
    public void ABundleWrittenBeforeRunsExistedImportsAsOneRun()
    {
        var bundle = Crafted(
            "legacy",
            """
            {"schemaVersion":1,"id":"a1b2c3d4e5f60718","title":"Delivery call","languages":["de"],
             "startedAt":"2026-09-08T14:30:00+00:00","endedAt":"2026-09-08T15:12:00+00:00"}
            """,
            (TranscriptLog.FileName,
                """{"id":"u1","speakerTag":"S1","tStartMs":0,"tEndMs":900,"srcLang":"de","srcText":"Guten Tag","revision":1,"state":"Final","codeSwitch":false,"speakerConfidence":0.9,"translations":{}}"""),
            (WorkspaceStore.AudioFileName, "RIFF"));
        var (store, workspace) = Opened("supplier");

        var imported = MeetingBundle.Import(store, workspace.Id, bundle, asNewRecord: false);

        Assert.Null(imported.Problem);
        var run = Assert.Single(imported.Meeting!.Segments);
        var folder = store.MeetingFolder(workspace.Id, imported.Meeting.Id)!;
        Assert.Equal(Path.Combine(folder, TranscriptLog.FileName), run.TranscriptPath);
        Assert.Equal(Path.Combine(folder, WorkspaceStore.AudioFileName), run.AudioPath);
        Assert.Equal(imported.Meeting.StartedAt, run.StartedAt);
    }

    /// <summary>The manifest's run list comes from another machine, exactly as its entry names do.</summary>
    [Theory]
    [InlineData("../escaped.jsonl")]
    [InlineData(@"..\escaped.jsonl")]
    [InlineData("sub/escaped.jsonl")]
    [InlineData("<absolute>")]
    public void ABundleWhoseRunsNameAPathIsRefused(string name)
    {
        var target = name == "<absolute>" ? Path.Combine(_root, "escaped.jsonl") : name;
        var bundle = Crafted(
            "tampered",
            $$"""
            {"schemaVersion":{{WorkspaceStore.MeetingSchemaVersion}},"id":"a1b2c3d4e5f60718",
             "title":"Delivery call","languages":["de"],
             "segments":[{"transcript":{{System.Text.Json.JsonSerializer.Serialize(target)}},"audio":null}]}
            """,
            (target.Replace('\\', '/'), "not yours"));
        var (store, workspace) = Opened("supplier");

        var imported = MeetingBundle.Import(store, workspace.Id, bundle, asNewRecord: false);

        Assert.Null(imported.Meeting);
        Assert.NotNull(imported.Problem);
        Assert.Empty(store.ListMeetings(workspace.Id).Meetings);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.jsonl")));
        Assert.False(File.Exists(Path.Combine(_root, "supplier", "Kanal", "escaped.jsonl")));
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
