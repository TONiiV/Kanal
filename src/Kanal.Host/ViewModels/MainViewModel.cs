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
using Kanal.Core.Diagnostics;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Relay;
using Kanal.Core.Room;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Providers.LocalMt;
using QRCoder;

namespace Kanal.Host.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Dictionary<string, Speaker> _speakerModels = new();
    private readonly Dictionary<string, string> _tagToCanonical = new();
    private readonly DispatcherTimer _snapshotTimer;
    private MeetingSession? _session;
    /// <summary>Outlives its session: the next Start uses it to redirect phones to the new room.</summary>
    private IRelayPublisher? _relay;
    private CancellationTokenSource? _captureCts;
    private MeetingRecorder? _recorder;
    private IAsrProvider? _asr;
    private IMtProvider? _mt;
    private readonly Func<AppSettings> _loadSettings;
    private readonly Func<ModelDownloadManager> _downloads;
    /// <summary>Enumeration source for the device dropdown; the capture pump opens its own.</summary>
    private readonly Func<IAudioCaptureService?> _captureFactory;
    private IAudioDeviceWatcher? _deviceWatcher;
    /// <summary>Null means the planner's default: stored key first, then the environment.</summary>
    private readonly PipelinePlanner.KeyResolver? _resolveKey;

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
        : this(SettingsStore.Load, () => new ModelDownloadManager(SettingsStore.ModelsPath),
            deviceWatcherFactory: AudioCaptureFactory.TryCreateDeviceWatcher)
    {
    }

    /// <summary>
    /// Test seam, in the shape of <see cref="RelayPublisherFactory"/>: both halves of "what does
    /// this machine translate with" are injected. Headless runs must not read the developer's
    /// real %APPDATA%\Kanal\settings.json — on a machine with a model downloaded, that made a
    /// UI test load a multi-gigabyte LLM and behave differently per developer. The key resolver
    /// is injected for the same reason: the default falls back to the ambient GLADIA_API_KEY,
    /// which made "this mode is unavailable without a key" untestable on a machine that has one.
    /// The capture factory feeds the device dropdown, and the watcher factory delivers hot-plug
    /// notifications for it — tests hand in a fake watcher they fire by hand.
    /// </summary>
    public MainViewModel(
        Func<AppSettings> loadSettings,
        Func<ModelDownloadManager> downloads,
        PipelinePlanner.KeyResolver? resolveKey = null,
        Func<IAudioCaptureService?>? captureFactory = null,
        Func<IAudioDeviceWatcher?>? deviceWatcherFactory = null)
    {
        _loadSettings = loadSettings;
        _downloads = downloads;
        _resolveKey = resolveKey;
        _captureFactory = captureFactory ?? AudioCaptureFactory.TryCreate;

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

        // no capture backend or no devices — demo mode still works
        RefreshDevices();

        // Only the production constructor passes the real platform factory: a native listener
        // created by every headless test would pile up registrations for hardware no test sees.
        _deviceWatcher = deviceWatcherFactory?.Invoke();
        if (_deviceWatcher is not null)
            _deviceWatcher.DevicesChanged += OnDevicesChanged;

        RefreshPipelineStatus();

        // The mode list and the two stage labels are built once; without this, switching the
        // application's language left five English rows on an otherwise translated screen.
        Localizer.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not Localizer.IndexerName)
                return;

            foreach (var option in Modes)
                option.RefreshText();
            OnPropertyChanged(nameof(SelectedLanguageSummary));
            OnPropertyChanged(nameof(LanguageLimitNotice));
            OnPropertyChanged(nameof(PauseLabel));
            RefreshPipelineStatus();
        };
    }

    private static Localizer L => Localizer.Instance;

    /// <summary>Watcher callbacks arrive on a CoreAudio/COM thread; Avalonia throws off the UI thread.</summary>
    private void OnDevicesChanged() => Dispatcher.UIThread.Post(RefreshDevices);

    /// <summary>
    /// Re-enumerates into <see cref="Devices"/>. The selection survives by its stable id —
    /// enumeration builds fresh instances every time — and an unplugged selection falls back
    /// to the list head, which the backends already order default-first. A capture already
    /// running keeps the device id it was started with: the dropdown updates, the meeting
    /// does not switch microphones mid-sentence.
    /// </summary>
    private void RefreshDevices()
    {
        IReadOnlyList<AudioDeviceInfo> fresh;
        try
        {
            fresh = _captureFactory()?.GetDevices() ?? [];
        }
        catch
        {
            return; // enumeration can fail transiently mid-unplug; a stale list beats none
        }

        var selectedId = SelectedDevice?.Id;
        Devices.Clear();
        foreach (var device in fresh)
            Devices.Add(device);
        SelectedDevice = fresh.FirstOrDefault(d => d.Id == selectedId) ?? Devices.FirstOrDefault();
    }

    /// <summary>Called from MainWindow.OnClosed: the native listener must not outlive the window.</summary>
    public void Dispose()
    {
        if (_deviceWatcher is null)
            return;
        _deviceWatcher.DevicesChanged -= OnDevicesChanged;
        _deviceWatcher.Dispose();
        _deviceWatcher = null;
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
        ? L["languages.none"]
        : string.Join(" · ", SelectedLanguages.Select(o => o.Code.ToUpperInvariant()));

    /// <summary>True once four languages are selected: every other row is refused until one goes.</summary>
    public bool IsAtLanguageLimit => SelectedLanguages.Count >= MaxLanguages;

    /// <summary>
    /// Why the remaining rows are disabled. Printed in the catalog dialog beside the rows and the
    /// add-by-code row, both of which obey the same cap — a click that does nothing and says
    /// nothing is the failure this replaces.
    /// </summary>
    public string LanguageLimitNotice => IsAtLanguageLimit
        ? L["langdlg.limit"]
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

    /// <summary>Loads relay runtime configuration; injectable so tests never read ambient secrets.</summary>
    public Func<RelaySettings> RelaySettingsFactory { get; set; } = RelaySettings.FromEnvironment;

    /// <summary>
    /// Test seam, in the shape of <see cref="RelayPublisherFactory"/>: rewrites the resolved
    /// plan before Start uses it, so a headless test can substitute providers — a translator
    /// whose load is held open, an ASR wrapper that records whether transcription began —
    /// without a real model on disk.
    /// </summary>
    public Func<PipelinePlan, PipelinePlan>? PlanFilter { get; set; }

    [ObservableProperty]
    private PipelineModeOption _selectedMode;

    /// <summary>ISO codes typed into the edit dialog's add row, e.g. "tr, nl".</summary>
    [ObservableProperty]
    private string _newLanguageInput = "";

    [ObservableProperty]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _status = Localizer.Instance["status.idle"];

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJoinError))]
    private string _joinError = "";

    public bool HasJoinInfo => JoinUrl.Length > 0;

    /// <summary>Shows relay bootstrap failures where the operator expected the join QR.</summary>
    public bool HasJoinError => JoinError.Length > 0;

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
                .Describe(option.Mode, settings, downloads, _resolveKey).Unavailable;

        var status = PipelinePlanner.Describe(SelectedMode.Mode, settings, downloads, _resolveKey);
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
    /// Set while Start is loading a local translation model — seconds to tens of seconds in
    /// which the room is not yet open and nothing is being transcribed. Start stays refused
    /// (no second load behind the first), and Stop stays offered: the loading phase is the
    /// operator's to abort, not a wait they are locked into.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    private bool _isStarting;

    /// <summary>Cancels a model load in progress; null outside the loading phase.</summary>
    private CancellationTokenSource? _warmupCts;

    /// <summary>
    /// The room is open but off the record. One button carries both directions — an operator
    /// mid-meeting should not have to find a second control to undo the first.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    private bool _isPaused;

    public string PauseLabel => L[IsPaused ? "transport.resume" : "transport.pause"];

    private bool CanStart() => !IsRunning && !IsStopping && !IsStarting;

    // Stop is offered while a model is still loading: pressing it then aborts the load.
    private bool CanStop() => (IsRunning || IsStarting) && !IsStopping;

    private bool CanPause() => IsRunning && !IsStopping && !IsStarting;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseAsync()
    {
        if (_session is null)
            return;

        var paused = !IsPaused;
        await _session.SetPausedAsync(paused);
        IsPaused = paused;
        Status = paused
            ? L["status.paused"]
            : L.Format("status.live", SelectedMode.Mode.Leaves);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var languages = SelectedLanguages.Select(o => o.Code.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (languages.Count == 0)
        {
            Status = L["status.pickalanguage"];
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
        var plan = PipelinePlanner.Plan(mode, settings, _downloads(), _resolveKey);
        if (PlanFilter is not null)
            plan = PlanFilter(plan);
        TranscriptionStatus = plan.Status.TranscriptionLabel;
        TranslationStatus = plan.Status.TranslationLabel;

        // A disabled row can still be reached programmatically, and settings can go stale
        // between opening the dropdown and pressing Start.
        if (plan.Status.Unavailable is not null)
        {
            SelectedMode.Unavailable = plan.Status.Unavailable;
            Status = plan.Status.Unavailable;
            Log.Warning(RoomLog, $"Start refused: mode {mode.Id} is unavailable.");
            return;
        }

        var asr = plan.Asr!;
        var mt = plan.Mt;
        _asr = asr;
        _mt = mt;

        // A local translation model loads to a working state *before* the room opens. Loading
        // it on the first final — which is what lazy loading did — meant the meeting's opening
        // sentences waited out a multi-gigabyte load with nothing on screen saying why.
        // Capability-checked, not vendor-checked: whatever declares a warm-up gets one. The
        // load runs off the UI thread; Stop cancels it; a load that fails stops the Start
        // rather than opening a room that cannot translate.
        if (mt is IWarmupProvider warmable)
        {
            IsStarting = true;
            Status = L["status.loadingmodel"];
            var warmupCts = new CancellationTokenSource();
            _warmupCts = warmupCts;
            try
            {
                await Task.Run(() => warmable.WarmUpAsync(warmupCts.Token));
            }
            catch (OperationCanceledException)
            {
                await DisposeProvidersAsync();
                Status = L["status.idle"];
                return;
            }
            catch (Exception ex)
            {
                await DisposeProvidersAsync();
                Status = L.Format("status.modelloadfailed", ex.Message);
                Log.Error(RoomLog, "The translation model failed to load; the room was not opened.", ex);
                return;
            }
            finally
            {
                _warmupCts = null;
                warmupCts.Dispose();
                IsStarting = false;
            }
        }

        var config = new RoomConfig(RoomIds.New(DateTime.Now), languages);
        var relaySettings = RelaySettingsFactory();
        var signingKey = RelaySigningKey.Create();
        RelayConnection relayConnection;
        try
        {
            relayConnection = await CreateRelayAsync(
                config.RoomId,
                relaySettings,
                signingKey);
        }
        catch (Exception ex)
        {
            // The meeting is the primary function; mobile delivery is optional. A missing,
            // unreachable, or rejected gateway must remove the QR, not prevent transcription.
            // There is deliberately no public Supabase fallback: the null publisher keeps the
            // secure boundary while the operator gets an explicit degraded-mode warning.
            relayConnection = new RelayConnection(
                new SignedRelayPublisher(new NullRelayPublisher(), signingKey),
                null,
                null,
                ex.Message);
            // "The QR code doesn't work" is the likeliest call this tool will ever generate, and
            // the warning it produces on screen is gone the moment the next status line lands.
            Log.Warning(RelayLog, "The relay could not be set up; the room is running without a QR code.", ex);
        }
        var relay = relayConnection.Publisher;

        // Phones hold the channel they scanned into, so the previous room has to be told
        // where the meeting went — otherwise a restart strands everyone until they rescan.
        if (_relay is not null)
        {
            if (relayConnection.InviteTicket is not null)
            {
                await PublishSafeAsync(
                    _relay,
                    new RoomMovedMessage(
                        config.RoomId,
                        signingKey.VerificationKey,
                        relayConnection.InviteTicket));
            }
            await _relay.DisposeAsync();
        }

        _relay = relay;

        var session = new MeetingSession(asr, mt, relay, config);

        session.Room.UtteranceUpserted += u => Dispatcher.UIThread.Post(() => ApplyUtterance(u));
        session.Room.SpeakerUpserted += s => Dispatcher.UIThread.Post(() => ApplySpeaker(s));
        session.ErrorOccurred += e =>
        {
            // Logged off the dispatcher: a fatal error that stops the room must be on disk even
            // if the UI thread never gets round to showing it.
            Log.Write(
                e.Fatal ? LogLevel.Error : LogLevel.Warning, RoomLog, Bounded(e.Message), error: null);
            Dispatcher.UIThread.Post(() =>
                Status = L.Format(e.Fatal ? "status.fatal" : "status.warning", e.Message));
        };
        session.SessionEnded += reason => Dispatcher.UIThread.Post(() =>
            Status = L.Format("status.sessionended", reason ?? L["status.done"]));

        try
        {
            await session.StartAsync();
        }
        catch (Exception ex)
        {
            Status = L.Format("status.startfailed", ex.Message);
            Log.Error(RoomLog, $"Room {config.RoomId} failed to start.", ex);
            await session.DisposeAsync();
            _relay = null;
            await relay.DisposeAsync();
            await DisposeProvidersAsync();
            return;
        }

        _session = session;
        IsRunning = true;
        Log.Info(
            RoomLog,
            $"Room {config.RoomId} open: mode {mode.Id}, languages {string.Join("/", languages)}, " +
            $"relay {(RelayEnabled ? "on" : "off")}.");
        var runningStatus = mode.Id == PipelineModeId.Demo
            ? L["status.demorunning"] + (plan.Substitution is null ? "" : $" {plan.Substitution}")
            : L.Format("status.live", mode.Leaves);
        Status = relayConnection.Warning is null
            ? runningStatus
            : $"{runningStatus} {L.Format("status.relayunavailable", relayConnection.Warning)}";
        JoinError = relayConnection.Warning is null
            ? ""
            : L.Format("join.unavailable", relayConnection.Warning);

        // Hung off the session's own tap, not the capture loop: pause promises that nothing said
        // in that minute is kept, and a second pause check here would be a second place for that
        // promise to quietly stop being true. Placed after the session started and the status
        // line is set — a start that fails must not leave a recorder holding an open file under
        // a lit RECORDING label, and a recording that cannot start appends its failure to the
        // status rather than being overwritten by it. The tap only fires once the microphone
        // pump below pushes audio, so nothing is missed by attaching here.
        StartRecording(session, mode, settings, config.RoomId);

        if (RelayEnabled && relayConnection.GatewayUrl is not null &&
            relayConnection.InviteTicket is not null)
        {
            ShowJoinInfo((relaySettings with { GatewayUrl = relayConnection.GatewayUrl }).BuildJoinUrl(
                relayConnection.InviteTicket,
                config.RoomId,
                signingKey.VerificationKey));
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
        Status = L["status.stopping"];

        try
        {
            // Inside the try on purpose: Cancel() runs the token's callbacks synchronously and
            // rethrows what they throw, and anything escaping before the finally would leave
            // IsStopping latched — the exact both-buttons-grey wedge the finally exists to prevent.
            _snapshotTimer.Stop();
            _warmupCts?.Cancel(); // a model still loading is abandoned, not waited out
            _captureCts?.Cancel();
            _captureCts = null;

            if (_session is not null)
            {
                await PublishSnapshotSafeAsync(); // leave a final full state on the channel
                await PublishClosedSafeAsync();   // …and say the meeting is over, so phones stop waiting
                await _session.DisposeAsync();    // session object stays for rename/merge/export
            }

            await DisposeProvidersAsync();
            StopRecording();
            JoinUrl = "";
            QrImage = null;
            JoinError = "";
            IsRunning = false;
            IsPaused = false;
            Status = _lastRecording.Length > 0
                ? L.Format("status.stopped.audio", _lastRecording)
                : L["status.stopped"];
            Log.Info(RoomLog, "Room closed.");
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
            {
                await _session.PublishSnapshotAsync();
                // Every 15 s while a room is open: a phone that shows nothing is either not
                // receiving these or not rendering them, and the file settles which.
                Log.Debug(RelayLog, "Snapshot published.");
            }
        }
        catch (Exception ex)
        {
            Status = L.Format("status.warning", L.Format("status.snapshotfailed", ex.Message));
            Log.Warning(RelayLog, "The closing snapshot did not publish.", ex);
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
            Status = L.Format("status.warning", L.Format("status.closefailed", ex.Message));
            Log.Warning(RelayLog, "The room-closed message did not publish; phones may still be waiting.", ex);
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
            Status = L.Format("status.warning", L.Format("status.publishfailed", ex.Message));
            Log.Warning(RelayLog, $"A {message.GetType().Name} did not publish.", ex);
        }
    }

    // Categories, so a line can be traced to what produced it without reading the message.
    private const string RoomLog = "room";
    private const string RelayLog = "relay";
    private const string AudioLog = "audio";

    /// <summary>
    /// Caps a message the host did not write. A provider's or a gateway's error text is passed
    /// through verbatim and can carry a whole rejected payload — which is a log line the length of
    /// a meeting, and, since the payload is what was said in the room, more of it on disk than the
    /// failure needs.
    /// </summary>
    private static string Bounded(string message) =>
        message.Length <= 300 ? message : message[..300] + "…";

    private async Task<RelayConnection> CreateRelayAsync(
        string roomId,
        RelaySettings settings,
        RelaySigningKey signingKey)
    {
        if (!RelayEnabled)
            return new RelayConnection(
                new SignedRelayPublisher(new NullRelayPublisher(), signingKey),
                null,
                null,
                null);

        if (RelayPublisherFactory is not null)
            return new RelayConnection(
                new SignedRelayPublisher(RelayPublisherFactory(roomId), signingKey),
                settings.GatewayUrl ?? "https://relay.test/kanal-relay",
                "test-reader-ticket",
                null);

        if (!settings.IsConfigured)
            throw new InvalidOperationException(
                "Set KANAL_RELAY_URL and KANAL_RELAY_HOST_TOKEN before enabling the relay.");

        var room = await GatewayRelayPublisher.CreateRoomAsync(
            settings.GatewayUrl!,
            settings.HostToken!,
            roomId,
            signingKey.VerificationKey);
        return new RelayConnection(
            new SignedRelayPublisher(room.Publisher, signingKey),
            settings.GatewayUrl,
            room.InviteTicket,
            null);
    }

    private sealed record RelayConnection(
        IRelayPublisher Publisher,
        string? GatewayUrl,
        string? InviteTicket,
        string? Warning);

    private async Task PumpMicrophoneAsync(MeetingSession session, string? deviceId, CancellationToken ct)
    {
        var capture = AudioCaptureFactory.TryCreate();
        if (capture is null)
        {
            Dispatcher.UIThread.Post(() => Status = L["status.nobackend"]);
            return;
        }

        Log.Debug(AudioLog, $"Capture opened on {deviceId ?? "the default device"}.");

        try
        {
            var framesSinceMeter = 0;
            var frames = 0L;
            await foreach (var frame in capture.CaptureAsync(deviceId, ct))
            {
                await session.PushAudioAsync(frame, ct);

                // A count, never a sample: the point of the Debug level is answering "was audio
                // still arriving at 14:32", which is the question a silent transcript raises.
                if (++frames % 500 == 0)
                    Log.Debug(AudioLog, $"{frames} frames captured.");

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
            Log.Error(AudioLog, "Capture stopped; the room is live with no audio arriving.", ex);
            Dispatcher.UIThread.Post(() => Status = L.Format("status.audiofailed", ex.Message));
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

    /// <summary>
    /// Where a meeting's audio is written, or null when it is not being recorded — scripted
    /// modes have no audio, and the operator can turn it off. Decided in one place so the
    /// indicator on screen and the file on disk cannot disagree.
    /// </summary>
    public static string? RecordingPathFor(PipelineMode mode, AppSettings settings, string roomId) =>
        mode.NeedsMicrophone && settings.RecordAudio
            ? Path.Combine(SettingsStore.ResolveAudioFolder(settings), $"{roomId}.wav")
            : null;

    private void StartRecording(MeetingSession session, PipelineMode mode, AppSettings settings, string roomId)
    {
        _lastRecording = ""; // a scripted run after a recorded one must not report the old file
        var path = RecordingPathFor(mode, settings, roomId);
        if (path is null)
            return;

        try
        {
            // MeetingRecorder owns the failure policy: Write never throws — an exception
            // escaping onto the capture thread would take the capture loop, and with it the
            // meeting, down along with the recording. The callback runs on that thread, so
            // every UI mutation goes through the dispatcher; raising PropertyChanged directly
            // here would hand Avalonia a binding update off the UI thread.
            var recorder = new MeetingRecorder(new WavWriter(path), reason =>
            {
                _recorder = null;
                _lastRecording = path; // what was written so far is patched and still plays
                // the room was told it was being recorded; it has to be told that stopped
                _ = session.SetRecordingAsync(false);
                Dispatcher.UIThread.Post(() =>
                {
                    RecordingPath = "";
                    Status = L.Format("status.recordingstopped", reason);
                });
            });
            _recorder = recorder;
            RecordingPath = path;
            session.AudioAccepted += frame => recorder.Write(frame.Span);
            // The operator's status bar is read by the operator alone. The people whose voices
            // are being written to the file read the phone, so the room is told as well.
            _ = session.SetRecordingAsync(true);
        }
        catch (Exception ex)
        {
            // A meeting that cannot be recorded is still a meeting worth holding — an
            // unwritable folder must not stop Start. Appended rather than assigned: the
            // "Live —" line was just set, and a message the operator never sees is a meeting
            // they believe is being recorded when it is not.
            var note = L.Format("status.notrecording", ex.Message);
            Status = $"{Status} {note}";
            Log.Warning(AudioLog, $"The room is not being recorded: {path} could not be opened.", ex);
        }
    }

    private void StopRecording()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is not null)
        {
            recorder.Dispose();
            _lastRecording = recorder.Path;
        }

        RecordingPath = "";
    }

    /// <summary>The finished recording, named in the Stop message so it can actually be found.</summary>
    private string _lastRecording = "";

    /// <summary>The file the meeting is being written to; empty when nothing is being recorded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording))]
    private string _recordingPath = "";

    public bool IsRecording => RecordingPath.Length > 0;

    /// <summary>
    /// Asks the operator where the transcript goes, given a suggested folder and file name.
    /// Returns null if they cancelled. Set by the view; without it — headless, tests — export
    /// falls back to the configured folder rather than opening a dialog that cannot exist.
    /// </summary>
    public Func<string, string, Task<string?>>? ChooseExportPath { get; set; }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        if (_session is null)
        {
            Status = L["status.nothingtoexport"];
            return;
        }

        var snapshot = _session.Room.Snapshot();
        var folder = SettingsStore.ResolveTranscriptFolder(_loadSettings());
        var name = $"{snapshot.Config.RoomId}.md";

        var path = ChooseExportPath is null
            ? Path.Combine(folder, name)
            : await ChooseExportPath(folder, name);
        if (path is null)
        {
            Status = L["status.exportcancelled"];
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Kanal — {snapshot.Config.RoomId}");
        sb.AppendLine();
        foreach (var u in snapshot.Utterances.Where(u => u.State == UtteranceState.Final))
        {
            var (speaker, _) = ResolveSpeaker(u.SpeakerTag);
            sb.AppendLine($"**{speaker}** ({u.SrcLang}): {u.SrcText}");
            foreach (var (lang, text) in u.Translations.OrderBy(t => t.Key))
                sb.AppendLine($"  - {lang}: {text}");
            sb.AppendLine();
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);

            // A meeting has two artefacts and one of them was never chosen in a dialog; naming
            // both here is the only moment the operator is told where the recording went.
            var audio = _recorder?.Path ?? (_lastRecording.Length > 0 ? _lastRecording : null);
            Status = audio is null
                ? L.Format("status.exported", path)
                : L.Format("status.exported.audio", path, audio);
        }
        catch (Exception ex)
        {
            // Losing the transcript at the last step is the worst possible moment for a throw
            // out of a command nothing is awaiting: read-only folder, full disk, revoked rights.
            Status = L.Format("status.exportfailed", ex.Message);
            Log.Error(RoomLog, $"The transcript could not be written to {path}.", ex);
        }
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
