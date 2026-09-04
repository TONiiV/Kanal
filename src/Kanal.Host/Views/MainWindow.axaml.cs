using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.ChooseExportPath = ChooseExportPathAsync;
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosed(e);
    }

    private async Task<string?> ChooseExportPathAsync(string folder, string suggestedName)
    {
        var extension = Path.GetExtension(suggestedName).TrimStart('.');
        var isJson = string.Equals(extension, "json", StringComparison.OrdinalIgnoreCase);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Instance["export.dialog.title"],
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await SafeFolderAsync(folder),
            FileTypeChoices =
            [
                new FilePickerFileType(isJson ? "JSON" : "Markdown")
                {
                    Patterns = [$"*.{extension}"],
                },
            ],
        });
        return file?.TryGetLocalPath();
    }

    private async Task<IStorageFolder?> SafeFolderAsync(string path)
    {
        try { return await StorageProvider.TryGetFolderFromPathAsync(path); }
        catch { return null; }
    }
}
