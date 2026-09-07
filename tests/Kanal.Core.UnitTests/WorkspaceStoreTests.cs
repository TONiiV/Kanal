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
        Assert.Equal(folder, problem.Subject);
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
        Directory.CreateDirectory(path); // a folder where a file belongs fails the read on every platform

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

    /// <summary>
    /// A meeting id becomes a folder name. A hand-edited ".." would list as an ordinary meeting
    /// and then, on Delete, take the whole workspace and every transcript in it.
    /// </summary>
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
        Assert.Equal(StoreProblemKind.Unreadable, Assert.Single(listing.Problems).Kind);
        Assert.Equal(StoreProblemKind.Invalid, store.DeleteMeeting(workspace.Id, "..")!.Kind);
        Assert.Null(store.MeetingFolder(workspace.Id, ".."));
        Assert.True(File.Exists(kept));
    }

    /// <summary>
    /// A copied folder — a restored backup, a share mounted twice — carries the original's id.
    /// Registering it by id alone would point the one row at the copy and lose the original.
    /// </summary>
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
                return; // running as root, where a mode of 000 stops nothing
        }
        catch (UnauthorizedAccessException)
        {
            // the folder is unreadable, which is the state this test needs
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
