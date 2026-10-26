namespace Kanal.Core.Updates;

public enum UpdateStatus
{
    Unknown,
    UpToDate,
    UpdateAvailable,
    Unsupported,
}

public sealed record UpdateResult(UpdateStatus Status, UpdateManifest? Manifest)
{
    public static UpdateResult Unknown { get; } = new(UpdateStatus.Unknown, null);
}
