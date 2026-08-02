using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Host.Services;
using Kanal.Providers.LocalMt;

namespace Kanal.Host.ViewModels;

public partial class ApiKeyItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _key = "";

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// Manages the stored Gladia API keys and the active translation model.
/// Multiple keys, one active; the env var GLADIA_API_KEY stays as the fallback
/// when no stored key exists.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel()
        : this(SettingsStore.Load())
    {
    }

    public SettingsViewModel(AppSettings settings)
    {
        foreach (var entry in settings.ApiKeys.Where(k => k.Provider == "gladia"))
        {
            Keys.Add(new ApiKeyItemViewModel
            {
                Name = entry.Name,
                Key = entry.Key,
                IsActive = entry.Name == settings.ActiveGladiaKeyName,
            });
        }

        if (Keys.Count > 0 && !Keys.Any(k => k.IsActive))
            Keys[0].IsActive = true;

        EnvFallback = SettingsStore.ReadEnvAllScopes(SettingsStore.GladiaEnvVar) is not null
            ? $"Fallback: {SettingsStore.GladiaEnvVar} env var is set."
            : $"Fallback: {SettingsStore.GladiaEnvVar} env var is not set.";

        var downloads = new ModelDownloadManager(SettingsStore.ModelsPath);
        TranslationModels.Add(new TranslationModelItemViewModel());
        foreach (var model in LocalModelCatalog.Models)
            TranslationModels.Add(new TranslationModelItemViewModel(model, downloads));

        var active = TranslationModels.FirstOrDefault(
                         m => m.IsLocal && m.ModelId == settings.ActiveTranslationModelId)
                     ?? TranslationModels[0];
        active.IsActive = true;

        _transcriptFolder = settings.TranscriptFolder ?? "";
        _audioFolder = settings.AudioFolder ?? "";
        _recordAudio = settings.RecordAudio;
    }

    public ObservableCollection<ApiKeyItemViewModel> Keys { get; } = new();

    public ObservableCollection<TranslationModelItemViewModel> TranslationModels { get; } = new();

    public string EnvFallback { get; }

    /// <summary>
    /// Where the export dialog opens, and where a meeting's audio is written. Blank means
    /// "wherever the default is" rather than the current directory — a cleared box must not
    /// silently start writing transcripts next to the executable.
    /// </summary>
    [ObservableProperty]
    private string _transcriptFolder = "";

    /// <inheritdoc cref="TranscriptFolder"/>
    [ObservableProperty]
    private string _audioFolder = "";

    /// <summary>Whether the room is written to disk while a meeting runs.</summary>
    [ObservableProperty]
    private bool _recordAudio = true;

    /// <summary>What the folders resolve to when both boxes are empty, printed under them.</summary>
    public string DefaultFolderNote => $"Empty means {SettingsStore.DefaultOutputFolder}";

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private string _newKey = "";

    [RelayCommand]
    private void Add()
    {
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewKey))
            return;

        var item = new ApiKeyItemViewModel
        {
            Name = NewName.Trim(),
            Key = NewKey.Trim(),
            IsActive = Keys.Count == 0,
        };
        Keys.Add(item);
        NewName = "";
        NewKey = "";
    }

    [RelayCommand]
    private void Remove(ApiKeyItemViewModel item)
    {
        var wasActive = item.IsActive;
        Keys.Remove(item);
        if (wasActive && Keys.Count > 0)
            Keys[0].IsActive = true;
    }

    [RelayCommand]
    private void SetActive(ApiKeyItemViewModel item)
    {
        foreach (var key in Keys)
            key.IsActive = ReferenceEquals(key, item);
    }

    /// <summary>
    /// Stops anything still downloading. The window owns this view model, and MainWindow builds
    /// a new pair every time Settings opens: a download left running behind a closed dialog is
    /// invisible, uncancellable, and collides with the download the next dialog offers.
    /// </summary>
    public void CancelDownloads()
    {
        foreach (var model in TranslationModels)
            model.CancelDownload();
    }

    public void Save()
    {
        var settings = SettingsStore.Load();
        ApplyTo(settings);
        SettingsStore.Save(settings);
    }

    /// <summary>Write the edited state onto <paramref name="settings"/> (separated from disk IO for tests).</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.ApiKeys.RemoveAll(k => k.Provider == "gladia");
        settings.ApiKeys.AddRange(Keys
            .Where(k => !string.IsNullOrWhiteSpace(k.Name) && !string.IsNullOrWhiteSpace(k.Key))
            .Select(k => new ApiKeyEntry(k.Name.Trim(), "gladia", k.Key.Trim())));
        settings.ActiveGladiaKeyName = Keys.FirstOrDefault(k => k.IsActive)?.Name.Trim();
        settings.ActiveTranslationModelId =
            TranslationModels.FirstOrDefault(m => m.IsActive)?.ModelId;
        settings.TranscriptFolder = Folder(TranscriptFolder);
        settings.AudioFolder = Folder(AudioFolder);
        settings.RecordAudio = RecordAudio;
    }

    /// <summary>Whitespace is stored as "unset", so the resolver's fallback is the only default.</summary>
    private static string? Folder(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
