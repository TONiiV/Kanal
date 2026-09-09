using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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
        };
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await new SettingsWindow().ShowDialog(owner);
        (DataContext as MainViewModel)?.RefreshPipelineStatus();
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
