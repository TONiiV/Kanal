namespace Kanal.Core.Updates;

public static class UpdatePolicy
{
    public static UpdateStatus Evaluate(ReleaseVersion current, UpdateManifest manifest)
    {
        if (current < manifest.MinimumSupported)
            return UpdateStatus.Unsupported;
        return current < manifest.Latest ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate;
    }
}
