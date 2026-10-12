using System.Text.Json;
using Kanal.Core.Models;
using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

public class WorkspaceStoreRecoveryTests : IDisposable
{
    private const string BaseTitle = "Recovered meeting 2026-10-11 09:30";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-ws-recover-" + Guid.NewGuid().ToString("N"));

    private WorkspaceStore Store() => new(Path.Combine(_root, "workspaces.json"));

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

    private Workspace NewWorkspace(WorkspaceStore store)
    {
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        var result = store.CreateWorkspace("ACME", folder);
        Assert.Null(result.Problem);
        return result.Workspace!;
    }

    private static string MeetingsOf(Workspace workspace) => Path.Combine(workspace.RootPath, "meetings");

    private static string SetAsideOf(Workspace workspace) => Path.Combine(workspace.RootPath, "kanal-set-aside");

    private static string Folder(Workspace workspace, string name)
    {
        var path = Path.Combine(MeetingsOf(workspace), name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static Utterance Said(
        string id, string lang, long startMs, long? endMs = null, params string[] translated) =>
        new(id, "S1", startMs, endMs, lang, "text", 1, UtteranceState.Final, false, 1.0,
            translated.ToDictionary(l => l, _ => "translated"));

    private static string WriteTranscript(string folder, params Utterance[] utterances)
    {
        var path = Path.Combine(folder, TranscriptLog.FileName);
        using (var writer = new TranscriptLogWriter(path, _ => { }))
            foreach (var utterance in utterances)
                writer.Append(utterance);

        return path;
    }

    private static Dictionary<string, int> Contents(string root)
    {
        var counts = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var key = Convert.ToHexString(File.ReadAllBytes(file));
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    private static void AssertNothingLost(Dictionary<string, int> before, Dictionary<string, int> after)
    {
        foreach (var (content, count) in before)
            Assert.True(after.GetValueOrDefault(content) >= count, "A file was deleted or overwritten.");
    }

    private static MeetingRecord Meeting(MeetingResult result)
    {
        Assert.Null(result.Problem);
        return result.Meeting!;
    }

    [Fact]
    public void KindsNameTheMeetingFolderAsSubject()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var missing = Folder(workspace, "missing");
        var damaged = Folder(workspace, "damaged");
        File.WriteAllText(Path.Combine(damaged, WorkspaceStore.MeetingFileName), "{ not json");
        var healthy = Meeting(store.CreateMeeting(workspace.Id, "Tooling review"));
        var copy = Folder(workspace, "copy");
        File.Copy(
            Path.Combine(store.MeetingFolder(workspace.Id, healthy.Id)!, WorkspaceStore.MeetingFileName),
            Path.Combine(copy, WorkspaceStore.MeetingFileName));

        var problems = Store().ListMeetings(workspace.Id).Problems.ToDictionary(p => p.Kind);

        Assert.Equal(missing, problems[StoreProblemKind.RecordMissing].Subject);
        Assert.Equal(damaged, problems[StoreProblemKind.RecordDamaged].Subject);
        Assert.Equal(copy, problems[StoreProblemKind.Misfiled].Subject);
    }

    [Fact]
    public void AMeetingFolderWithATranscriptIsRebuiltFromIt()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        var transcript = WriteTranscript(
            folder,
            Said("u1", "de", 5_000, 6_000, "zh", "pl"),
            Said("u2", "zh", 9_000, 10_000, "de"),
            Said("u3", "pl", 65_000, 70_000));
        File.WriteAllBytes(Path.Combine(folder, WorkspaceStore.AudioFileName), [1, 2, 3]);
        var ended = new DateTime(2026, 10, 10, 14, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(transcript, ended);
        File.SetLastWriteTimeUtc(Path.Combine(folder, WorkspaceStore.AudioFileName), ended.AddSeconds(-3));

        var result = store.RecoverMeeting(workspace.Id, folder, BaseTitle);

        Assert.Null(result.Problem);
        Assert.Null(result.SetAsidePath);
        var record = result.Recovered!;
        Assert.Equal("lost1", record.Id);
        Assert.Equal(BaseTitle, record.Title);
        Assert.Equal(["de", "zh", "pl"], record.Languages);
        Assert.Equal(new DateTimeOffset(ended), record.EndedAt);
        Assert.Equal(new DateTimeOffset(ended.AddSeconds(-65)), record.StartedAt);
        Assert.Equal(record.StartedAt, record.CreatedAt);
        var run = Assert.Single(record.Runs);
        Assert.Equal(transcript, run.TranscriptPath);
        Assert.Equal(Path.Combine(folder, WorkspaceStore.AudioFileName), run.AudioPath);

        var listing = Store().ListMeetings(workspace.Id);
        Assert.Empty(listing.Problems);
        Assert.Equal(record.Id, Assert.Single(listing.Meetings).Id);
    }

    [Fact]
    public void ARebuiltMeetingWithoutAudioHasNoAudioPath()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "de", 0));

        var record = store.RecoverMeeting(workspace.Id, folder, BaseTitle).Recovered!;

        Assert.Null(Assert.Single(record.Runs).AudioPath);
    }

    [Fact]
    public void ARebuiltMeetingKeepsEveryRunEvenAfterASkippedNumber()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "de", 0));
        var third = Path.Combine(folder, MeetingRun.TranscriptFileName(3));
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "scratch")).FullName;
        File.Move(WriteTranscript(scratch, Said("u1", "pl", 0)), third);
        File.WriteAllBytes(Path.Combine(folder, MeetingRun.AudioFileName(3)), [0x52, 0x49, 0x46, 0x46]);

        var record = store.RecoverMeeting(workspace.Id, folder, BaseTitle).Recovered!;

        Assert.Equal(
            [Path.Combine(folder, TranscriptLog.FileName), third],
            record.Runs.Select(r => r.TranscriptPath));
        Assert.Null(record.Runs[0].AudioPath);
        Assert.Equal(Path.Combine(folder, MeetingRun.AudioFileName(3)), record.Runs[1].AudioPath);
        Assert.Equal(["de", "pl"], record.Languages);
    }

    [Fact]
    public void ADamagedRecordIsRenamedBeforeTheNewOneIsWritten()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "de", 0));
        File.WriteAllText(Path.Combine(folder, WorkspaceStore.MeetingFileName), "{ not json");

        var result = store.RecoverMeeting(workspace.Id, folder, BaseTitle);

        Assert.NotNull(result.Recovered);
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(folder, "meeting.json.damaged")));
        Assert.Equal("lost1", Assert.Single(Store().ListMeetings(workspace.Id).Meetings).Id);
    }

    [Fact]
    public void ATakenDamagedNameGetsANumberAndNothingIsOverwritten()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "de", 0));
        File.WriteAllText(Path.Combine(folder, "meeting.json.damaged"), "first");
        File.WriteAllText(Path.Combine(folder, WorkspaceStore.MeetingFileName), "second");

        Assert.NotNull(store.RecoverMeeting(workspace.Id, folder, BaseTitle).Recovered);

        Assert.Equal("first", File.ReadAllText(Path.Combine(folder, "meeting.json.damaged")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(folder, "meeting.json.damaged.2")));
    }

    [Fact]
    public void ARecordThatIsAFolderIsRenamedToo()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "de", 0));
        Directory.CreateDirectory(Path.Combine(folder, WorkspaceStore.MeetingFileName));

        Assert.NotNull(store.RecoverMeeting(workspace.Id, folder, BaseTitle).Recovered);

        Assert.True(Directory.Exists(Path.Combine(folder, "meeting.json.damaged")));
        Assert.True(File.Exists(Path.Combine(folder, WorkspaceStore.MeetingFileName)));
    }

    [Fact]
    public void TheRecoveredTitleIsMadeUniqueWithFreeTitle()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        Meeting(store.CreateMeeting(workspace.Id, BaseTitle));
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "de", 0));

        var record = store.RecoverMeeting(workspace.Id, folder, BaseTitle).Recovered!;

        Assert.Equal(BaseTitle + " 2", record.Title);
    }

    [Fact]
    public void AFolderWithNoReadableUtteranceIsSetAside()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        File.WriteAllText(Path.Combine(folder, TranscriptLog.FileName), "{ half a line");
        File.WriteAllText(Path.Combine(folder, WorkspaceStore.MeetingFileName), "{ not json");
        var before = Contents(workspace.RootPath);

        var result = store.RecoverMeeting(workspace.Id, folder, BaseTitle);

        Assert.Null(result.Problem);
        Assert.Null(result.Recovered);
        var target = Path.Combine(SetAsideOf(workspace), "lost1");
        Assert.Equal(target, result.SetAsidePath);
        Assert.False(Directory.Exists(folder));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(target, WorkspaceStore.MeetingFileName)));
        AssertNothingLost(before, Contents(workspace.RootPath));
        var listing = Store().ListMeetings(workspace.Id);
        Assert.Empty(listing.Meetings);
        Assert.Empty(listing.Problems);
    }

    [Fact]
    public void AnEmptyFolderIsSetAside()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");

        var result = store.RecoverMeeting(workspace.Id, folder, BaseTitle);

        Assert.Equal(Path.Combine(SetAsideOf(workspace), "lost1"), result.SetAsidePath);
        Assert.True(Directory.Exists(result.SetAsidePath));
    }

    [Fact]
    public void AFolderNameThatIsNotAnIdIsSetAsideEvenWithATranscript()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "my meeting");
        WriteTranscript(folder, Said("u1", "de", 0));

        var result = store.RecoverMeeting(workspace.Id, folder, BaseTitle);

        Assert.Equal(Path.Combine(SetAsideOf(workspace), "my meeting"), result.SetAsidePath);
        Assert.True(File.Exists(Path.Combine(result.SetAsidePath!, TranscriptLog.FileName)));
    }

    [Fact]
    public void ASetAsideNameClashGetsANumber()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var taken = Path.Combine(SetAsideOf(workspace), "lost1");
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "note.txt"), "earlier");
        var folder = Folder(workspace, "lost1");
        File.WriteAllText(Path.Combine(folder, "note.txt"), "later");

        var result = store.RecoverMeeting(workspace.Id, folder, BaseTitle);

        Assert.Equal(taken + "-2", result.SetAsidePath);
        Assert.Equal("earlier", File.ReadAllText(Path.Combine(taken, "note.txt")));
        Assert.Equal("later", File.ReadAllText(Path.Combine(taken + "-2", "note.txt")));
    }

    [Fact]
    public void OnlyADirectSubfolderOfMeetingsIsAccepted()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var nested = Path.Combine(Folder(workspace, "lost1"), "inner");
        Directory.CreateDirectory(nested);
        var outside = Path.Combine(_root, "elsewhere", "lost2");
        Directory.CreateDirectory(outside);
        var sibling = Path.Combine(workspace.RootPath, "lost3");
        Directory.CreateDirectory(sibling);
        string[] refused =
        [
            workspace.RootPath,
            MeetingsOf(workspace),
            nested,
            outside,
            sibling,
            Path.Combine(MeetingsOf(workspace), "lost1", ".."),
            Path.Combine(MeetingsOf(workspace), "..", "lost3"),
        ];
        var before = Contents(_root);

        foreach (var path in refused)
        {
            Assert.Equal(StoreProblemKind.Invalid, store.RecoverMeeting(workspace.Id, path, BaseTitle).Problem!.Kind);
            Assert.Equal(StoreProblemKind.Invalid, store.ReidentifyMeeting(workspace.Id, path).Problem!.Kind);
        }

        Assert.True(Directory.Exists(nested));
        Assert.True(Directory.Exists(outside));
        Assert.True(Directory.Exists(sibling));
        Assert.False(Directory.Exists(SetAsideOf(workspace)));
        AssertNothingLost(before, Contents(_root));
    }

    [Fact]
    public void AFolderThatIsNotThereIsReported()
    {
        var store = Store();
        var workspace = NewWorkspace(store);

        var result = store.RecoverMeeting(workspace.Id, Path.Combine(MeetingsOf(workspace), "gone"), BaseTitle);

        Assert.Equal(StoreProblemKind.NotFound, result.Problem!.Kind);
    }

    [Fact]
    public void AnUnknownWorkspaceIsReported()
    {
        var store = Store();

        Assert.Equal(
            StoreProblemKind.NotFound, store.RecoverMeeting("nosuchworkspace", "x", BaseTitle).Problem!.Kind);
        Assert.Equal(
            StoreProblemKind.NotFound, store.ReidentifyMeeting("nosuchworkspace", "x").Problem!.Kind);
    }

    [Fact]
    public void ABlankBaseTitleIsRefusedBeforeAnythingMoves()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");

        var result = store.RecoverMeeting(workspace.Id, folder, "  ");

        Assert.Equal(StoreProblemKind.Invalid, result.Problem!.Kind);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void AHealthyMeetingIsNotRecovered()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var meeting = Meeting(store.CreateMeeting(workspace.Id, "Tooling review"));
        var folder = store.MeetingFolder(workspace.Id, meeting.Id)!;
        var before = Contents(workspace.RootPath);

        var result = store.RecoverMeeting(workspace.Id, folder, BaseTitle);

        Assert.Equal(StoreProblemKind.Invalid, result.Problem!.Kind);
        Assert.Equal(before, Contents(workspace.RootPath));
    }

    [Fact]
    public void AMisfiledMeetingIsNotRecovered()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var meeting = Meeting(store.CreateMeeting(workspace.Id, "Tooling review"));
        var copy = Folder(workspace, "copy");
        File.Copy(
            Path.Combine(store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName),
            Path.Combine(copy, WorkspaceStore.MeetingFileName));

        var result = store.RecoverMeeting(workspace.Id, copy, BaseTitle);

        Assert.Equal(StoreProblemKind.Misfiled, result.Problem!.Kind);
        Assert.True(File.Exists(Path.Combine(copy, WorkspaceStore.MeetingFileName)));
    }

    [Fact]
    public void ARecordOfAnotherFormatIsLeftAsItIs()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "de", 0));
        var record = Path.Combine(folder, WorkspaceStore.MeetingFileName);
        File.WriteAllText(record, """{"schemaVersion":2,"id":"lost1"}""");

        var result = store.RecoverMeeting(workspace.Id, folder, BaseTitle);

        Assert.Equal(StoreProblemKind.UnsupportedVersion, result.Problem!.Kind);
        Assert.Equal("""{"schemaVersion":2,"id":"lost1"}""", File.ReadAllText(record));
        Assert.False(File.Exists(record + ".damaged"));
    }

    [Fact]
    public void AMisfiledRecordTakesTheNameOfItsFolder()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var meeting = Meeting(store.CreateMeeting(workspace.Id, "Tooling review"));
        var original = store.MeetingFolder(workspace.Id, meeting.Id)!;
        var copy = Folder(workspace, "copy-of-it");
        File.Copy(
            Path.Combine(original, WorkspaceStore.MeetingFileName),
            Path.Combine(copy, WorkspaceStore.MeetingFileName));

        var result = store.ReidentifyMeeting(workspace.Id, copy);

        Assert.Null(result.Problem);
        Assert.Null(result.SetAsidePath);
        var record = result.Recovered!;
        Assert.Equal("copy-of-it", record.Id);
        Assert.Equal("Tooling review 2", record.Title);
        var listing = Store().ListMeetings(workspace.Id);
        Assert.Empty(listing.Problems);
        Assert.Equal(new[] { "copy-of-it", meeting.Id }.Order(), listing.Meetings.Select(m => m.Id).Order());
    }

    [Fact]
    public void ReidentifyKeepsEveryOtherFieldAndTheOriginalFolder()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var meeting = Meeting(store.CreateMeeting(workspace.Id, "Tooling review"));
        var original = store.MeetingFolder(workspace.Id, meeting.Id)!;
        var originalText = File.ReadAllText(Path.Combine(original, WorkspaceStore.MeetingFileName));
        var copy = Folder(workspace, "copy-of-it");
        File.WriteAllText(Path.Combine(copy, WorkspaceStore.MeetingFileName), originalText);

        store.ReidentifyMeeting(workspace.Id, copy);

        Assert.Equal(originalText, File.ReadAllText(Path.Combine(original, WorkspaceStore.MeetingFileName)));
        var stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(Path.Combine(copy, WorkspaceStore.MeetingFileName)))!;
        var first = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(originalText)!;
        foreach (var (key, value) in first.Where(p => p.Key is not ("id" or "title")))
            Assert.Equal(value.GetRawText(), stored[key].GetRawText());
        Assert.Equal("copy-of-it", stored["id"].GetString());
        Assert.Equal("Tooling review 2", stored["title"].GetString());
    }

    [Fact]
    public void ReidentifySetsAsideAMisfiledFolderWhoseNameIsNotAnId()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var meeting = Meeting(store.CreateMeeting(workspace.Id, "Tooling review"));
        var copy = Folder(workspace, "copy of it");
        var record = Path.Combine(copy, WorkspaceStore.MeetingFileName);
        File.Copy(Path.Combine(store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName), record);
        File.WriteAllText(Path.Combine(copy, "note.txt"), "kept");
        var before = Contents(workspace.RootPath);

        var result = store.ReidentifyMeeting(workspace.Id, copy);

        Assert.Null(result.Problem);
        Assert.Null(result.Recovered);
        var target = Path.Combine(SetAsideOf(workspace), "copy of it");
        Assert.Equal(target, result.SetAsidePath);
        Assert.False(Directory.Exists(copy));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "note.txt")));
        AssertNothingLost(before, Contents(workspace.RootPath));
        var listing = Store().ListMeetings(workspace.Id);
        Assert.Empty(listing.Problems);
        Assert.Equal(meeting.Id, Assert.Single(listing.Meetings).Id);
    }

    [Fact]
    public void ARebuiltMeetingLeavesOutTheUnknownLanguage()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "und", 0, null, "de"), Said("u2", "zh", 1_000, null, "und"));

        var record = store.RecoverMeeting(workspace.Id, folder, BaseTitle).Recovered!;

        Assert.Equal(["de", "zh"], record.Languages);
    }

    [Fact]
    public void AFolderWithOnlyTheUnknownLanguageIsSetAside()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "und", 0));

        var result = store.RecoverMeeting(workspace.Id, folder, BaseTitle);

        Assert.Equal(Path.Combine(SetAsideOf(workspace), "lost1"), result.SetAsidePath);
    }

    [Fact]
    public void AFailedMoveLeavesTheFolderAndEveryFileWhereTheyWere()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        File.WriteAllText(Path.Combine(folder, "note.txt"), "kept");
        File.WriteAllText(SetAsideOf(workspace), "in the way");
        var before = Contents(workspace.RootPath);

        var result = store.RecoverMeeting(workspace.Id, folder, BaseTitle);

        Assert.Equal(StoreProblemKind.Unwritable, result.Problem!.Kind);
        Assert.Null(result.SetAsidePath);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(folder, "note.txt")));
        Assert.Equal(before, Contents(workspace.RootPath));
    }

    [Fact]
    public void ReidentifyRefusesAHealthyOrDamagedMeeting()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var meeting = Meeting(store.CreateMeeting(workspace.Id, "Tooling review"));
        var damaged = Folder(workspace, "damaged");
        File.WriteAllText(Path.Combine(damaged, WorkspaceStore.MeetingFileName), "{ not json");

        Assert.Equal(
            StoreProblemKind.Invalid,
            store.ReidentifyMeeting(workspace.Id, store.MeetingFolder(workspace.Id, meeting.Id)!).Problem!.Kind);
        Assert.Equal(
            StoreProblemKind.RecordDamaged, store.ReidentifyMeeting(workspace.Id, damaged).Problem!.Kind);
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(damaged, WorkspaceStore.MeetingFileName)));
    }

    [Fact]
    public void ARecordThatCannotBeOpenedIsUnreadableAndNothingIsRenamedOrMoved()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var meeting = Meeting(store.CreateMeeting(workspace.Id, "Tooling review"));
        var folder = store.MeetingFolder(workspace.Id, meeting.Id)!;
        WriteTranscript(folder, Said("u1", "de", 0));
        var record = Path.Combine(folder, WorkspaceStore.MeetingFileName);
        var text = File.ReadAllText(record);
        var before = Contents(workspace.RootPath);

        using (new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var problem = Assert.Single(Store().ListMeetings(workspace.Id).Problems);
            Assert.Equal(StoreProblemKind.Unreadable, problem.Kind);
            Assert.Equal(folder, problem.Subject);
            Assert.Equal(
                StoreProblemKind.Unreadable, store.RecoverMeeting(workspace.Id, folder, BaseTitle).Problem!.Kind);
            Assert.Equal(StoreProblemKind.Unreadable, store.ReidentifyMeeting(workspace.Id, folder).Problem!.Kind);
        }

        Assert.Equal(text, File.ReadAllText(record));
        Assert.False(File.Exists(record + ".damaged"));
        Assert.False(Directory.Exists(SetAsideOf(workspace)));
        Assert.Equal(before, Contents(workspace.RootPath));
        Assert.Equal(meeting.Id, Assert.Single(Store().ListMeetings(workspace.Id).Meetings).Id);
    }

    [Fact]
    public void LanguageCodesThatDifferOnlyInCaseAreListedOnceWithTheFirstSpelling()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "de", 0, null, "zh"), Said("u2", "ZH", 1_000, null, "DE"));

        var record = store.RecoverMeeting(workspace.Id, folder, BaseTitle).Recovered!;

        Assert.Equal(["de", "zh"], record.Languages);
    }

    [Fact]
    public void RecoveringARecordKeepsEveryFileInTheProject()
    {
        var store = Store();
        var workspace = NewWorkspace(store);
        var folder = Folder(workspace, "lost1");
        WriteTranscript(folder, Said("u1", "de", 0));
        File.WriteAllBytes(Path.Combine(folder, WorkspaceStore.AudioFileName), [9, 9]);
        File.WriteAllText(Path.Combine(folder, WorkspaceStore.MeetingFileName), "{ not json");
        var before = Contents(workspace.RootPath);

        Assert.NotNull(store.RecoverMeeting(workspace.Id, folder, BaseTitle).Recovered);

        AssertNothingLost(before, Contents(workspace.RootPath));
    }
}
