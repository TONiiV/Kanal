using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kanal.Core.Diagnostics;
using Kanal.Host.Diagnostics;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
        : this(new SettingsViewModel())
    {
    }

    // Constructor-injected: assigning DataContext afterwards still ran the production view model.
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnBrowseTranscriptsClick(object? sender, RoutedEventArgs e) =>
        Guarded("Choosing a transcript folder", async () =>
        {
            if (DataContext is SettingsViewModel vm && await PickFolderAsync(vm.TranscriptFolder) is { } picked)
                vm.TranscriptFolder = picked;
        });

    private void OnBrowseAudioClick(object? sender, RoutedEventArgs e) =>
        Guarded("Choosing an audio folder", async () =>
        {
            if (DataContext is SettingsViewModel vm && await PickFolderAsync(vm.AudioFolder) is { } picked)
                vm.AudioFolder = picked;
        });

    // Every async void handler goes through here: a throw out of one takes the host down.
    private static async void Guarded(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Warning(SettingsLog, $"{what} failed.", ex);
        }
    }

    private const string SettingsLog = "settings";

    /// <summary>Opens on whatever is already in the box, falling back to the resolved default.</summary>
    private async Task<string?> PickFolderAsync(string current)
    {
        var start = string.IsNullOrWhiteSpace(current) ? SettingsStore.DefaultOutputFolder : current;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Instance["settings.browse.title"],
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

    private bool _changelogOpen;

    private void OnChangelogClick(object? sender, RoutedEventArgs e) =>
        Guarded("Opening the changelog", async () =>
        {
            if (_changelogOpen)
                return;

            _changelogOpen = true;
            try
            {
                await new ChangelogWindow().ShowDialog(this);
            }
            finally
            {
                _changelogOpen = false;
            }
        });

    private bool _openSourceOpen;

    private void OnOpenSourceClick(object? sender, RoutedEventArgs e) =>
        Guarded("Opening the open-source acknowledgements", async () =>
        {
            if (_openSourceOpen)
                return;

            _openSourceOpen = true;
            try
            {
                await new OpenSourceWindow().ShowDialog(this);
            }
            finally
            {
                _openSourceOpen = false;
            }
        });

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if ((DataContext as SettingsViewModel)?.Save() is { } saved)
                LogSetup.Apply(saved);
        }
        catch (Exception ex)
        {
            Log.Error(SettingsLog, "Settings could not be saved.", ex);
        }

        Close();
    }

    /// <summary>Covers every way out of the dialog — Save, Cancel and the window chrome.</summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SettingsViewModel)?.CancelDownloads();
        base.OnClosed(e);
    }
}
