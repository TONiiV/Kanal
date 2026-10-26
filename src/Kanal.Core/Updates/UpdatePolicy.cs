namespace Kanal.Core.Updates;

public static class UpdatePolicy
{
    public static UpdateStatus Evaluate(ReleaseVersion current, UpdateManifest manifest) =>
        UpdateStatus.Unknown;
}
