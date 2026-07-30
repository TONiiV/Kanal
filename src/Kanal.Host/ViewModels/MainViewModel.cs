using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Audio;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Relay;
using Kanal.Core.Room;
using Kanal.Providers.Gladia;

namespace Kanal.Host.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly Dictionary<string, Speaker> _speakerModels = new();
    private readonly Dictionary<string, string> _tagToCanonical = new();
    private MeetingSession? _session;
    private CancellationTokenSource? _captureCts;
    private GladiaAsrProvider? _gladiaProvider;

    public MainViewModel()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var device in new WasapiAudioCapture().GetDevices())
                    Devices.Add(device);
                SelectedDevice = Devices.FirstOrDefault();
            }
            catch
            {
                // no capture devices — demo mode still works
            }
        }
    }

    public ObservableCollection<ColumnViewModel> Columns { get; } = new();

    public ObservableCollection<SpeakerItemViewModel> Speakers { get; } = new();

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();

    public string[] Modes { get; } = ["Demo (scripted)", "Gladia (live)"];

    [ObservableProperty]
    private string _selectedMode = "Demo (scripted)";

    [ObservableProperty]
    private string _languagesInput = "zh, de, pl, en";

    [ObservableProperty]
    private string _gladiaApiKey = "";

    [ObservableProperty]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _status = "Idle.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _mergeFromTag = "";

    [ObservableProperty]
    private string _mergeIntoTag = "";

    public bool IsGladiaMode => SelectedMode.StartsWith("Gladia", StringComparison.Ordinal);

    partial void OnSelectedModeChanged(string value) => OnPropertyChanged(nameof(IsGladiaMode));

    private bool CanStart() => !IsRunning;

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var languages = LanguagesInput
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (languages.Count == 0)
        {
            Status = "Enter at least one language code (e.g. zh, de, pl).";
            return;
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
            if (string.IsNullOrWhiteSpace(GladiaApiKey))
            {
                Status = "Gladia mode needs an API key.";
                return;
            }

            _gladiaProvider = new GladiaAsrProvider(new GladiaOptions { ApiKey = GladiaApiKey.Trim() });
            asr = _gladiaProvider;
            mt = null; // Gladia caps declare end-to-end translation
        }
        else
        {
            asr = new FakeAsrProvider(loop: true);
            mt = new FakeMtProvider();
        }

        var config = new RoomConfig($"kanal-{DateTime.Now:HHmmss}", languages);
        var session = new MeetingSession(asr, mt, new NullRelayPublisher(), config);
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

        if (IsGladiaMode)
        {
            _captureCts = new CancellationTokenSource();
            _ = PumpMicrophoneAsync(session, SelectedDevice?.Id, _captureCts.Token);
        }
    }

    private async Task PumpMicrophoneAsync(MeetingSession session, string? deviceId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            Dispatcher.UIThread.Post(() => Status = "Live capture is Windows-only for now (macOS backend is the open D0-A item).");
            return;
        }

        try
        {
            var capture = new WasapiAudioCapture();
            await foreach (var frame in capture.CaptureAsync(deviceId, ct))
                await session.PushAudioAsync(frame, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Status = $"Audio capture failed: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        _captureCts?.Cancel();
        _captureCts = null;

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        _gladiaProvider?.Dispose();
        _gladiaProvider = null;
        IsRunning = false;
        Status = "Stopped.";
    }

    [RelayCommand]
    private void RenameSpeaker(SpeakerItemViewModel item)
    {
        var name = string.IsNullOrWhiteSpace(item.Name) ? null : item.Name.Trim();
        _session?.RenameSpeaker(item.Tag, name);
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
            bubble.IsPartial = u.State == UtteranceState.Partial;
            bubble.CodeSwitch = u.CodeSwitch;
            // translation on top, source below as the trust anchor; before the
            // translation arrives the column shows the source text alone
            bubble.Text = isSourceColumn ? u.SrcText : translation ?? u.SrcText;
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
            item = new SpeakerItemViewModel { Tag = speaker.Tag };
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
