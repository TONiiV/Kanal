using System.Text.Json;
using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

/// <summary>What must never happen to meeting records on disk: cross-talk, overwriting, silent loss.</summary>
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
            // a temp directory that outlives the test is the operating system's problem
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

        // a second store over the same registry is what the next launch sees
        var listing = Store().ListWorkspaces();

        var read = Assert.Single(listing.Workspaces);
        Assert.Empty(listing.Problems);
        Assert.Equal(made.Id, read.Id);
        Assert.Equal("ACME tooling", read.Name);
        Assert.Equal(folder, read.RootPath);
        Assert.Equal(made.CreatedAt, read.CreatedAt);
    }

    [Fact]
    public void AMeetingSurvivesARestartWithEveryFieldIntact()
    {
        var workspace = Created(Store().CreateWorkspace("ACME", Folder("acme")));
        var made = Created(Store().CreateMeeting(workspace.Id, "Tooling review"));

        var saved = made with
        {
            StartedAt = new DateTimeOffset(2026, 9, 7, 9, 30, 0, TimeSpan.FromHours(2)),
            EndedAt = new DateTimeOffset(2026, 9, 7, 10, 15, 0, TimeSpan.FromHours(2)),
            Languages = ["zh", "de", "pl"],
            TranscriptFileName = "transcript.md",
            AudioFileName = "room.wav",
        };
        Assert.Null(Store().SaveMeeting(saved).Problem);

        var read = Assert.Single(Store().ListMeetings(workspace.Id).Meetings);
        Assert.Equal(saved.Languages, read.Languages);
        // A record compares its list field by reference, so the languages are checked above and
        // held constant here. What this still catches is the one that matters: a field added to
        // MeetingRecord later and forgotten on the way to disk.
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

    /// <summary>
    /// Two meetings on the same day about the same thing is the ordinary case, not the odd one.
    /// Nothing about a title may decide where bytes land.
    /// </summary>
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

    /// <summary>A cleared text box is not a name, and must not become one.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsRefused(string blank)
    {
        var store = Store();

        Assert.NotNull(store.CreateWorkspace(blank, Folder("acme")).Problem);

        var workspace = Created(store.CreateWorkspace("ACME", Folder("beta")));
        Assert.NotNull(store.RenameWorkspace(workspace.Id, blank).Problem);
        Assert.NotNull(store.CreateMeeting(workspace.Id, blank).Problem);
    }

    /// <summary>
    /// A workspace on a drive that is not plugged in must read as unavailable, not as gone: an
    /// empty meeting list where a year of records used to be is the wrong thing to show.
    /// </summary>
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
        Assert.Equal(folder, problem.Path);
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

    /// <summary>
    /// A record written by a later version is not corrupt, and guessing at it is worse than
    /// saying so — reading half of it and then saving would discard whatever we did not know about.
    /// </summary>
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
        }
    }

    /// <summary>Adopting a folder that already holds a workspace keeps its identity and records.</summary>
    [Fact]
    public void AnExistingFolderIsAdoptedRatherThanReplaced()
    {
        var folder = Folder("acme");
        var original = Created(Store().CreateWorkspace("ACME", folder));
        var meeting = Created(Store().CreateMeeting(original.Id, "Tooling review"));

        File.Delete(Registry); // the application's list of workspaces is gone, the folder is not
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

    /// <summary>
    /// Forgetting is not deleting. The folder is the operator's, holds their transcripts, and may
    /// be a shared drive — removing it from the list is the most this is allowed to mean.
    /// </summary>
    [Fact]
    public void ForgettingAWorkspaceLeavesEveryFileWhereItWas()
    {
        var folder = Folder("acme");
        var store = Store();
        var workspace = Created(store.CreateWorkspace("ACME", folder));
        var meeting = Created(store.CreateMeeting(workspace.Id, "Tooling review"));
        var meetingFolder = store.MeetingFolder(workspace.Id, meeting.Id)!;

        Assert.True(store.ForgetWorkspace(workspace.Id));

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

        Assert.True(store.DeleteMeeting(workspace.Id, meeting.Id));

        Assert.False(Directory.Exists(folder));
        Assert.Equal(kept.Id, Assert.Single(Store().ListMeetings(workspace.Id).Meetings).Id);
    }

    [Fact]
    public void AnUnknownWorkspaceIsReportedRatherThanCreated()
    {
        var store = Store();

        var meetings = store.ListMeetings("no-such-workspace");

        Assert.Empty(meetings.Meetings);
        Assert.Equal(StoreProblemKind.NotFound, Assert.Single(meetings.Problems).Kind);
        Assert.NotNull(store.CreateMeeting("no-such-workspace", "Tooling review").Problem);
        Assert.False(store.DeleteMeeting("no-such-workspace", "whatever"));
    }

    /// <summary>
    /// The registry is the application's own file, so an unreadable one is not the operator's
    /// fault to solve — but it must not read as "you have no workspaces" either.
    /// </summary>
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
