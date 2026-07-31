namespace Kanal.Core.Models;

/// <summary>
/// Room ids: a human-readable time prefix plus a random suffix, so two hosts
/// starting in the same second never share a broadcast channel.
/// </summary>
public static class RoomIds
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static string New(DateTime now)
    {
        Span<char> suffix = stackalloc char[4];
        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
        return $"kanal-{now:HHmmss}-{new string(suffix)}";
    }
}
