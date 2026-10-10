using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Diagnostics;
using Kanal.Core.Workspaces;
using Kanal.Host.Controls;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public enum ProjectRemoval
{
    ListOnly,

    DeleteFiles,
}

public sealed partial class ProjectSettingsViewModel : ViewModelBase
{
    private const string LogCategory = "workspace";

    private readonly WorkspaceStore _store;

    public ProjectSettingsViewModel(WorkspaceStore store, Workspace project, bool recording)
    {
        _store = store;
        Project = project;
        _name = project.Name;
        _glyph = project.IconGlyph;
        _imagePath = project.IconPath;
        CanRemove = !recording;
    }

    public Workspace Project { get; }

    public IReadOnlyList<string> Glyphs => ProjectIcons.Glyphs;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomIcon))]
    private string? _glyph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomIcon))]
    [NotifyPropertyChangedFor(nameof(ImageFileName))]
    private string? _imagePath;

    [ObservableProperty]
    private string _problem = "";

    public bool IsCustomIcon => Glyph is not null || ImagePath is not null;

    public string? ImageFileName => ImagePath is null ? null : Path.GetFileName(ImagePath);

    public bool CanRemove { get; }

    public string RemoveBlockedNote => CanRemove ? "" : L["workspace.remove.recording"];

    public bool Changed { get; private set; }

    public Func<Task<string?>>? ChooseImageFile { get; set; }

    public Func<string, Task<ProjectRemoval?>>? ConfirmRemoval { get; set; }

    public event Action? Finished;

    [RelayCommand]
    private void ChooseGlyph(string glyph)
    {
        Glyph = glyph;
        ImagePath = null;
    }

    [RelayCommand]
    private async Task ChooseImageAsync()
    {
        if (ChooseImageFile is null || await ChooseImageFile() is not { } path)
            return;

        if (new FileInfo(path) is { Exists: true, Length: > WorkspaceStore.MaxIconBytes })
        {
            Problem = L["workspace.icon.toolarge"];
            return;
        }

        Problem = "";
        ImagePath = path;
        Glyph = null;
    }

    [RelayCommand]
    private void ResetIcon()
    {
        Glyph = null;
        ImagePath = null;
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (Name.Trim() != Project.Name && Refused(_store.RenameWorkspace(Project.Id, Name).Problem))
            return;

        var icon = ImagePath is not null && ImagePath != Project.IconPath
                ? _store.SetWorkspaceIconFile(Project.Id, ImagePath)
            : ImagePath is null && Glyph is not null && Glyph != Project.IconGlyph
                ? _store.SetWorkspaceIcon(Project.Id, Glyph)
            : !IsCustomIcon && (Project.IconGlyph is not null || Project.IconFile is not null)
                ? _store.ResetWorkspaceIcon(Project.Id)
            : null;

        Changed = true;
        if (Refused(icon?.Problem))
            return;

        Finished?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        // An unwired confirmation reads as a refusal, as for a meeting.
        if (!CanRemove || ConfirmRemoval is null || await ConfirmRemoval(Project.Name) is not { } removal)
            return;

        var problem = removal == ProjectRemoval.DeleteFiles
            ? _store.DeleteWorkspace(Project.Id)
            : _store.ForgetWorkspace(Project.Id);
        if (Refused(problem))
            return;

        Changed = true;
        Finished?.Invoke();
    }

    private bool Refused(StoreProblem? problem)
    {
        if (problem is null)
            return false;

        Problem = problem.Detail;
        Log.Warning(LogCategory, $"{problem.Kind} on {problem.Subject}: {problem.Detail}");
        return true;
    }

    private static Localizer L => Localizer.Instance;
}
