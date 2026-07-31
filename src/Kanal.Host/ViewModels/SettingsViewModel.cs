using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Audio;
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

    // ---- microphone test -------------------------------------------------------------------

    /// <summary>Test seam: headless tests feed generated audio instead of opening a device.</summary>
    public Func<IAudioCaptureService?> CaptureFactory { get; }

    private CancellationTokenSource? _testCts;
    private LevelMeter _meter = new();

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
    private string _verdictLabel = "Not tested";

    [ObservableProperty]
    private string _verdictDetail =
        "Start the test and speak from where people will actually sit.";

    /// <summary>
    /// The honest answer to "what noise suppression does this have": none of its own. Whatever
    /// the device and Windows do to the signal happens before Kanal sees a sample, so the useful
    /// thing this panel can offer is a measurement of the result rather than a control that
    /// pretends to change it.
    /// </summary>
    public string ProcessingNote =>
        "Kanal applies no noise suppression, echo cancellation or automatic gain of its own. "
        + "Whatever the microphone and Windows do to the signal happens before Kanal sees it, and "
        + "is configured per device in Windows sound settings — this test measures the result.";

    private bool CanStartTest() => !IsTesting;

    private bool CanStopTest() => IsTesting;

    [RelayCommand(CanExecute = nameof(CanStartTest))]
    private void StartTest()
    {
        var capture = CaptureFactory();
        if (capture is null)
        {
            VerdictLabel = "No audio backend";
            VerdictDetail = "This platform has no capture support built in yet.";
            return;
        }

        _meter = new LevelMeter();
        _testCts = new CancellationTokenSource();
        IsTesting = true;
        VerdictLabel = "Listening…";
        VerdictDetail = "Speak from where people will actually sit, then leave a few seconds of quiet.";
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
                VerdictLabel = "Could not open the microphone";
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
        LevelReadout = _meter.Frames == 0
            ? ""
            : _meter.HasMeasurableNoise
                ? $"peak {_meter.PeakDb:0} dB · room {_meter.NoiseFloorDb:0} dB · margin {_meter.MarginDb:0} dB"
                : $"peak {_meter.PeakDb:0} dB · room silent";

        (VerdictLabel, VerdictDetail) = _meter.Verdict switch
        {
            InputVerdict.Silent => (
                "Nothing is arriving",
                "Check that this is the right device and that Windows has not muted or disabled it."),
            InputVerdict.TooQuiet => (
                "Too quiet",
                "Raise the input level in Windows sound settings, or put the microphone nearer the table."),
            InputVerdict.Clipping => (
                "Clipping",
                "Lower the input level in Windows sound settings. A clipped consonant is gone for good — "
                + "no transcriber recovers it."),
            InputVerdict.Noisy => (
                "The room is nearly as loud as the speaker",
                $"Speech is only {_meter.MarginDb:0} dB above the room. Move the microphone closer to the "
                + "talkers, or turn off whatever is making the noise."),
            _ => (
                "Good",
                _meter.HasMeasurableNoise
                    ? $"Speech sits {_meter.MarginDb:0} dB above the room."
                    : $"Speech peaks at {_meter.PeakDb:0} dB and the gaps are completely silent — "
                      + "either a very quiet room, or the device is gating the signal."),
        };
    }

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
    }

    /// <summary>Whitespace is stored as "unset", so the resolver's fallback is the only default.</summary>
    private static string? Folder(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
