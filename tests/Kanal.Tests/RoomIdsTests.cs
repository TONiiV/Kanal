using Kanal.Core.Models;

namespace Kanal.Tests;

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
        Assert.Matches("^kanal-093005-[A-Za-z0-9_-]{22}$", id);
    }

    [Fact]
    public void IdCarriesAtLeast128BitsOfCryptographicCapability()
    {
        var id = RoomIds.New(new DateTime(2026, 7, 31, 9, 30, 5));
        var token = id[("kanal-093005-".Length)..];

        Assert.Equal(22, token.Length); // unpadded base64url of 16 bytes
        Assert.DoesNotContain('=', token);
    }
}
