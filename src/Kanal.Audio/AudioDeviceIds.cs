using System.Security.Cryptography;
using System.Text;

namespace Kanal.Audio;

public static class AudioDeviceIds
{
    public static string Hash(string? id) =>
        id is null ? "default" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12];
}
