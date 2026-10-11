using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class ProjectSettingsWindow : Window
{
    public ProjectSettingsWindow(ProjectSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ChooseImageFile = ChooseImageAsync;
        viewModel.ConfirmRemoval = ConfirmRemovalAsync;
        viewModel.ChooseProjectFolder = ChooseProjectFolderAsync;
        viewModel.ConfirmListRemoval = ConfirmListRemovalAsync;
        viewModel.Finished += Close;
        if (viewModel.OpenAtIssues)
            Loaded += (_, _) => IssuesSection.BringIntoView();
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
                MaxHeight = FittedMaxHeight(MaxHeight, screen.WorkingArea.Height, screen.Scaling);
        };
    }

    private const double TitleBarAllowance = 48;

    public static double FittedMaxHeight(double designed, int workAreaPixels, double scaling) =>
        Math.Min(designed, workAreaPixels / scaling - TitleBarAllowance);

    private void OnGlyphClick(object? sender, RoutedEventArgs e) => ChooseGlyph.Flyout?.Hide();

    private async Task<string?> ChooseImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Instance["workspace.icon.file"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Localizer.Instance["workspace.icon.filetype"])
                {
                    Patterns = [.. WorkspaceStore.IconExtensions.Select(extension => "*" + extension)],
                },
            ],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private Task<ProjectRemoval?> ConfirmRemovalAsync(string projectName) =>
        new RemoveProjectWindow(projectName).ShowDialog<ProjectRemoval?>(this);

    private async Task<string?> ChooseProjectFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Instance["workspace.issues.choosefolder"],
            AllowMultiple = false,
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private async Task<bool> ConfirmListRemovalAsync(string projectName) =>
        await new RemoveProjectWindow(projectName, listOnly: true).ShowDialog<ProjectRemoval?>(this)
            == ProjectRemoval.ListOnly;
}
