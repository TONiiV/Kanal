using System.Text.Json;
using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

public class WorkspaceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-ws-" + Guid.NewGuid().ToString("N"));

    private string Registry => Path.Combine(_root, "workspaces.json");

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private WorkspaceStore Store() => new(Registry);

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

    private static Workspace Created(WorkspaceResult result)
    {
        Assert.Null(result.Problem);
        return Assert.IsType<Workspace>(result.Workspace);
    }

    private static MeetingRecord Created(MeetingResult result)
    {
        Assert.Null(result.Problem);
        return Assert.IsType<MeetingRecord>(result.Meeting);
    }

    [Fact]
    public void AWorkspaceSurvivesARestart()
    {
        var folder = Folder("acme");
        var made = Created(Store().CreateWorkspace("ACME tooling", folder));

        var listing = Store().ListWorkspaces();

        var read = Assert.Single(listing.Workspaces);
        Assert.Empty(listing.Problems);
        Assert.Equal(made.Id, read.Id);
        Assert.Equal("ACME tooling", read.Name);
        Assert.Equal(made.RootPath, read.RootPath);
        Assert.Equal(made.CreatedAt, read.CreatedAt);
    }

    [Fact]
    public void AMeetingSurvivesARestartWithEveryFieldIntact()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var made = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var meetingFolder = store.MeetingFolder(workspace.Id, made.Id)!;

        var saved = made with
        {
            StartedAt = new DateTimeOffset(2026, 9, 7, 9, 30, 0, TimeSpan.FromHours(2)),
            EndedAt = new DateTimeOffset(2026, 9, 7, 10, 15, 0, TimeSpan.FromHours(2)),
            Languages = ["zh", "de", "pl"],
            TranscriptPath = Path.Combine(meetingFolder, "transcript.md"),
            AudioPath = Path.Combine(meetingFolder, "room.wav"),
        };
        Assert.Null(store.SaveMeeting(saved).Problem);

        var read = Assert.Single(Store().ListMeetings(workspace.Id).Meetings);
        Assert.Equal(saved.Languages, read.Languages);
        // A record compares its list field by reference, so hold it constant and compare the rest.
        IReadOnlyList<string> same = [];
        Assert.Equal(saved with { Languages = same }, read with { Languages = same });
    }

    [Fact]
    public void TwoWorkspacesNeverSeeEachOthersMeetings()
    {
        var store = Store();
        var acme = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var beta = Created(store.CreateWorkspace("Beta", Folder("beta")));

        Created(store.CreateMeeting(acme.Id, "Tooling review"));
        Created(store.CreateMeeting(beta.Id, "Tooling review"));
        Created(store.CreateMeeting(beta.Id, "Delivery dates"));

        Assert.Equal(["Tooling review"], Titles(store, acme.Id));
        Assert.Equal(["Delivery dates", "Tooling review"], Titles(store, beta.Id));
    }

    private static string[] Titles(WorkspaceStore store, string workspaceId) =>
        [.. store.ListMeetings(workspaceId).Meetings.Select(m => m.Title).Order()];

    // Creation is exempt from the uniqueness rule the rename paths carry: two records made
    // before either is recorded both read "New meeting", and numbering the placeholder would
    // survive into the default title.
    [Fact]
    public void TwoMeetingsWithOneTitleGetSeparateHomes()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));

        var first = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var second = Created(store.CreateMeeting(workspace.Id, "Tooling review"));

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(
            store.MeetingFolder(workspace.Id, first.Id),
            store.MeetingFolder(workspace.Id, second.Id));
        Assert.Equal(2, store.ListMeetings(workspace.Id).Meetings.Count);
    }

    [Fact]
    public void RenamingAWorkspaceAndAMeetingPersists()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Untitled"));

        Assert.Null(store.RenameWorkspace(workspace.Id, "ACME tooling").Problem);
        Assert.Null(store.RenameMeeting(workspace.Id, meeting.Id, "Delivery dates").Problem);

        var fresh = Store();
        Assert.Equal("ACME tooling", Assert.Single(fresh.ListWorkspaces().Workspaces).Name);
        Assert.Equal("Delivery dates", Assert.Single(fresh.ListMeetings(workspace.Id).Meetings).Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsRefused(string blank)
    {
        var store = Store();

        Assert.Equal(StoreProblemKind.Invalid, store.CreateWorkspace(blank, Folder("acme")).Problem!.Kind);

        var workspace = Created(store.CreateWorkspace("ACME", Folder("beta")));
        Assert.Equal(StoreProblemKind.Invalid, store.RenameWorkspace(workspace.Id, blank).Problem!.Kind);
        Assert.Equal(StoreProblemKind.Invalid, store.CreateMeeting(workspace.Id, blank).Problem!.Kind);
    }

    [Fact]
    public void AWorkspaceWhoseFolderHasVanishedIsReportedRatherThanDropped()
    {
        var folder = Folder("acme");
        var workspace = Created(Store().CreateWorkspace("ACME", folder));
        Directory.Delete(folder, recursive: true);

        var listing = Store().ListWorkspaces();

        Assert.Equal(workspace.Id, Assert.Single(listing.Workspaces).Id);
        var problem = Assert.Single(listing.Problems);
        Assert.Equal(StoreProblemKind.FolderMissing, problem.Kind);
        Assert.Equal(workspace.RootPath, problem.Subject);
    }

    [Fact]
    public void MeetingsOfAVanishedWorkspaceAreReportedRatherThanEmpty()
    {
        var folder = Folder("acme");
        var workspace = Created(Store().CreateWorkspace("ACME", folder));
        Directory.Delete(folder, recursive: true);

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Empty(listing.Meetings);
        Assert.Equal(StoreProblemKind.FolderMissing, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void AMissingMeetingsFolderIsReportedRatherThanEmpty()
    {
        var workspace = Created(Store().CreateWorkspace("ACME", Folder("acme")));
        Directory.Delete(Path.Combine(workspace.RootPath, "meetings"));

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Empty(listing.Meetings);
        Assert.Equal(StoreProblemKind.FolderMissing, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void ACorruptRecordIsReportedAndItsNeighboursStillList()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var broken = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        Created(store.CreateMeeting(workspace.Id, "Delivery dates"));

        File.WriteAllText(
            Path.Combine(store.MeetingFolder(workspace.Id, broken.Id)!, WorkspaceStore.MeetingFileName),
            "{ not json at all");

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Equal("Delivery dates", Assert.Single(listing.Meetings).Title);
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void ARecordFromALaterSchemaIsRefusedRatherThanGuessedAt()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));

        var path = Path.Combine(
            store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName);
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;
        document["schemaVersion"] = JsonSerializer.SerializeToElement(WorkspaceStore.SchemaVersion + 1);
        File.WriteAllText(path, JsonSerializer.Serialize(document));

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Empty(listing.Meetings);
        Assert.Equal(StoreProblemKind.UnsupportedVersion, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void ARecordWithoutASchemaVersionIsRefusedRatherThanGuessedAt()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var path = Path.Combine(
            store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName);
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;
        document.Remove("schemaVersion");
        File.WriteAllText(path, JsonSerializer.Serialize(document));

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Empty(listing.Meetings);
        Assert.Equal(StoreProblemKind.UnsupportedVersion, Assert.Single(listing.Problems).Kind);
    }

    [Theory]
    [InlineData("createdAt")]
    [InlineData("languages")]
    public void AMeetingMissingARequiredFieldIsRefused(string field)
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var path = Path.Combine(
            store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName);
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;
        document.Remove(field);
        File.WriteAllText(path, JsonSerializer.Serialize(document));

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Empty(listing.Meetings);
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void AStoredArtifactPathIsRefusedRatherThanListed()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var path = Path.Combine(
            store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName);
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;
        document["transcriptFileName"] = JsonSerializer.SerializeToElement("../../elsewhere.md");
        File.WriteAllText(path, JsonSerializer.Serialize(document));

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Empty(listing.Meetings);
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void EveryRecordCarriesTheSchemaItWasWrittenWith()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));

        foreach (var path in new[]
                 {
                     Path.Combine(workspace.RootPath, WorkspaceStore.WorkspaceFileName),
                     Path.Combine(store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName),
                 })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                WorkspaceStore.SchemaVersion,
                document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.False(document.RootElement.TryGetProperty("complete", out _));
        }
    }

    [Fact]
    public void AnExistingFolderIsAdoptedRatherThanReplaced()
    {
        var folder = Folder("acme");
        var original = Created(Store().CreateWorkspace("ACME", folder));
        var meeting = Created(Store().CreateMeeting(original.Id, "Tooling review"));

        File.Delete(Registry);
        var adopted = Created(Store().OpenWorkspace(folder));

        Assert.Equal(original.Id, adopted.Id);
        Assert.Equal("ACME", adopted.Name);
        Assert.Equal(meeting.Id, Assert.Single(Store().ListMeetings(adopted.Id).Meetings).Id);
    }

    [Fact]
    public void AWorkspaceIsNotAdoptedTwice()
    {
        var folder = Folder("acme");
        var store = Store();
        var first = Created(store.CreateWorkspace("ACME", folder));

        var again = Created(store.OpenWorkspace(folder));

        Assert.Equal(first.Id, again.Id);
        Assert.Single(store.ListWorkspaces().Workspaces);
    }

    [Fact]
    public void AdoptingAWorkspaceKeepsTheNameItAlreadyHad()
    {
        var folder = Folder("acme");
        Created(Store().CreateWorkspace("ACME tooling", folder));
        File.Delete(Registry);

        var adopted = Created(Store().CreateWorkspace("Something else entirely", folder));

        Assert.Equal("ACME tooling", adopted.Name);
    }

    [Fact]
    public void ForgettingAWorkspaceLeavesEveryFileWhereItWas()
    {
        var folder = Folder("acme");
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", folder));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var meetingFolder = store.MeetingFolder(workspace.Id, meeting.Id)!;

        Assert.Null(store.ForgetWorkspace(workspace.Id));

        Assert.Empty(Store().ListWorkspaces().Workspaces);
        Assert.True(File.Exists(Path.Combine(folder, WorkspaceStore.WorkspaceFileName)));
        Assert.True(File.Exists(Path.Combine(meetingFolder, WorkspaceStore.MeetingFileName)));
    }

    [Fact]
    public void DeletingAMeetingTakesItsFolderWithIt()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var kept = Created(store.CreateMeeting(workspace.Id, "Delivery dates"));
        var folder = store.MeetingFolder(workspace.Id, meeting.Id)!;

        Assert.Null(store.DeleteMeeting(workspace.Id, meeting.Id));

        Assert.False(Directory.Exists(folder));
        Assert.Equal(kept.Id, Assert.Single(Store().ListMeetings(workspace.Id).Meetings).Id);
    }

    [Fact]
    public void DeletingAMeetingTakesItsTranscriptAndItsRecordingWithIt()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var folder = store.MeetingFolder(workspace.Id, meeting.Id)!;
        var transcript = Path.Combine(folder, "transcript.jsonl");
        var audio = Path.Combine(folder, "audio.wav");
        File.WriteAllText(transcript, """{"text":"Guten Tag"}""");
        File.WriteAllBytes(audio, [0x52, 0x49, 0x46, 0x46]);
        Created(store.SaveMeeting(meeting with { TranscriptPath = transcript, AudioPath = audio }));

        Assert.Null(store.DeleteMeeting(workspace.Id, meeting.Id));

        Assert.False(File.Exists(transcript));
        Assert.False(File.Exists(audio));
    }

    [Fact]
    public void AnUnknownWorkspaceIsReportedRatherThanCreated()
    {
        var store = Store();

        var meetings = store.ListMeetings("no-such-workspace");

        Assert.Empty(meetings.Meetings);
        Assert.Equal(StoreProblemKind.NotFound, Assert.Single(meetings.Problems).Kind);
        Assert.NotNull(store.CreateMeeting("no-such-workspace", "Tooling review").Problem);
        Assert.Equal(StoreProblemKind.NotFound, store.DeleteMeeting("no-such-workspace", "whatever")!.Kind);
        Assert.Equal(StoreProblemKind.NotFound, store.ForgetWorkspace("no-such-workspace")!.Kind);
    }

    [Fact]
    public void AnUnreadableRegistryIsReportedRatherThanTreatedAsEmpty()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Registry, "{ not json at all");

        var listing = Store().ListWorkspaces();

        Assert.Empty(listing.Workspaces);
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void AMissingRegistryIsSimplyNoWorkspacesYet()
    {
        var listing = Store().ListWorkspaces();

        Assert.Empty(listing.Workspaces);
        Assert.Empty(listing.Problems);
    }

    [Fact]
    public void AHalfWrittenRecordIsReportedRatherThanListedAsAPhantom()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var path = Path.Combine(
            store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName);

        File.WriteAllText(path, $$"""{"schemaVersion":{{WorkspaceStore.SchemaVersion}}}""");

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Empty(listing.Meetings);
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void AHalfWrittenRegistryIsReportedRatherThanListedAsPhantoms()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Registry,
            $$"""{"schemaVersion":{{WorkspaceStore.SchemaVersion}},"workspaces":[{"name":"ACME"}]}""");

        var listing = Store().ListWorkspaces();

        Assert.Empty(listing.Workspaces);
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void ARecordThatCannotBeReadIsReportedRatherThanThrown()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var path = Path.Combine(
            store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName);

        File.Delete(path);
        Directory.CreateDirectory(path);

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Empty(listing.Meetings);
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void AMeetingFolderWithNoRecordIsReported()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        Directory.CreateDirectory(Path.Combine(workspace.RootPath, "meetings", "orphan"));

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Empty(listing.Meetings);
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void AnUnreadableWorkspaceFileIsReportedRatherThanAdopted()
    {
        var folder = Folder("acme");
        File.WriteAllText(Path.Combine(folder, WorkspaceStore.WorkspaceFileName), "{ not json at all");

        var result = Store().OpenWorkspace(folder);

        Assert.Null(result.Workspace);
        Assert.Equal(StoreProblemKind.Unreadable, result.Problem!.Kind);
        Assert.Empty(Store().ListWorkspaces().Workspaces);
    }

    [Fact]
    public void RenamingAMeetingThatIsNotThereIsReported()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));

        var result = store.RenameMeeting(workspace.Id, "no-such-meeting", "Delivery dates");

        Assert.Null(result.Meeting);
        Assert.Equal(StoreProblemKind.NotFound, result.Problem!.Kind);
    }

    [Fact]
    public void AMeetingIdThatWouldEscapeItsWorkspaceIsRefused()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var kept = Path.Combine(workspace.RootPath, WorkspaceStore.WorkspaceFileName);
        var folder = Path.Combine(workspace.RootPath, "meetings", "odd");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, WorkspaceStore.MeetingFileName),
            $$"""{"schemaVersion":{{WorkspaceStore.SchemaVersion}},"id":"..","title":"Tooling review"}""");

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Empty(listing.Meetings);
        // and Delete on it would have resolved to the workspace root
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
        Assert.Equal(StoreProblemKind.Invalid, store.DeleteMeeting(workspace.Id, "..")!.Kind);
        Assert.Null(store.MeetingFolder(workspace.Id, ".."));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void ACopiedWorkspaceFolderIsReportedRatherThanReplacingTheOriginal()
    {
        var store = Store();
        var original = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(original.Id, "Tooling review"));
        var copy = Folder("acme-backup");
        foreach (var file in Directory.EnumerateFiles(original.RootPath))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));

        var result = store.OpenWorkspace(copy);

        Assert.Null(result.Workspace);
        Assert.Equal(StoreProblemKind.Invalid, result.Problem!.Kind);
        var listed = Assert.Single(Store().ListWorkspaces().Workspaces);
        Assert.Equal(original.RootPath, listed.RootPath);
        Assert.Equal(meeting.Id, Assert.Single(Store().ListMeetings(original.Id).Meetings).Id);
    }

    [Fact]
    public void AMeetingsFolderThatCannotBeListedIsReported()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var meetings = Path.Combine(workspace.RootPath, "meetings");

        if (OperatingSystem.IsWindows())
            return; // no mode bits to take away; the guard under test is platform-independent

        File.SetUnixFileMode(meetings, UnixFileMode.None);
        try
        {
            if (Directory.EnumerateDirectories(meetings).Any())
                return;
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            var listing = Store().ListMeetings(workspace.Id);

            Assert.Empty(listing.Meetings);
            Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
        }
        finally
        {
            File.SetUnixFileMode(
                meetings, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void AWorkspaceThatMovedIsOpenedRatherThanMistakenForACopy()
    {
        var was = Folder("acme");
        var workspace = Created(Store().CreateWorkspace("ACME", was));
        var meeting = Created(Store().CreateMeeting(workspace.Id, "Tooling review"));
        var now = Path.Combine(_root, "moved");
        Directory.Move(was, now);

        var moved = Created(Store().OpenWorkspace(now));

        Assert.Equal(workspace.Id, moved.Id);
        var listed = Assert.Single(Store().ListWorkspaces().Workspaces);
        Assert.Equal(moved.RootPath, listed.RootPath);
        Assert.EndsWith("moved", listed.RootPath);
        Assert.Equal(meeting.Id, Assert.Single(Store().ListMeetings(workspace.Id).Meetings).Id);
    }

    /// <summary>A folder picker hands back a trailing separator; the same folder is not a copy of itself.</summary>
    [Fact]
    public void TheSameFolderSpeltDifferentlyIsStillTheSameWorkspace()
    {
        var folder = Folder("acme");
        var workspace = Created(Store().CreateWorkspace("ACME", folder));

        var again = Created(Store().OpenWorkspace(folder + Path.DirectorySeparatorChar));

        Assert.Equal(workspace.Id, again.Id);
        Assert.Single(Store().ListWorkspaces().Workspaces);
    }

    [Fact]
    public void ADuplicatedMeetingFolderIsReportedRatherThanListedTwice()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var copy = Path.Combine(workspace.RootPath, "meetings", "copy-of-it");
        Directory.CreateDirectory(copy);
        File.Copy(
            Path.Combine(store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName),
            Path.Combine(copy, WorkspaceStore.MeetingFileName));

        var listing = Store().ListMeetings(workspace.Id);

        Assert.Equal(meeting.Id, Assert.Single(listing.Meetings).Id);
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
    }

    [Fact]
    public void AnArtifactPathOutsideTheMeetingFolderIsRefused()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));

        var result = store.SaveMeeting(
            meeting with { TranscriptPath = Path.Combine("..", "..", "elsewhere.md") });

        Assert.Null(result.Meeting);
        Assert.Equal(StoreProblemKind.Invalid, result.Problem!.Kind);
        Assert.Null(Assert.Single(Store().ListMeetings(workspace.Id).Meetings).TranscriptPath);
    }

    [Fact]
    public void AMeetingWithoutLanguagesIsRefusedBeforeItCanBecomeUnreadable()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));

        var result = store.SaveMeeting(meeting with { Languages = null! });

        Assert.Null(result.Meeting);
        Assert.Equal(StoreProblemKind.Invalid, result.Problem!.Kind);
        Assert.Single(Store().ListMeetings(workspace.Id).Meetings);
    }

    [Fact]
    public void AMeetingWithoutACreatedTimestampIsRefusedBeforeItCanBecomeUnreadable()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));

        var result = store.SaveMeeting(meeting with { CreatedAt = default });

        Assert.Null(result.Meeting);
        Assert.Equal(StoreProblemKind.Invalid, result.Problem!.Kind);
        Assert.Single(Store().ListMeetings(workspace.Id).Meetings);
    }

    [Fact]
    public void AFolderAlreadyOnTheListIsNotMadeIntoASecondWorkspace()
    {
        var folder = Folder("acme");
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", folder));
        Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        File.Delete(Path.Combine(folder, WorkspaceStore.WorkspaceFileName));

        var result = store.CreateWorkspace("Beta", folder);

        Assert.Null(result.Workspace);
        Assert.Equal(StoreProblemKind.Invalid, result.Problem!.Kind);
        Assert.Equal(workspace.Id, Assert.Single(Store().ListWorkspaces().Workspaces).Id);
    }

    [Fact]
    public void OneFolderReachedByTwoSpellingsIsOneWorkspace()
    {
        var folder = Folder("acme");
        var workspace = Created(Store().CreateWorkspace("ACME", folder));
        var meeting = Created(Store().CreateMeeting(workspace.Id, "Tooling review"));
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, _root);
        var sameFolder = Path.Combine(link, "acme");

        var again = Created(Store().OpenWorkspace(sameFolder));

        Assert.Equal(workspace.Id, again.Id);
        Assert.Single(Store().ListWorkspaces().Workspaces);
        Assert.Equal(meeting.Id, Assert.Single(Store().ListMeetings(workspace.Id).Meetings).Id);
    }

    [Fact]
    public void ARenameSurvivesTheFolderComingBack()
    {
        var folder = Folder("acme");
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", folder));
        var marker = Path.Combine(folder, WorkspaceStore.WorkspaceFileName);
        var away = File.ReadAllText(marker);

        File.Delete(marker);
        Directory.Delete(folder, recursive: true);
        Assert.Null(store.RenameWorkspace(workspace.Id, "ACME tooling").Problem);
        Directory.CreateDirectory(folder);
        File.WriteAllText(marker, away);

        Assert.Equal("ACME tooling", Created(Store().OpenWorkspace(folder)).Name);
        Assert.Equal("ACME tooling", Assert.Single(Store().ListWorkspaces().Workspaces).Name);
    }

    [Fact]
    public void RenamingThroughADuplicatedFolderIsRefused()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var copy = Path.Combine(workspace.RootPath, "meetings", "copy-of-it");
        Directory.CreateDirectory(copy);
        File.Copy(
            Path.Combine(store.MeetingFolder(workspace.Id, meeting.Id)!, WorkspaceStore.MeetingFileName),
            Path.Combine(copy, WorkspaceStore.MeetingFileName));

        var result = store.RenameMeeting(workspace.Id, "copy-of-it", "Delivery dates");

        Assert.Null(result.Meeting);
        Assert.Equal(StoreProblemKind.Unreadable, result.Problem!.Kind);
        Assert.Equal("Tooling review", Assert.Single(Store().ListMeetings(workspace.Id).Meetings).Title);
    }

    [Fact]
    public void OneBadRowDoesNotHideTheRestOfTheList()
    {
        var store = Store();
        var acme = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var beta = Created(store.CreateWorkspace("Beta", Folder("beta")));

        var registry = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Registry))!;
        var rows = registry["workspaces"].EnumerateArray()
            .Select(r => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.GetRawText())!)
            .ToList();
        rows.Insert(1, new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("Rubble") });
        registry["workspaces"] = JsonSerializer.SerializeToElement(rows);
        File.WriteAllText(Registry, JsonSerializer.Serialize(registry));

        var listing = Store().ListWorkspaces();

        Assert.Equal([acme.Id, beta.Id], listing.Workspaces.Select(w => w.Id));
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
    }

    /// <summary>A Windows-shaped traversal is still a traversal when it is written on a Unix host.</summary>
    [Theory]
    [InlineData("../elsewhere.md")]
    [InlineData("..\\..\\elsewhere.md")]
    [InlineData("..")]
    [InlineData("   ")]
    public void ARelativeOrTraversalArtifactPathIsRefused(string name)
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));

        var result = store.SaveMeeting(meeting with { TranscriptPath = name });

        Assert.Equal(StoreProblemKind.Invalid, result.Problem!.Kind);
    }

    [Fact]
    public void AWorkspaceCreatedInAFolderThatDidNotExistCanBeOpenedAgain()
    {
        var folder = Path.Combine(_root, "not-yet", "acme");
        Directory.CreateDirectory(_root);
        var made = Created(Store().CreateWorkspace("ACME", folder));

        var again = Created(Store().OpenWorkspace(folder));

        Assert.Equal(made.Id, again.Id);
        Assert.Single(Store().ListWorkspaces().Workspaces);
        Assert.True(Path.IsPathRooted(made.RootPath));
    }

    [Fact]
    public void AFolderCannotBeTakenOverByAnotherWorkspacesMarker()
    {
        var store = Store();
        var acme = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var beta = Created(store.CreateWorkspace("Beta", Folder("beta")));
        File.Copy(
            Path.Combine(acme.RootPath, WorkspaceStore.WorkspaceFileName),
            Path.Combine(beta.RootPath, WorkspaceStore.WorkspaceFileName),
            overwrite: true);
        Directory.Delete(acme.RootPath, recursive: true);

        var result = store.OpenWorkspace(beta.RootPath);

        Assert.Null(result.Workspace);
        Assert.Equal(StoreProblemKind.Invalid, result.Problem!.Kind);
        Assert.Equal(
            new[] { acme.Id, beta.Id }.Order(),
            Store().ListWorkspaces().Workspaces.Select(w => w.Id).Order());
    }

    [Fact]
    public void RenamingThroughAnIncompleteRowIsRefused()
    {
        var store = Store();
        Created(store.CreateWorkspace("ACME", Folder("acme")));
        var registry = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Registry))!;
        registry["workspaces"] = JsonSerializer.SerializeToElement(
            new[] { new Dictionary<string, string?> { ["id"] = "rubble", ["name"] = "Rubble" } });
        File.WriteAllText(Registry, JsonSerializer.Serialize(registry));

        var result = Store().RenameWorkspace("rubble", "Something");

        Assert.Null(result.Workspace);
        Assert.Equal(StoreProblemKind.NotFound, result.Problem!.Kind);
    }

    [Fact]
    public void RenamingAMeetingOntoAnotherOnesTitleIsRefused()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var second = Created(store.CreateMeeting(workspace.Id, "Delivery dates"));

        var result = store.RenameMeeting(workspace.Id, second.Id, " tooling review ");

        Assert.Null(result.Meeting);
        Assert.Equal(StoreProblemKind.TitleTaken, result.Problem!.Kind);
        Assert.Equal(
            new[] { "Delivery dates", "Tooling review" }, Titles(store, workspace.Id));
    }

    [Fact]
    public void AMeetingKeepingItsOwnTitleIsNotACollision()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));

        Assert.Null(store.RenameMeeting(workspace.Id, meeting.Id, "Tooling review").Problem);
    }

    [Fact]
    public void AGeneratedTitleTakesTheNextFreeNumberInsteadOfBeingRefused()
    {
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", Folder("acme")));
        var mine = Created(store.CreateMeeting(workspace.Id, "New meeting"));
        Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        Created(store.CreateMeeting(workspace.Id, "Tooling review 2"));

        var free = store.FreeTitle(workspace.Id, "Tooling review", mine.Id);

        Assert.Equal("Tooling review 3", free);
        Assert.Null(store.RenameMeeting(workspace.Id, mine.Id, free).Problem);
    }

    [Fact]
    public void TheFirstLaunchGetsAWorkspaceWithoutBeingAsked()
    {
        var folder = Path.Combine(_root, "Kanal");

        var made = Created(Store().EnsureWorkspace("Kanal", folder));
        var second = Created(Store().EnsureWorkspace("Kanal", folder));

        Assert.Equal("Kanal", made.Name);
        Assert.Equal(made.Id, second.Id);
        Assert.Single(Store().ListWorkspaces().Workspaces);
    }

    [Fact]
    public void AWorkspaceCannotBeCreatedOnAPathThatIsAFile()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(file, "");

        var result = Store().CreateWorkspace("ACME", file);

        Assert.Null(result.Workspace);
        Assert.NotNull(result.Problem);
        Assert.Empty(Store().ListWorkspaces().Workspaces);
    }
}
