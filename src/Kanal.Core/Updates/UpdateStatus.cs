namespace Kanal.Core.Updates;

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    Unsupported,
    Unknown,
}

public sealed record UpdateResult(UpdateStatus Status, UpdateManifest? Manifest)
{
    public static UpdateResult Unknown { get; } = new(UpdateStatus.Unknown, null);
}
