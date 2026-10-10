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
        viewModel.Finished += Close;
    }

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
}
