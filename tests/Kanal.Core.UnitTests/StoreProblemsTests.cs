using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

public class StoreProblemsTests
{
    public static TheoryData<StoreProblem, StoreProblemKind, string> FixedProblems => new()
    {
        { StoreProblems.NotAMeetingId("m"), StoreProblemKind.Invalid, "That is not a meeting id." },
        { StoreProblems.EmptyFile("m"), StoreProblemKind.Unreadable, "The file is empty." },
        { StoreProblems.IncompleteRecord("m"), StoreProblemKind.Unreadable, "The record is missing fields." },
        { StoreProblems.IncompleteRow("m"), StoreProblemKind.Unreadable, "A row of the list is missing fields." },
        { StoreProblems.NoRecord("m"), StoreProblemKind.Unreadable, "That meeting folder holds no record." },
        { StoreProblems.NotABundle("m"), StoreProblemKind.Unreadable, "That file is not a Kanal meeting bundle." },
        { StoreProblems.IncompleteManifest("m"), StoreProblemKind.Unreadable, "The bundle's manifest is missing fields." },
        { StoreProblems.UnsupportedWorkspaceSchema("m", 7), StoreProblemKind.UnsupportedVersion, "Unsupported workspace schema 7." },
        { StoreProblems.UnsupportedBundleSchema("m", 7), StoreProblemKind.UnsupportedVersion, "Unsupported bundle schema 7." },
        { StoreProblems.FolderMissing("m"), StoreProblemKind.FolderMissing, "That folder is not there." },
        { StoreProblems.MeetingsFolderMissing("m"), StoreProblemKind.FolderMissing, "The meetings folder is not there." },
        { StoreProblems.TitleTaken("m"), StoreProblemKind.TitleTaken, "Another meeting in this workspace is already called \"m\"." },
        { StoreProblems.WrongIconType("m"), StoreProblemKind.WrongType, "A project icon must be a PNG, JPEG, ICO, BMP or WebP image." },
        { StoreProblems.NoSuchImage("m"), StoreProblemKind.NotFound, "That image is not there." },
        { StoreProblems.IconTooLarge("m"), StoreProblemKind.TooLarge, "A project icon must be 2 MB or smaller." },
    };

    [Theory]
    [MemberData(nameof(FixedProblems))]
    public void A_fixed_problem_keeps_its_kind_subject_and_detail(
        StoreProblem problem, StoreProblemKind kind, string detail)
    {
        Assert.Equal(kind, problem.Kind);
        Assert.Equal("m", problem.Subject);
        Assert.Equal(detail, problem.Detail);
    }

    [Fact]
    public void A_caller_supplied_detail_passes_through_unchanged()
    {
        Assert.Equal(new StoreProblem(StoreProblemKind.Unreadable, "p", "boom"), StoreProblems.Unreadable("p", "boom"));
        Assert.Equal(new StoreProblem(StoreProblemKind.Unwritable, "p", "boom"), StoreProblems.Unwritten("p", "boom"));
    }

    [Fact]
    public void An_unwritable_problem_joins_the_detail_and_the_cause()
    {
        var problem = StoreProblems.Unwritable("p", "It failed.", new IOException("disk full"));

        Assert.Equal(new StoreProblem(StoreProblemKind.Unwritable, "p", "It failed. disk full"), problem);
    }
}
