using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }

    private async void OnBrowseTranscriptsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && await PickFolderAsync(vm.TranscriptFolder) is { } picked)
            vm.TranscriptFolder = picked;
    }

    private async void OnBrowseAudioClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && await PickFolderAsync(vm.AudioFolder) is { } picked)
            vm.AudioFolder = picked;
    }

    /// <summary>Opens on whatever is already in the box, falling back to the resolved default.</summary>
    private async Task<string?> PickFolderAsync(string current)
    {
        var start = string.IsNullOrWhiteSpace(current) ? SettingsStore.DefaultOutputFolder : current;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder",
            AllowMultiple = false,
            SuggestedStartLocation = await SafeFolderAsync(start),
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>A folder that has been deleted or renamed must not take the dialog down with it.</summary>
    private async Task<IStorageFolder?> SafeFolderAsync(string path)
    {
        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch
        {
            return null;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.Save();
        Close();
    }

    /// <summary>Covers every way out of the dialog — Save, Cancel and the window chrome.</summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SettingsViewModel)?.CancelDownloads();
        base.OnClosed(e);
    }
}
