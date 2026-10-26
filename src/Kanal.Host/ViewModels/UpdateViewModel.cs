using System;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Updates;
using Kanal.Host.Localization;
using Kanal.Host.Services;

namespace Kanal.Host.ViewModels;

public sealed partial class UpdateViewModel : ViewModelBase
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    private readonly UpdateChecker? _checker;
    private readonly string _currentVersion;
    private readonly UpdateSchedule _schedule;
    private UpdateResult _result = UpdateResult.Unknown;
    private bool _isChecking;
    private bool _manualCheckFailed;
    private DispatcherTimer? _timer;

    public UpdateViewModel(UpdateChecker? checker, string currentVersion, TimeProvider? clock = null)
    {
        _checker = checker;
        _currentVersion = currentVersion;
        _schedule = new UpdateSchedule(clock ?? TimeProvider.System);
        if (checker is null)
            return;

        Localizer.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is Localizer.IndexerName)
                NotifyResult();
        };
    }

    public Func<bool> IsMeetingRunning { get; set; } = () => false;

    public UpdateStatus Status => _result.Status;

    public UpdateManifest? Manifest => _result.Manifest;

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HasStatus));
                CheckNowCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool HasNewerVersion => Status is UpdateStatus.UpdateAvailable or UpdateStatus.Unsupported;

    public bool ShowNotice => HasNewerVersion;

    public string NoticeText => HasNewerVersion
        ? Localizer.Instance.Format("update.notice", Manifest!.Latest)
        : "";

    public string StatusText
    {
        get
        {
            var localizer = Localizer.Instance;
            if (IsChecking)
                return localizer["update.status.checking"];
            if (_manualCheckFailed)
                return localizer["update.status.failed"];
            return Status switch
            {
                UpdateStatus.UpToDate => localizer["update.status.uptodate"],
                UpdateStatus.UpdateAvailable => localizer.Format("update.status.available", Manifest!.Latest),
                UpdateStatus.Unsupported => localizer.Format("update.status.unsupported", Manifest!.Latest),
                _ => "",
            };
        }
    }

    public bool HasStatus => StatusText.Length > 0;

    public string? Notes => HasNewerVersion && !string.IsNullOrWhiteSpace(Manifest!.Notes)
        ? Manifest.Notes
        : null;

    public bool ShowNotes => Notes is not null;

    public Uri? DownloadUrl => HasNewerVersion ? Manifest!.DownloadUrl : null;

    public bool ShowDownloadLink => DownloadUrl is not null;

    public bool ShowContactMaintainer => HasNewerVersion && DownloadUrl is null;

    public bool IsUnsupported => Status == UpdateStatus.Unsupported;

    public string? UnsupportedReason => IsUnsupported
        ? Localizer.Instance.Format("reason.versiontooold", Manifest!.Latest)
        : null;

    // The Localizer event is static and never unsubscribed, so an inert instance must not subscribe.
    public static UpdateViewModel CreateInert() => new(null, "0.0.0");

    public static UpdateViewModel CreateProduction()
    {
        var url = SettingsStore.ReadEnvAllScopes("KANAL_UPDATE_URL") ?? UpdateChecker.ProductionUrl;
        var endpoint = Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed
            : new Uri(UpdateChecker.ProductionUrl);
        return new UpdateViewModel(
            new UpdateChecker(new HttpClient(), endpoint),
            AppVersion.Current);
    }

    public void Start()
    {
        if (_timer is not null)
            return;

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => _ = CheckIfDueAsync();
        _timer.Start();
        _ = CheckIfDueAsync();
    }

    public Task CheckIfDueAsync() =>
        IsChecking || !_schedule.IsDue(IsMeetingRunning()) ? Task.CompletedTask : RunCheckAsync(manual: false);

    private bool CanCheckNow() => !IsChecking && !IsMeetingRunning();

    public void NotifyMeetingStateChanged() => CheckNowCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckNowAsync() => RunCheckAsync(manual: true);

    private async Task RunCheckAsync(bool manual)
    {
        _schedule.MarkChecked();
        IsChecking = true;
        UpdateResult result;
        try
        {
            result = _checker is null
                ? UpdateResult.Unknown
                : await _checker.CheckAsync(_currentVersion, UpdatePlatform.Current);
        }
        catch (Exception)
        {
            result = UpdateResult.Unknown;
        }

        _result = result.Status != UpdateStatus.Unknown ? result
            : _result.Status == UpdateStatus.Unsupported ? _result with { Status = UpdateStatus.UpdateAvailable }
            : _result;
        _manualCheckFailed = manual && result.Status == UpdateStatus.Unknown;
        IsChecking = false;
        NotifyResult();
    }

    private void NotifyResult()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Manifest));
        OnPropertyChanged(nameof(ShowNotice));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(ShowNotes));
        OnPropertyChanged(nameof(DownloadUrl));
        OnPropertyChanged(nameof(ShowDownloadLink));
        OnPropertyChanged(nameof(ShowContactMaintainer));
        OnPropertyChanged(nameof(IsUnsupported));
        OnPropertyChanged(nameof(UnsupportedReason));
    }
}
