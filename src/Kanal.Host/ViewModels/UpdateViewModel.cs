using System;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Updates;
using Kanal.Host.Services;

namespace Kanal.Host.ViewModels;

public sealed partial class UpdateViewModel(
    UpdateChecker checker,
    string currentVersion,
    TimeProvider? clock = null) : ViewModelBase
{
    public Func<bool> IsMeetingRunning { get; set; } = () => false;

    public UpdateStatus Status => UpdateStatus.Unknown;

    public UpdateManifest? Manifest => null;

    public bool IsChecking => false;

    public bool ShowNotice => false;

    public string NoticeText => "";

    public string StatusText => "";

    public string? Notes => null;

    public Uri? DownloadUrl => null;

    public bool ShowDownloadLink => false;

    public bool ShowContactMaintainer => false;

    public bool IsUnsupported => false;

    public string? UnsupportedReason => null;

    public static UpdateViewModel CreateProduction()
    {
        var url = SettingsStore.ReadEnvAllScopes("KANAL_UPDATE_URL") ?? UpdateChecker.ProductionUrl;
        return new UpdateViewModel(
            new UpdateChecker(new HttpClient(), new Uri(url)),
            AppVersion.Current);
    }

    public Task CheckIfDueAsync() => throw new NotImplementedException();

    [RelayCommand]
    private Task CheckNowAsync() => throw new NotImplementedException();
}
