using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Audio;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Relay;
using Kanal.Core.Room;
using Kanal.Host.Services;
using Kanal.Providers.Gladia;
using QRCoder;

namespace Kanal.Host.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly Dictionary<string, Speaker> _speakerModels = new();
    private readonly Dictionary<string, string> _tagToCanonical = new();
    private readonly DispatcherTimer _snapshotTimer;
    private MeetingSession? _session;
    private CancellationTokenSource? _captureCts;
    private GladiaAsrProvider? _gladiaProvider;

    public MainViewModel()
    {
        foreach (var (code, name) in LanguageCatalog.Known)
            AttachLanguageOption(new LanguageOption
            {
                Code = code,
                Label = name,
                IsSelected = code is "zh" or "de" or "pl",
            });
        RefreshSelectedLanguages();

        Columns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasColumns));

        _snapshotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _snapshotTimer.Tick += async (_, _) => await PublishSnapshotSafeAsync();

        try
        {
            foreach (var device in AudioCaptureFactory.Create().GetDevices())
                Devices.Add(device);
            SelectedDevice = Devices.FirstOrDefault();
        }
        catch
        {
            // no capture backend or no devices — demo mode still works
        }

        RefreshKeyStatus();
    }

    public ObservableCollection<ColumnViewModel> Columns { get; } = new();

    public ObservableCollection<SpeakerItemViewModel> Speakers { get; } = new();

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();

    /// <summary>The full pickable catalog, shown in the edit dialog; custom ISO codes are appended.</summary>
    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new();

    /// <summary>The selected subset, in catalog order — drives the flag stack and the room config.</summary>
    public ObservableCollection<LanguageOption> SelectedLanguages { get; } = new();

    /// <summary>Codes next to the flags: colour never carries meaning alone.</summary>
    public string SelectedLanguageSummary => SelectedLanguages.Count == 0
        ? "none — click to add"
        : string.Join(" · ", SelectedLanguages.Select(o => o.Code.ToUpperInvariant()));

    private void AttachLanguageOption(LanguageOption option)
    {
        option.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LanguageOption.IsSelected))
                RefreshSelectedLanguages();
        };
        LanguageOptions.Add(option);
    }

    private void RefreshSelectedLanguages()
    {
        SelectedLanguages.Clear();
        foreach (var option in LanguageOptions.Where(o => o.IsSelected))
            SelectedLanguages.Add(option);
        OnPropertyChanged(nameof(SelectedLanguageSummary));
    }

    /// <summary>Adds (or selects) languages typed as ISO codes in the edit dialog, e.g. "tr, nl".</summary>
    [RelayCommand]
    private void AddLanguage()
    {
        foreach (var raw in NewLanguageInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = raw.ToLowerInvariant();
            if (code.Length is < 2 or > 3 || !code.All(char.IsAsciiLetterLower))
                continue;

            var existing = LanguageOptions.FirstOrDefault(o =>
                string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                existing.IsSelected = true;
            else
                AttachLanguageOption(new LanguageOption
                {
                    Code = code,
                    Label = LanguageCatalog.NativeName(code) ?? code.ToUpperInvariant(),
                    IsSelected = true,
                });
        }

        NewLanguageInput = "";
    }

    public string[] Modes { get; } = ["Demo (scripted)", "Gladia (live)"];

    /// <summary>Relay can be disabled (tests, fully offline use); QR is only shown when enabled.</summary>
    public bool RelayEnabled { get; set; } = true;

    [ObservableProperty]
    private string _selectedMode = "Demo (scripted)";

    /// <summary>ISO codes typed into the edit dialog's add row, e.g. "tr, nl".</summary>
    [ObservableProperty]
    private string _newLanguageInput = "";

    [ObservableProperty]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _status = "Idle.";

    [ObservableProperty]
    private string _keyStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(ShowMicLevel))]
    private bool _isRunning;

    /// <summary>Input peak 0–100, updated ~4×/s while live capture runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMicLevel))]
    private double _micLevel;

    public bool ShowMicLevel => IsRunning && IsGladiaMode;

    [ObservableProperty]
    private string _mergeFromTag = "";

    [ObservableProperty]
    private string _mergeIntoTag = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJoinInfo))]
    private string _joinUrl = "";

    [ObservableProperty]
    private Bitmap? _qrImage;

    public bool HasJoinInfo => JoinUrl.Length > 0;

    /// <summary>False before the first Start — the column area shows what to do instead of a void.</summary>
    public bool HasColumns => Columns.Count > 0;

    public bool IsGladiaMode => SelectedMode.StartsWith("Gladia", StringComparison.Ordinal);

    partial void OnSelectedModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsGladiaMode));
        OnPropertyChanged(nameof(ShowMicLevel));
    }

    public void RefreshKeyStatus()
    {
        var resolved = SettingsStore.ResolveGladiaKey(SettingsStore.Load());
        KeyStatus = resolved is null
            ? "Gladia key: none — open Settings"
            : $"Gladia key: {resolved.Value.Source}";
    }

    private bool CanStart() => !IsRunning;

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var languages = SelectedLanguages.Select(o => o.Code.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (languages.Count == 0)
        {
            Status = "Select at least one language.";
            return;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        Columns.Clear();
        Speakers.Clear();
        _speakerModels.Clear();
        _tagToCanonical.Clear();
        // host renders at most 4 columns; remaining languages still translate and relay
        foreach (var lang in languages.Take(4))
            Columns.Add(new ColumnViewModel(lang));

        IAsrProvider asr;
        IMtProvider? mt;
        if (IsGladiaMode)
        {
            var resolved = SettingsStore.ResolveGladiaKey(SettingsStore.Load());
            if (resolved is null)
            {
                Status = "No Gladia API key. Add one in Settings or set GLADIA_API_KEY.";
                return;
            }

            _gladiaProvider = new GladiaAsrProvider(new GladiaOptions { ApiKey = resolved.Value.Key });
            asr = _gladiaProvider;
            mt = null; // Gladia caps declare end-to-end translation
        }
        else
        {
            asr = new FakeAsrProvider(loop: true);
            mt = new FakeMtProvider();
        }

        var config = new RoomConfig($"kanal-{DateTime.Now:HHmmss}", languages);
        var relaySettings = RelaySettings.FromEnvironment();
        IRelayPublisher relay = RelayEnabled
            ? new SupabaseRelayPublisher(relaySettings.SupabaseUrl, relaySettings.AnonKey, config.RoomId)
            : new NullRelayPublisher();

        var session = new MeetingSession(asr, mt, relay, config);
        session.Room.UtteranceUpserted += u => Dispatcher.UIThread.Post(() => ApplyUtterance(u));
        session.Room.SpeakerUpserted += s => Dispatcher.UIThread.Post(() => ApplySpeaker(s));
        session.ErrorOccurred += e => Dispatcher.UIThread.Post(() =>
            Status = (e.Fatal ? "Fatal: " : "Warning: ") + e.Message);
        session.SessionEnded += reason => Dispatcher.UIThread.Post(() =>
            Status = $"Session ended: {reason ?? "done"}");

        try
        {
            await session.StartAsync();
        }
        catch (Exception ex)
        {
            Status = $"Start failed: {ex.Message}";
            await session.DisposeAsync();
            _gladiaProvider?.Dispose();
            _gladiaProvider = null;
            return;
        }

        _session = session;
        IsRunning = true;
        Status = IsGladiaMode ? "Live — streaming microphone to Gladia." : "Demo running.";

        if (RelayEnabled)
        {
            ShowJoinInfo(relaySettings.BuildJoinUrl(config.RoomId));
            _snapshotTimer.Start();
        }

        if (IsGladiaMode)
        {
            _captureCts = new CancellationTokenSource();
            _ = PumpMicrophoneAsync(session, SelectedDevice?.Id, _captureCts.Token);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        _snapshotTimer.Stop();
        _captureCts?.Cancel();
        _captureCts = null;

        if (_session is not null)
        {
            await PublishSnapshotSafeAsync(); // leave a final full state on the channel
            await _session.DisposeAsync();    // session object stays for rename/merge/export
        }

        _gladiaProvider?.Dispose();
        _gladiaProvider = null;
        JoinUrl = "";
        QrImage = null;
        IsRunning = false;
        Status = "Stopped. Rename, merge and export still work on the last room.";
    }

    private void ShowJoinInfo(string url)
    {
        JoinUrl = url;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 5);
        QrImage = new Bitmap(new MemoryStream(png));
    }

    private async Task PublishSnapshotSafeAsync()
    {
        try
        {
            if (_session is not null)
                await _session.PublishSnapshotAsync();
        }
        catch (Exception ex)
        {
            Status = $"Warning: snapshot publish failed: {ex.Message}";
        }
    }

    private async Task PumpMicrophoneAsync(MeetingSession session, string? deviceId, CancellationToken ct)
    {
        var capture = AudioCaptureFactory.TryCreate();
        if (capture is null)
        {
            Dispatcher.UIThread.Post(() => Status = "No audio capture backend on this platform.");
            return;
        }

        try
        {
            var framesSinceMeter = 0;
            await foreach (var frame in capture.CaptureAsync(deviceId, ct))
            {
                await session.PushAudioAsync(frame, ct);

                // input level meter ~4×/s — "is the mic alive" must be visible at a glance
                if (++framesSinceMeter >= 3)
                {
                    framesSinceMeter = 0;
                    var peak = FramePeak(frame.Span);
                    Dispatcher.UIThread.Post(() => MicLevel = peak);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Status = $"Audio capture failed: {ex.Message}");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => MicLevel = 0);
        }
    }

    private static double FramePeak(ReadOnlySpan<byte> pcm16)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm16);
        var peak = 0;
        foreach (var s in samples)
            peak = Math.Max(peak, Math.Abs((int)s));
        return peak / (double)short.MaxValue * 100.0;
    }

    private void ApplyRename(SpeakerItemViewModel item)
    {
        if (_session is null)
            return;
        var name = string.IsNullOrWhiteSpace(item.Name) ? null : item.Name.Trim();
        _session.RenameSpeaker(item.Tag, name);
    }

    [RelayCommand]
    private void MergeSpeakers()
    {
        if (_session is null ||
            string.IsNullOrWhiteSpace(MergeFromTag) || string.IsNullOrWhiteSpace(MergeIntoTag))
            return;
        _session.MergeSpeakers(MergeFromTag.Trim(), MergeIntoTag.Trim());
        MergeFromTag = "";
        MergeIntoTag = "";
    }

    [RelayCommand]
    private void ExportMarkdown()
    {
        if (_session is null)
        {
            Status = "Nothing to export.";
            return;
        }

        var snapshot = _session.Room.Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine($"# Kanal — {snapshot.Config.RoomId}");
        sb.AppendLine();
        foreach (var u in snapshot.Utterances.Where(u => u.State == UtteranceState.Final))
        {
            var (name, _) = ResolveSpeaker(u.SpeakerTag);
            sb.AppendLine($"**{name}** ({u.SrcLang}): {u.SrcText}");
            foreach (var (lang, text) in u.Translations.OrderBy(t => t.Key))
                sb.AppendLine($"  - {lang}: {text}");
            sb.AppendLine();
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"{snapshot.Config.RoomId}.md");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Status = $"Exported to {path}";
    }

    internal SpeakerItemViewModel CreateSpeakerItem(string tag) => new(ApplyRename) { Tag = tag };

    private void ApplyUtterance(Utterance u)
    {
        var (speakerName, speakerColor) = ResolveSpeaker(u.SpeakerTag);
        foreach (var column in Columns)
        {
            var isSourceColumn = string.Equals(column.Language, u.SrcLang, StringComparison.OrdinalIgnoreCase);
            var translation = u.Translations.TryGetValue(column.Language, out var t) ? t : null;

            var bubble = column.GetOrAdd(u.Id);
            bubble.SpeakerTag = u.SpeakerTag;
            bubble.SpeakerName = speakerName;
            bubble.SpeakerColor = speakerColor;
            bubble.SourceLang = u.SrcLang.ToUpperInvariant();
            bubble.IsPartial = u.State == UtteranceState.Partial;
            bubble.CodeSwitch = u.CodeSwitch;
            // each column reads in its own language: the source column carries the transcript
            // (labelled ORIGINAL), every other column waits for its translation — never the
            // untranslated source text
            bubble.IsTranscript = isSourceColumn;
            bubble.AwaitingTranslation = !isSourceColumn && translation is null;
            bubble.Text = isSourceColumn ? u.SrcText : translation ?? "…";
            bubble.SourceText = isSourceColumn || translation is null ? "" : u.SrcText;
        }
    }

    private void ApplySpeaker(Speaker speaker)
    {
        _speakerModels[speaker.Tag] = speaker;
        _tagToCanonical[speaker.Tag] = speaker.Tag;
        foreach (var merged in speaker.MergedFrom)
        {
            _tagToCanonical[merged] = speaker.Tag;
            _speakerModels.Remove(merged);
            var stale = Speakers.FirstOrDefault(s => s.Tag == merged);
            if (stale is not null)
                Speakers.Remove(stale);
        }

        var item = Speakers.FirstOrDefault(s => s.Tag == speaker.Tag);
        if (item is null)
        {
            item = CreateSpeakerItem(speaker.Tag);
            Speakers.Add(item);
        }

        item.Color = speaker.Color;
        item.Name = speaker.DisplayName ?? "";
        item.MergedFromLabel = speaker.MergedFrom.Count > 0
            ? $"⊇ {string.Join(", ", speaker.MergedFrom)}"
            : "";

        // re-resolve every history bubble — renames and merges rewrite the past
        foreach (var bubble in Columns.SelectMany(c => c.Bubbles))
        {
            var (name, color) = ResolveSpeaker(bubble.SpeakerTag);
            bubble.SpeakerName = name;
            bubble.SpeakerColor = color;
        }
    }

    private (string Name, string Color) ResolveSpeaker(string tag)
    {
        var canonical = _tagToCanonical.TryGetValue(tag, out var c) ? c : tag;
        if (_speakerModels.TryGetValue(canonical, out var speaker))
            return (speaker.DisplayName ?? speaker.Tag, speaker.Color);
        return (tag, "#4C5C68");
    }
}
