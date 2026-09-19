using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class WorkspaceSidebarView : UserControl
{
    public WorkspaceSidebarView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm)
                return;

            vm.Sidebar.ChooseWorkspaceFolder = ChooseFolderAsync;
            vm.Sidebar.ChooseFileToImport = ChooseFileAsync;
            vm.Sidebar.ChooseExportPath = ChooseTargetAsync;
            vm.Sidebar.ConfirmDeleteMeeting = ConfirmDeleteAsync;
            vm.Sidebar.ConfirmExportBundle = ConfirmExportBundleAsync;
            vm.Sidebar.ChooseImportChoice = ChooseImportChoiceAsync;
        };
    }

    private void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: MeetingItemViewModel meeting })
            return;

        // Posted: the editor is not visible yet while the flyout closes, and cannot take focus.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (MeetingList.ContainerFromItem(meeting) is not Control row)
                    return;

                if (row.GetLogicalDescendants().OfType<TextBox>()
                        .FirstOrDefault(box => box.Name == "MeetingRowEditor") is not { } editor)
                    return;

                editor.Focus();
                editor.SelectAll();
            },
            DispatcherPriority.Background);
    }

    private void OnRowEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MeetingItemViewModel meeting)
            return;

        if (e.Key == Key.Enter)
            meeting.CommitRenameCommand.Execute(null);
        else if (e.Key == Key.Escape)
            meeting.CancelRenameCommand.Execute(null);
        else
            return;

        e.Handled = true;
    }

    private void OnRowEditorLostFocus(object? sender, RoutedEventArgs e) =>
        ((sender as Control)?.DataContext as MeetingItemViewModel)?.CommitRenameCommand.Execute(null);

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await new SettingsWindow().ShowDialog(owner);
        (DataContext as MainViewModel)?.RefreshPipelineStatus();
    }

    private async Task<bool> ConfirmDeleteAsync(MeetingItemViewModel meeting)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return false;

        return await new DeleteMeetingWindow(meeting.Title).ShowDialog<bool>(owner);
    }

    private async Task<bool?> ConfirmExportBundleAsync(MeetingItemViewModel meeting)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return null;

        return await new ExportBundleWindow(meeting.Title).ShowDialog<bool?>(owner);
    }

    private async Task<BundleImportChoice> ChooseImportChoiceAsync(string meetingTitle)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return BundleImportChoice.Skip;

        return await new ImportBundleWindow(meetingTitle).ShowDialog<BundleImportChoice>(owner);
    }

    private async Task<string?> ChooseFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Instance["workspace.newproject"],
            AllowMultiple = false,
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private async Task<string?> ChooseFileAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Instance["workspace.importrecord"],
            AllowMultiple = false,
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> ChooseTargetAsync(string suggestedName)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Instance["export.dialog.title"],
            SuggestedFileName = suggestedName,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }
}
