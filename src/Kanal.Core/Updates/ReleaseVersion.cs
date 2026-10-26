namespace Kanal.Core.Updates;

public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        return false;
    }

    public int CompareTo(ReleaseVersion other) => 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => false;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => false;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => false;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => false;

    public override string ToString() => "";
}
