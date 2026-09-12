using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Diagnostics;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public sealed partial class MeetingFilesViewModel(
    WorkspaceStore store, Func<Task<string?>> chooseFile) : ViewModelBase
{
    private const string AttachmentsFolderName = "attachments";
    private const string LogCategory = "workspace";

    private static readonly StringComparer Alphabetical = StringComparer.OrdinalIgnoreCase;

    private MeetingRecord? _meeting;

    public ObservableCollection<MeetingFileViewModel> Entries { get; } = new();

    [ObservableProperty]
    private string _problemNote = "";

    public bool HasMeeting => _meeting is not null;

    public bool IsEmpty => Entries.Count == 0;

    public string EmptyNote => L[HasMeeting ? "files.empty" : "files.nomeeting"];

    public void Show(MeetingRecord? meeting)
    {
        _meeting = meeting;
        OnPropertyChanged(nameof(HasMeeting));
        ImportCommand.NotifyCanExecuteChanged();
        Refresh();
    }

    public void Refresh()
    {
        var found = new List<MeetingFileViewModel>();
        if (Folder() is { } folder && Directory.Exists(folder))
            Collect(folder, 0, found);

        Entries.Clear();
        foreach (var entry in found)
            Entries.Add(entry);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyNote));
    }

    [RelayCommand(CanExecute = nameof(HasMeeting))]
    private async Task ImportAsync()
    {
        if (Folder() is not { } folder || await chooseFile() is not { } source)
            return;

        var attachments = Path.Combine(folder, AttachmentsFolderName);
        try
        {
            Directory.CreateDirectory(attachments);
            File.Copy(source, Free(attachments, Path.GetFileName(source)));
            ProblemNote = "";
        }
        catch (Exception ex)
        {
            ProblemNote = L.Format("workspace.importfailed", ex.Message);
            Log.Error(LogCategory, $"{source} could not be brought into {attachments}.", ex);
        }

        Refresh();
    }

    private string? Folder() =>
        _meeting is { } meeting ? store.MeetingFolder(meeting.WorkspaceId, meeting.Id) : null;

    private void Collect(string folder, int depth, List<MeetingFileViewModel> into)
    {
        List<string> folders, files;
        try
        {
            folders = [.. Directory.EnumerateDirectories(folder).OrderBy(Path.GetFileName, Alphabetical)];
            files = [.. Directory.EnumerateFiles(folder).OrderBy(Path.GetFileName, Alphabetical)];
        }
        catch (Exception ex)
        {
            ProblemNote = L.Format("files.unreadable", ex.Message);
            Log.Warning(LogCategory, $"{folder} could not be listed.", ex);
            return;
        }

        foreach (var path in folders)
        {
            into.Add(MeetingFileViewModel.Folder(Path.GetFileName(path), depth));

            // A linked folder can point at an ancestor of itself, and walking into it never returns.
            if (new DirectoryInfo(path).LinkTarget is null)
                Collect(path, depth + 1, into);
        }

        foreach (var path in files)
            into.Add(MeetingFileViewModel.File(Path.GetFileName(path), depth, LengthOf(path)));
    }

    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    // Never overwritten: two suppliers send "quotation.pdf", and the second import must not
    // silently take the place of a file the operator can already see listed above the button.
    private static string Free(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var copy = 2; Path.Exists(target); copy++)
            target = Path.Combine(folder, $"{stem} ({copy}){extension}");

        return target;
    }

    private static Localizer L => Localizer.Instance;
}
