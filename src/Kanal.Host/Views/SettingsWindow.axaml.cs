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

    /// <summary>
    /// The view model is built by the caller so a test can hand in a hermetic one. Setting
    /// <c>DataContext</c> after construction did not: the parameterless view model had already
    /// read the developer's real settings file, enumerated their microphones and registered a
    /// native hot-plug listener that then outlived the window.
    /// </summary>
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

    /// <summary>
    /// Every `async void` handler in this window goes through here. An exception escaping one of
    /// them crosses back into the framework with nowhere to be caught and takes the host down —
    /// mid-meeting, to lose a dialog.
    /// </summary>
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

    /// <summary>Guards against a second dialog while one is open — the button stays clickable.</summary>
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

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as SettingsViewModel;
        AppSettings saved;
        try
        {
            saved = viewModel?.Save()!;
        }
        catch (Exception ex)
        {
            // A locked or read-only profile directory — roaming sync, antivirus, a full disk.
            // Closing here was closing on a write that did not happen: the operator pasted a key,
            // saw the dialog accept it, and found out at the next Start from a refusal about a key
            // they had just entered. So the dialog stays, with the reason on it.
            //
            // Only the write is inside this try. Everything after it runs on a file that is
            // already on disk, and a failure there saying "nothing has been written" would be a
            // lie about the one thing the operator needs to know.
            Log.Error(SettingsLog, "Settings could not be saved.", ex);
            if (viewModel is not null)
            {
                viewModel.SaveError = Localizer.Instance.Format("settings.save.failed", ex.Message);
                return;
            }

            Close();
            return;
        }

        if (viewModel is not null && saved is not null)
        {
            // The level and the size the operator just chose apply to the next line written, not
            // to the next launch: the reason someone turns Debug on is that something is going
            // wrong now. Applied from what Save returned rather than by re-reading the file.
            // ApplyTo catches its own failures — an unwritable folder is reported through
            // LogIsWritable, not by throwing.
            LogSetup.Apply(saved);
            viewModel.RefreshLogState();
            viewModel.SaveError = "";
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
