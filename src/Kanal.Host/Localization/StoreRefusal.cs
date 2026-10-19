using Kanal.Core.Diagnostics;
using Kanal.Core.Workspaces;

namespace Kanal.Host.Localization;

public static class StoreRefusal
{
    public static string Report(StoreProblem problem, string logCategory, string? key = null)
    {
        Log.Warning(logCategory, $"{problem.Kind} on {problem.Subject}: {problem.Detail}");
        return Localizer.Instance.Format(key ?? KeyOf(problem.Kind), problem.Subject);
    }

    private static string KeyOf(StoreProblemKind kind) => kind switch
    {
        StoreProblemKind.TitleTaken => "workspace.titletaken",
        StoreProblemKind.TooLarge => "workspace.icon.toolarge",
        StoreProblemKind.WrongType => "workspace.icon.badtype",
        StoreProblemKind.NotThisWorkspace => "workspace.remove.notthisproject",
        StoreProblemKind.Invalid => "workspace.refused.invalid",
        StoreProblemKind.NotFound => "workspace.refused.notfound",
        StoreProblemKind.FolderMissing => "workspace.refused.foldermissing",
        StoreProblemKind.Unreadable or StoreProblemKind.RecordMissing or StoreProblemKind.RecordDamaged
            => "workspace.refused.unreadable",
        StoreProblemKind.FolderTaken => "workspace.refused.foldertaken",
        StoreProblemKind.Misfiled => "workspace.refused.misfiled",
        StoreProblemKind.Unwritable => "workspace.refused.unwritable",
        StoreProblemKind.UnsupportedVersion => "workspace.refused.unsupportedversion",
        _ => "workspace.refused.other",
    };
}
