namespace Kanal.Core.Models;

/// <summary>One diarization model's download as a single state rather than three booleans. A
/// failure is a state, never an exception somebody has to turn into a dialog mid-meeting.</summary>
public sealed class DiarizationModelDownload
{
    private readonly ModelDownloadManager _downloads;
    private CancellationTokenSource? _cts;

    public DiarizationModelDownload(DiarizationModelInfo model, ModelDownloadManager downloads)
    {
        Model = model;
        _downloads = downloads;
        State = downloads.IsDownloaded(model)
            ? DiarizationReadiness.Ready
            : DiarizationReadiness.NotDownloaded;
    }

    public DiarizationModelInfo Model { get; }

    public DiarizationReadiness State { get; private set; }

    public double Progress { get; private set; }

    public string Error { get; private set; } = "";

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        if (State is DiarizationReadiness.Downloading or DiarizationReadiness.Ready)
            return;

        State = DiarizationReadiness.Downloading;
        Progress = 0;
        Error = "";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // Synchronous relay rather than Progress<double>: a posted callback can land after
            // the download has already finished and drag the state back to Downloading.
            await _downloads.DownloadAsync(Model, new Relay(OnProgress), _cts.Token);
            Progress = 1;
            State = DiarizationReadiness.Ready;
        }
        catch (OperationCanceledException)
        {
            Progress = 0;
            State = DiarizationReadiness.NotDownloaded;
        }
        catch (Exception ex)
        {
            Progress = 0;
            Error = ex.Message;
            State = DiarizationReadiness.Failed;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void Delete()
    {
        if (State == DiarizationReadiness.Downloading)
            return;
        _downloads.Delete(Model);
        Progress = 0;
        Error = "";
        State = DiarizationReadiness.NotDownloaded;
    }

    private void OnProgress(double value)
    {
        if (State == DiarizationReadiness.Downloading)
            Progress = value;
    }

    private sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
