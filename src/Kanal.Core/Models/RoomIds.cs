using System.Security.Cryptography;
using Kanal.Core.Relay;

namespace Kanal.Core.Models;

/// <summary>
/// Room ids are bearer capabilities: the readable time helps the operator, while
/// 128 cryptographically random bits make the public Realtime topic infeasible to enumerate.
/// </summary>
public static class RoomIds
{
    public static string New(DateTime now)
    {
        Span<byte> capability = stackalloc byte[16];
        RandomNumberGenerator.Fill(capability);
        return $"kanal-{now:HHmmss}-{Base64Url.Encode(capability)}";
    }
}
