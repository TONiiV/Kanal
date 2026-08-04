using Kanal.Core.Models;

namespace Kanal.Core.UnitTests;

public class RoomIdsTests
{
    [Fact]
    public void IdsDifferEvenWithinTheSameSecond()
    {
        // two hosts pressing Start in the same second must not share a broadcast channel
        var now = new DateTime(2026, 7, 31, 9, 30, 0);
        var ids = Enumerable.Range(0, 10).Select(_ => RoomIds.New(now)).ToHashSet();
        Assert.Equal(10, ids.Count);
    }

    [Fact]
    public void IdKeepsHumanReadableTimePrefix()
    {
        var id = RoomIds.New(new DateTime(2026, 7, 31, 9, 30, 5));
        Assert.Matches("^kanal-093005-[a-z0-9]{4}$", id);
    }
}
