using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Host.Localization;
using Kanal.Core.Models;
using Kanal.Providers.LocalAsr;
using Kanal.Providers.LocalMt;

namespace Kanal.Host.ViewModels;

public partial class ModelItemViewModel : ViewModelBase
{
    private readonly ModelDownloadManager? _downloads;
    private readonly IReadOnlyList<IDownloadableFile> _parts = [];
    private readonly string? _name;
    private readonly string? _meta;
    private readonly bool _transcription;
    private CancellationTokenSource? _downloadCts;

    public ModelItemViewModel()
    {
    }

    public ModelItemViewModel(LocalModelInfo model, ModelDownloadManager downloads)
        : this(model.Id, model.DisplayName, model.Parameters, model.SizeLabel, model.License,
            model.LicenseNote, [model], downloads, transcription: false, model.SizeBytes)
    {
    }

    public ModelItemViewModel(AsrModelInfo model, ModelDownloadManager downloads)
        : this(model.Id, model.DisplayName, model.Parameters, model.SizeLabel, model.License,
            model.LicenseNote, model.Parts, downloads, transcription: true, model.SizeBytes)
    {
    }

    private ModelItemViewModel(
        string id, string name, string parameters, string size, string license, string? licenseNote,
        IReadOnlyList<IDownloadableFile> parts, ModelDownloadManager downloads, bool transcription, long sizeBytes)
    {
        _transcription = transcription;
        SizeBytes = sizeBytes;
        ModelId = id;
        _name = name;
        _meta = $"{parameters} · {size} · {license}";
        LicenseNote = licenseNote;
        _parts = parts;
        _downloads = downloads;
        IsDownloaded = downloads.IsDownloaded(parts);
    }

    public bool IsLocal => _downloads is not null;

    public string? ModelId { get; }

    public long SizeBytes { get; }

    public string DisplayName => _name ?? Localizer.Instance["settings.model.none"];

    public string MetaLabel => _meta ?? Localizer.Instance["settings.model.none.note"];

    public string? LicenseNote { get; }

    public bool HasChoice { get; set; } = true;

    public bool HasLicenseNote => !string.IsNullOrEmpty(LicenseNote);

    // Avalonia scopes GroupName to the window, so both lists sharing one name would be one group.
    public string RadioGroup => _transcription ? "activeTranscriptionModel" : "activeModel";

    public string UseTip => Localizer.Instance[_transcription ? "settings.asrmodel.usetip" : "settings.model.usetip"];

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isDownloading;

    /// <summary>0..1 while a download runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private string _error = "";

    public bool CanDownload => IsLocal && !IsDownloaded && !IsDownloading;

    public bool CanDelete =>
        IsLocal && !IsDownloading && (IsDownloaded || _parts.Any(_downloads!.IsDownloaded));

    public string StatusLabel =>
        !IsLocal ? "" :
        Error.Length > 0 ? Error :
        IsDownloading ? Localizer.Instance.Format("settings.model.downloading", (int)(Progress * 100)) :
        Localizer.Instance[IsDownloaded ? "settings.model.downloaded" : "settings.model.notdownloaded"];

    /// <summary>Re-reads this row's strings after the application's language changes.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(MetaLabel));
        OnPropertyChanged(nameof(UseTip));
        OnPropertyChanged(nameof(StatusLabel));
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_downloads is null || IsDownloading || IsDownloaded)
            return;

        Error = "";
        Progress = 0;
        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        try
        {
            await _downloads.DownloadAsync(
                _parts, new Progress<double>(p => Progress = p), _downloadCts.Token);
            IsDownloaded = true;
        }
        catch (OperationCanceledException)
        {
            // user pressed Cancel
        }
        catch (Exception ex)
        {
            Error = Localizer.Instance.Format("settings.model.downloadfailed", ex.Message);
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    public void CancelDownload() => _downloadCts?.Cancel();

    public void RefreshDownloaded()
    {
        if (_downloads is not null && !IsDownloading)
            IsDownloaded = _downloads.IsDownloaded(_parts);
    }

    [RelayCommand]
    private void Delete()
    {
        if (_downloads is null || IsDownloading)
            return;
        _downloads.Delete(_parts);
        IsDownloaded = false;
        Error = "";
        OnPropertyChanged(nameof(CanDelete));
    }
}
