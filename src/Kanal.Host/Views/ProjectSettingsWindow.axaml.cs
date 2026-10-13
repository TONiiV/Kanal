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

    // Hide() unbinds a Command that reads its window through the popup before Click finishes, so run it here.
    private void OnGlyphClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string glyph } && DataContext is ProjectSettingsViewModel settings)
            settings.ChooseGlyphCommand.Execute(glyph);

        ChooseGlyph.Flyout?.Hide();
    }

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
