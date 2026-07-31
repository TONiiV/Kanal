using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Providers.LocalMt;

namespace Kanal.Host.ViewModels;

/// <summary>
/// One row in the TRANSLATION MODEL section: either the "Gladia cloud" default
/// (no download lifecycle) or a catalog model with download / cancel / delete.
/// </summary>
public partial class TranslationModelItemViewModel : ViewModelBase
{
    private readonly LocalModelInfo? _model;
    private readonly ModelDownloadManager? _downloads;
    private CancellationTokenSource? _downloadCts;

    /// <summary>The cloud row: translation stays with the ASR provider.</summary>
    public TranslationModelItemViewModel()
    {
    }

    public TranslationModelItemViewModel(LocalModelInfo model, ModelDownloadManager downloads)
    {
        _model = model;
        _downloads = downloads;
        IsDownloaded = downloads.IsDownloaded(model);
    }

    public bool IsLocal => _model is not null;

    public string? ModelId => _model?.Id;

    public string DisplayName => _model?.DisplayName ?? "Gladia cloud";

    public string MetaLabel => _model is null
        ? "Translated by the ASR provider — no download."
        : $"{_model.Parameters} · {_model.SizeLabel} · {_model.License}";

    public string? LicenseNote => _model?.LicenseNote;

    public bool HasLicenseNote => !string.IsNullOrEmpty(_model?.LicenseNote);

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

    public bool CanDelete => IsLocal && IsDownloaded && !IsDownloading;

    public string StatusLabel =>
        !IsLocal ? "" :
        Error.Length > 0 ? Error :
        IsDownloading ? $"downloading {(int)(Progress * 100)}%" :
        IsDownloaded ? "downloaded" : "not downloaded";

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_model is null || _downloads is null || IsDownloading || IsDownloaded)
            return;

        Error = "";
        Progress = 0;
        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        try
        {
            await _downloads.DownloadAsync(
                _model, new Progress<double>(p => Progress = p), _downloadCts.Token);
            IsDownloaded = true;
        }
        catch (OperationCanceledException)
        {
            // user pressed Cancel — no error, no file left behind
        }
        catch (Exception ex)
        {
            Error = $"download failed: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    /// <summary>Also called when the Settings window closes — a download nobody can see
    /// or cancel any more must not keep running against a discarded view model.</summary>
    [RelayCommand]
    public void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private void Delete()
    {
        if (_model is null || _downloads is null || IsDownloading)
            return;
        _downloads.Delete(_model);
        IsDownloaded = false;
        Error = "";
    }
}
