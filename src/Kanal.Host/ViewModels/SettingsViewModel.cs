using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Audio;
using Kanal.Host.Localization;
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

    /// <param name="captureFactory">
    /// Test seam, like <c>MainViewModel</c>'s: headless runs must not enumerate or open the
    /// developer's real microphone.
    /// </param>
    public SettingsViewModel(AppSettings settings, Func<IAudioCaptureService?>? captureFactory = null)
    {
        CaptureFactory = captureFactory ?? AudioCaptureFactory.TryCreate;
        try
        {
            foreach (var device in CaptureFactory()?.GetDevices() ?? [])
                Devices.Add(device);
            TestDevice = Devices.FirstOrDefault();
        }
        catch
        {
            // no capture backend, or no devices — the panel says so when the test is started
        }

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

        _envVarIsSet = SettingsStore.ReadEnvAllScopes(SettingsStore.GladiaEnvVar) is not null;

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
        // What this settings object says, or — when nothing has been chosen — whatever the
        // application resolved to at launch. Reading only the live localizer would have shown
        // the wrong row for a settings file that had not been applied yet.
        var chosen = settings.AppLanguage ?? Localizer.Instance.Current;
        _appLanguage = Localizer.Available.FirstOrDefault(l => l.Code == chosen)
                       ?? Localizer.Available[0];

        // The switch happens in this window, so this window least of all may stay in the old
        // language. Unsubscribed in CancelDownloads — the same close-time cleanup the downloads
        // use — so the static localizer does not keep dead view models reachable.
        Localizer.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not "Item[]")
            return;

        OnPropertyChanged(nameof(EnvFallback));
        OnPropertyChanged(nameof(ProcessingNote));
        OnPropertyChanged(nameof(DefaultFolderNote));
        foreach (var model in TranslationModels)
            model.RefreshText();

        // The verdict is re-spoken only where it is still a standing state rather than the
        // record of a measurement or a failure: "not tested" before any test, "listening"
        // while one runs but nothing has arrived, and a measured verdict recomputed from the
        // meter it came from. A failure message keeps its language like any other event.
        if (!_verdictTouched)
        {
            VerdictLabel = Localizer.Instance["mic.untested"];
            VerdictDetail = Localizer.Instance["mic.untested.detail"];
        }
        else if (IsTesting && _meter.Frames == 0)
        {
            VerdictLabel = Localizer.Instance["mic.listening"];
            VerdictDetail = Localizer.Instance["mic.listening.detail"];
        }
        else if (_meter.Frames > 0)
        {
            Publish();
        }
    }

    public ObservableCollection<ApiKeyItemViewModel> Keys { get; } = new();

    public ObservableCollection<TranslationModelItemViewModel> TranslationModels { get; } = new();

    private readonly bool _envVarIsSet;

    public string EnvFallback => Localizer.Instance.Format(
        _envVarIsSet ? "settings.env.set" : "settings.env.unset", SettingsStore.GladiaEnvVar);

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

    public IReadOnlyList<AppLanguage> AppLanguages => Localizer.Available;

    /// <summary>
    /// The language of this application's own labels. Applied the moment it is chosen rather
    /// than on Save: an operator who cannot read the current language cannot be expected to
    /// find the Save button to prove their choice worked.
    /// </summary>
    [ObservableProperty]
    private AppLanguage? _appLanguage;

    partial void OnAppLanguageChanged(AppLanguage? value)
    {
        if (value is not null)
            Localizer.Instance.Current = value.Code;
    }

    // ---- microphone test -------------------------------------------------------------------

    /// <summary>Test seam: headless tests feed generated audio instead of opening a device.</summary>
    public Func<IAudioCaptureService?> CaptureFactory { get; }

    private CancellationTokenSource? _testCts;
    private LevelMeter _meter = new();

    /// <summary>False until the first test touches the verdict — the only state in which a
    /// language change may rewrite it wholesale rather than recompute or preserve it.</summary>
    private bool _verdictTouched;

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();

    [ObservableProperty]
    private AudioDeviceInfo? _testDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartTestCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopTestCommand))]
    private bool _isTesting;

    /// <summary>0–100 for the bar: the current frame's level.</summary>
    [ObservableProperty]
    private double _levelScale;

    /// <summary>Where the loudest thing heard so far sits on the same bar.</summary>
    [ObservableProperty]
    private double _peakScale;

    [ObservableProperty]
    private string _levelReadout = "";

    [ObservableProperty]
    private string _verdictLabel = Localizer.Instance["mic.untested"];

    [ObservableProperty]
    private string _verdictDetail = Localizer.Instance["mic.untested.detail"];

    /// <summary>
    /// The honest answer to "what noise suppression does this have": none of its own. Whatever
    /// the device and Windows do to the signal happens before Kanal sees a sample, so the useful
    /// thing this panel can offer is a measurement of the result rather than a control that
    /// pretends to change it.
    /// </summary>
    public string ProcessingNote => Localizer.Instance["settings.input.note"];

    private bool CanStartTest() => !IsTesting;

    private bool CanStopTest() => IsTesting;

    [RelayCommand(CanExecute = nameof(CanStartTest))]
    private void StartTest()
    {
        _verdictTouched = true;
        var capture = CaptureFactory();
        if (capture is null)
        {
            VerdictLabel = Localizer.Instance["mic.nobackend"];
            VerdictDetail = Localizer.Instance["mic.nobackend.detail"];
            return;
        }

        _meter = new LevelMeter();
        _testCts = new CancellationTokenSource();
        IsTesting = true;
        VerdictLabel = Localizer.Instance["mic.listening"];
        VerdictDetail = Localizer.Instance["mic.listening.detail"];
        _ = RunTestAsync(capture, TestDevice?.Id, _testCts.Token);
    }

    [RelayCommand(CanExecute = nameof(CanStopTest))]
    private void StopTest()
    {
        _testCts?.Cancel();
        _testCts = null;
        IsTesting = false;
        LevelScale = 0;
    }

    private async Task RunTestAsync(IAudioCaptureService capture, string? deviceId, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in capture.CaptureAsync(deviceId, ct))
            {
                _meter.Add(frame.Span);
                Dispatcher.UIThread.Post(Publish);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                VerdictLabel = Localizer.Instance["mic.failed"];
                VerdictDetail = ex.Message;
                IsTesting = false;
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() => LevelScale = 0);
        }
    }

    /// <summary>Pushes the meter's current reading onto the bound properties. Internal for tests.</summary>
    internal void Publish()
    {
        LevelScale = LevelMeter.ToScale(_meter.CurrentDb);
        PeakScale = LevelMeter.ToScale(_meter.PeakDb);
        var l = Localizer.Instance;
        LevelReadout = _meter.Frames == 0
            ? ""
            : _meter.HasMeasurableNoise
                ? l.Format("mic.readout", $"{_meter.PeakDb:0}", $"{_meter.NoiseFloorDb:0}", $"{_meter.MarginDb:0}")
                : l.Format("mic.readout.silentroom", $"{_meter.PeakDb:0}");

        (VerdictLabel, VerdictDetail) = _meter.Verdict switch
        {
            InputVerdict.Silent => (l["mic.silent"], l["mic.silent.detail"]),
            InputVerdict.TooQuiet => (l["mic.quiet"], l["mic.quiet.detail"]),
            InputVerdict.Clipping => (l["mic.clipping"], l["mic.clipping.detail"]),
            InputVerdict.Noisy => (
                l["mic.noisy"], l.Format("mic.noisy.detail", $"{_meter.MarginDb:0}")),
            _ => (
                l["mic.good"],
                _meter.HasMeasurableNoise
                    ? l.Format("mic.good.detail", $"{_meter.MarginDb:0}")
                    : l.Format("mic.good.silentroom", $"{_meter.PeakDb:0}")),
        };
    }

    /// <summary>What the folders resolve to when both boxes are empty, printed under them.</summary>
    public string DefaultFolderNote =>
        Localizer.Instance.Format("settings.files.default", SettingsStore.DefaultOutputFolder);

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
        // Part of the same window-closed cleanup: without this the static localizer keeps
        // every closed Settings dialog's view model alive and keeps refreshing it.
        Localizer.Instance.PropertyChanged -= OnLanguageChanged;

        foreach (var model in TranslationModels)
            model.CancelDownload();

        // Same reasoning: a microphone left open behind a closed dialog is invisible and
        // uncancellable, and it holds the device the next meeting wants.
        StopTest();
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
        settings.AppLanguage = AppLanguage?.Code;
    }

    /// <summary>Whitespace is stored as "unset", so the resolver's fallback is the only default.</summary>
    private static string? Folder(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
