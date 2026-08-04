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

    private async void OnChangelogClick(object? sender, RoutedEventArgs e)
    {
        if (_changelogOpen)
            return;

        _changelogOpen = true;
        try
        {
            await new ChangelogWindow().ShowDialog(this);
        }
        catch (Exception ex)
        {
            // async void: an exception escaping here takes the host down mid-meeting rather than
            // losing a dialog nobody needs. Same hardening as the folder pickers above.
            Log.Warning("settings", "The changelog window did not open.", ex);
        }
        finally
        {
            _changelogOpen = false;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // The level and the size the operator just chose apply to the next line written, not to
        // the next launch: the reason someone turns Debug on is that something is going wrong now.
        // Applied from what Save returned rather than by re-reading the file — see Save.
        if ((DataContext as SettingsViewModel)?.Save() is { } saved)
            LogSetup.Apply(saved);
        Close();
    }

    /// <summary>Covers every way out of the dialog — Save, Cancel and the window chrome.</summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SettingsViewModel)?.CancelDownloads();
        base.OnClosed(e);
    }
}
