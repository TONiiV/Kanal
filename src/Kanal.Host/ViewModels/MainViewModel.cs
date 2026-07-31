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
using Kanal.Core.Relay;
using Kanal.Core.Room;
using Kanal.Host.Services;
using Kanal.Providers.LocalMt;
using QRCoder;

namespace Kanal.Host.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly Dictionary<string, Speaker> _speakerModels = new();
    private readonly Dictionary<string, string> _tagToCanonical = new();
    private readonly DispatcherTimer _snapshotTimer;
    private MeetingSession? _session;
    /// <summary>Outlives its session: the next Start uses it to redirect phones to the new room.</summary>
    private IRelayPublisher? _relay;
    private CancellationTokenSource? _captureCts;
    private IAsrProvider? _asr;
    private IMtProvider? _mt;
    private readonly Func<AppSettings> _loadSettings;
    private readonly Func<ModelDownloadManager> _downloads;

    /// <summary>
    /// Presentation order of the language codes, seeded from <see cref="LanguageCatalog"/> and
    /// rewritten when the operator drags a column. The flag stack and the columns both read it,
    /// so they cannot disagree. Host-local: <see cref="RoomConfig"/> carries the language *set*,
    /// phones render one column chosen from a dropdown, and nothing about order goes on the wire.
    /// </summary>
    private readonly List<string> _columnOrder = new();

    /// <summary>Column the operator has picked up, or -1. Set by the header's drag handler.</summary>
    private int _dragSource = -1;

    public MainViewModel()
        : this(SettingsStore.Load, () => new ModelDownloadManager(SettingsStore.ModelsPath))
    {
    }

    /// <summary>
    /// Test seam, in the shape of <see cref="RelayPublisherFactory"/>: both halves of "what does
    /// this machine translate with" are injected. Headless runs must not read the developer's
    /// real %APPDATA%\Kanal\settings.json — on a machine with a model downloaded, that made a
    /// UI test load a multi-gigabyte LLM and behave differently per developer.
    /// </summary>
    public MainViewModel(Func<AppSettings> loadSettings, Func<ModelDownloadManager> downloads)
    {
        _loadSettings = loadSettings;
        _downloads = downloads;

        foreach (var mode in PipelineMode.All)
            Modes.Add(new PipelineModeOption(mode, unavailable: null));
        _selectedMode = Modes[0];

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

        RefreshPipelineStatus();
    }

    /// <summary>
    /// The PRD freezes the host at four language columns. This is the only place that number
    /// lives: the selection refuses the fifth language here, and <see cref="StartAsync"/> builds
    /// its columns from the same constant, so the picker and the layout cannot drift apart.
    /// </summary>
    public const int MaxLanguages = 4;

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

    /// <summary>True once four languages are selected: every other row is refused until one goes.</summary>
    public bool IsAtLanguageLimit => SelectedLanguages.Count >= MaxLanguages;

    /// <summary>
    /// Why the remaining rows are disabled. Printed in the catalog dialog beside the rows and the
    /// add-by-code row, both of which obey the same cap — a click that does nothing and says
    /// nothing is the failure this replaces.
    /// </summary>
    public string LanguageLimitNotice => IsAtLanguageLimit
        ? "Four columns maximum — deselect one to add another."
        : "";

    private void AttachLanguageOption(LanguageOption option)
    {
        // An option that arrives already selected over the cap — a restored list, a future
        // settings file — is taken in unselected rather than becoming a fifth column.
        if (option.IsSelected && IsAtLanguageLimit)
            option.IsSelected = false;

        if (!_columnOrder.Any(c => string.Equals(c, option.Code, StringComparison.OrdinalIgnoreCase)))
            _columnOrder.Add(option.Code); // a language the operator adds joins at the end

        option.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LanguageOption.IsSelected))
                return;

            // The catalog disables these rows, but nothing stops a fifth arriving in code, so
            // the refusal lives on the property itself: one rule, every path.
            if (option.IsSelected && IsAtLanguageLimit && !SelectedLanguages.Contains(option))
            {
                option.IsSelected = false; // re-enters here and falls through to the refresh
                return;
            }

            RefreshSelectedLanguages();
        };
        LanguageOptions.Add(option);

        // The handler above cannot fire for an option that arrived already selected — a language
        // typed as an ISO code did reach the catalog but never the flag stack or the room config.
        RefreshSelectedLanguages();
    }

    private void RefreshSelectedLanguages()
    {
        SelectedLanguages.Clear();
        foreach (var option in LanguageOptions.Where(o => o.IsSelected).OrderBy(ColumnOrderIndex))
            SelectedLanguages.Add(option);

        var full = SelectedLanguages.Count >= MaxLanguages;
        foreach (var option in LanguageOptions)
            option.IsSelectable = option.IsSelected || !full;

        OnPropertyChanged(nameof(SelectedLanguageSummary));
        OnPropertyChanged(nameof(IsAtLanguageLimit));
        OnPropertyChanged(nameof(LanguageLimitNotice));
    }

    /// <summary>Adds (or selects) languages typed as ISO codes in the edit dialog, e.g. "tr, nl".</summary>
    [RelayCommand]
    private void AddLanguage()
    {
        var refused = new List<string>();

        foreach (var raw in NewLanguageInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = raw.ToLowerInvariant();
            if (code.Length is < 2 or > 3 || !code.All(char.IsAsciiLetterLower))
                continue;

            var existing = LanguageOptions.FirstOrDefault(o =>
                string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && existing.IsSelected)
                continue;

            if (IsAtLanguageLimit)
            {
                // the same cap the checkboxes obey; LanguageLimitNotice sits above this row
                refused.Add(code);
                continue;
            }

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

        // what was refused stays in the box: retyping it after deselecting is not the operator's job
        NewLanguageInput = string.Join(", ", refused);
    }

    private int ColumnOrderIndex(LanguageOption option)
    {
        var index = _columnOrder.FindIndex(c =>
            string.Equals(c, option.Code, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>
    /// Moves a column to a new position, mid-meeting — the operator puts the language they are
    /// reading where they are looking. The <see cref="ColumnViewModel"/> moves with it, so every
    /// utterance already rendered comes along untouched; the selected-language order is rewritten
    /// to match, so the flag stack never disagrees with the screen. Nothing is republished:
    /// order is host-local presentation.
    /// </summary>
    public void MoveColumn(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Columns.Count || to >= Columns.Count)
            return;

        Columns.Move(from, to);

        // rewrite only the slots the columns occupy — a selected language without a column, and
        // every unselected one, keeps its place in the catalog's order
        var slots = new List<int>();
        for (var i = 0; i < _columnOrder.Count; i++)
        {
            if (Columns.Any(c => string.Equals(c.Language, _columnOrder[i], StringComparison.OrdinalIgnoreCase)))
                slots.Add(i);
        }

        for (var s = 0; s < slots.Count && s < Columns.Count; s++)
            _columnOrder[slots[s]] = Columns[s].Language;

        RefreshSelectedLanguages();
    }

    /// <summary>Records which column was picked up; the drop is resolved against it.</summary>
    public void BeginColumnDrag(int index) =>
        _dragSource = index >= 0 && index < Columns.Count ? index : -1;

    /// <summary>Marks one edge of one column with the drop rule, and clears every other.</summary>
    public void UpdateColumnDropTarget(int hoveredIndex, bool before)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            var hit = _dragSource >= 0 && i == hoveredIndex;
            Columns[i].IsDropBefore = hit && before;
            Columns[i].IsDropAfter = hit && !before;
        }
    }

    /// <summary>Drag abandoned: no rule left on screen, no order changed.</summary>
    public void CancelColumnDrag()
    {
        _dragSource = -1;
        foreach (var column in Columns)
        {
            column.IsDropBefore = false;
            column.IsDropAfter = false;
        }
    }

    /// <summary>Commits the drag: the picked-up column lands on the marked edge.</summary>
    public void DropColumn(int hoveredIndex, bool before)
    {
        var from = _dragSource;
        CancelColumnDrag();
        if (from < 0 || hoveredIndex < 0 || hoveredIndex >= Columns.Count)
            return;

        var target = before ? hoveredIndex : hoveredIndex + 1;
        if (target > from)
            target--; // the column vacates its own slot before it lands

        MoveColumn(from, Math.Clamp(target, 0, Columns.Count - 1));
    }

    /// <summary>The five pipelines, in order. Unavailable ones stay in the list, disabled.</summary>
    public ObservableCollection<PipelineModeOption> Modes { get; } = new();

    /// <summary>Relay can be disabled (tests, fully offline use); QR is only shown when enabled.</summary>
    public bool RelayEnabled { get; set; } = true;

    /// <summary>Builds the publisher for a room id; tests substitute a recording fake.</summary>
    public Func<string, IRelayPublisher>? RelayPublisherFactory { get; set; }

    [ObservableProperty]
    private PipelineModeOption _selectedMode;

    /// <summary>ISO codes typed into the edit dialog's add row, e.g. "tr, nl".</summary>
    [ObservableProperty]
    private string _newLanguageInput = "";

    [ObservableProperty]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _status = "Idle.";

    /// <summary>Where the selected mode transcribes, named before Start rather than guessed.</summary>
    [ObservableProperty]
    private string _transcriptionStatus = "";

    /// <summary>Which engine will translate, named before Start rather than inferred from latency.</summary>
    [ObservableProperty]
    private string _translationStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyPropertyChangedFor(nameof(ShowMicLevel))]
    private bool _isRunning;

    /// <summary>Input peak 0–100, updated ~4×/s while live capture runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMicLevel))]
    private double _micLevel;

    public bool ShowMicLevel => IsRunning && NeedsMicrophone;

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

    /// <summary>An input device and a level meter only mean something for captured audio.</summary>
    public bool NeedsMicrophone => SelectedMode.Mode.NeedsMicrophone;

    partial void OnSelectedModeChanged(PipelineModeOption value)
    {
        OnPropertyChanged(nameof(NeedsMicrophone));
        OnPropertyChanged(nameof(ShowMicLevel));
        RefreshPipelineStatus();
    }

    /// <summary>
    /// Re-resolves every mode against the current settings: the two stage labels for the selected
    /// one, and the availability reason on all five. Called at construction, when the mode
    /// changes, and after the Settings dialog closes.
    /// </summary>
    public void RefreshPipelineStatus()
    {
        var settings = _loadSettings();
        var downloads = _downloads();

        foreach (var option in Modes)
            option.Unavailable = PipelinePlanner
                .Describe(option.Mode, settings, downloads).Unavailable;

        var status = PipelinePlanner.Describe(SelectedMode.Mode, settings, downloads);
        TranscriptionStatus = status.TranscriptionLabel;
        TranslationStatus = status.TranslationLabel;
    }

    /// <summary>
    /// Set for the length of <see cref="StopAsync"/>. Stopping publishes a final snapshot, says
    /// the room is closed and lets whatever is still translating land — a second or two during
    /// which both buttons would otherwise be live and a second press would race the first.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    private bool _isStopping;

    /// <summary>
    /// The room is open but off the record. One button carries both directions — an operator
    /// mid-meeting should not have to find a second control to undo the first.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    private bool _isPaused;

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    private bool CanStart() => !IsRunning && !IsStopping;

    private bool CanStop() => IsRunning && !IsStopping;

    private bool CanPause() => IsRunning && !IsStopping;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseAsync()
    {
        if (_session is null)
            return;

        var paused = !IsPaused;
        await _session.SetPausedAsync(paused);
        IsPaused = paused;
        Status = paused
            ? "Paused — nothing is being transcribed, translated or sent. The room stays open."
            : $"Live — {SelectedMode.Mode.Leaves}.";
    }

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
        IsPaused = false; // a new room is never inheriting the last one's pause
        // the selection is already capped at MaxLanguages; this reads the same constant so the
        // two can never disagree about how many columns a room has
        foreach (var lang in languages.Take(MaxLanguages))
            Columns.Add(new ColumnViewModel(lang));

        var mode = SelectedMode.Mode;
        var settings = _loadSettings();
        var plan = PipelinePlanner.Plan(mode, settings, _downloads());
        TranscriptionStatus = plan.Status.TranscriptionLabel;
        TranslationStatus = plan.Status.TranslationLabel;

        // A disabled row can still be reached programmatically, and settings can go stale
        // between opening the dropdown and pressing Start.
        if (plan.Status.Unavailable is not null)
        {
            SelectedMode.Unavailable = plan.Status.Unavailable;
            Status = plan.Status.Unavailable;
            return;
        }

        var asr = plan.Asr!;
        var mt = plan.Mt;
        _asr = asr;
        _mt = mt;

        var config = new RoomConfig(RoomIds.New(DateTime.Now), languages);
        var relaySettings = RelaySettings.FromEnvironment();
        var relay = CreateRelay(config.RoomId, relaySettings);

        // Phones hold the channel they scanned into, so the previous room has to be told
        // where the meeting went — otherwise a restart strands everyone until they rescan.
        if (_relay is not null)
        {
            await PublishSafeAsync(_relay, new RoomMovedMessage(config.RoomId));
            await _relay.DisposeAsync();
        }

        _relay = relay;

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
            await DisposeProvidersAsync();
            return;
        }

        _session = session;
        IsRunning = true;
        Status = mode.Id == PipelineModeId.Demo
            ? "Demo running." + (plan.Substitution is null ? "" : $" {plan.Substitution}")
            : $"Live — {mode.Leaves}.";

        if (RelayEnabled)
        {
            ShowJoinInfo(relaySettings.BuildJoinUrl(config.RoomId));
            _snapshotTimer.Start();
        }

        if (mode.NeedsMicrophone)
        {
            _captureCts = new CancellationTokenSource();
            _ = PumpMicrophoneAsync(session, SelectedDevice?.Id, _captureCts.Token);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        IsStopping = true;
        Status = "Stopping…";
        _snapshotTimer.Stop();
        _captureCts?.Cancel();
        _captureCts = null;

        try
        {
            if (_session is not null)
            {
                await PublishSnapshotSafeAsync(); // leave a final full state on the channel
                await PublishClosedSafeAsync();   // …and say the meeting is over, so phones stop waiting
                await _session.DisposeAsync();    // session object stays for rename/merge/export
            }

            await DisposeProvidersAsync();
            JoinUrl = "";
            QrImage = null;
            IsRunning = false;
            IsPaused = false;
            Status = "Stopped. Rename, merge and export still work on the last room.";
        }
        finally
        {
            // whatever went wrong above, the operator gets their buttons back — a host stuck
            // with Start and Stop both greyed out cannot be recovered without a restart
            IsStopping = false;
        }
    }

    /// <summary>
    /// Frees whichever pair the mode resolved to, without naming either — the view model has no
    /// vendor-typed field left. Prefers the async path: the local translator frees llama.cpp
    /// weights, and it waits for an in-flight decode before doing so rather than freeing memory
    /// out from under it. That wait belongs off the UI thread.
    /// </summary>
    private async Task DisposeProvidersAsync()
    {
        var asr = _asr;
        var mt = _mt;
        _asr = null;
        _mt = null;
        await DisposeAnyAsync(mt);
        await DisposeAnyAsync(asr);
    }

    private static async Task DisposeAnyAsync(object? provider)
    {
        switch (provider)
        {
            case IAsyncDisposable async:
                await async.DisposeAsync();
                break;
            case IDisposable sync:
                sync.Dispose();
                break;
        }
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

    private async Task PublishClosedSafeAsync()
    {
        try
        {
            if (_session is not null)
                await _session.PublishClosedAsync();
        }
        catch (Exception ex)
        {
            Status = $"Warning: close notice failed: {ex.Message}";
        }
    }

    private async Task PublishSafeAsync(IRelayPublisher relay, RelayMessage message)
    {
        try
        {
            await relay.PublishAsync(message);
        }
        catch (Exception ex)
        {
            Status = $"Warning: relay publish failed: {ex.Message}";
        }
    }

    private IRelayPublisher CreateRelay(string roomId, RelaySettings settings)
    {
        if (!RelayEnabled)
            return new NullRelayPublisher();
        return RelayPublisherFactory?.Invoke(roomId)
               ?? new SupabaseRelayPublisher(settings.SupabaseUrl, settings.AnonKey, roomId);
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
