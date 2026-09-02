namespace Kanal.Core.Relay;

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var standard = value.Replace('-', '+').Replace('_', '/');
        standard += new string('=', (4 - standard.Length % 4) % 4);
        return Convert.FromBase64String(standard);
    }
}
