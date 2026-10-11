using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

public class StoreProblemsTests
{
    [Fact]
    public void A_logged_unwritable_problem_joins_the_detail_and_the_cause()
    {
        var problem = StoreProblems.LoggedUnwritable("p", "It failed.", new IOException("disk full"));

        Assert.Equal(new StoreProblem(StoreProblemKind.Unwritable, "p", "It failed. disk full"), problem);
    }
}
