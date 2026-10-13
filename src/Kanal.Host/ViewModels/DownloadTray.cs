using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Host.Controls;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public enum DownloadState
{
    Running,
    Done,
    Cancelled,
    Failed,
}

public sealed partial class DownloadItemViewModel : ViewModelBase
{
    private const double FullRing = 359.9;

    public DownloadItemViewModel(ModelItemViewModel row, string iconName)
    {
        Row = row;
        IconName = iconName;
        row.PropertyChanged += OnRowChanged;
    }

    public ModelItemViewModel Row { get; }

    public string IconName { get; }

    public Geometry Icon => Icons.Of(IconName);

    public ITimer? HideTimer { get; set; }

    public string Name => Row.DisplayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsCancelled))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(RingSweep))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private DownloadState _state;

    public bool IsRunning => State == DownloadState.Running;

    public bool IsDone => State == DownloadState.Done;

    public bool IsCancelled => State == DownloadState.Cancelled;

    public bool IsFailed => State == DownloadState.Failed;

    public double RingSweep =>
        State is DownloadState.Done or DownloadState.Failed
            ? FullRing
            : Math.Min(FullRing, Row.Progress * 360);

    public string Summary => State switch
    {
        DownloadState.Running =>
            $"{Name} · {(int)(Row.Progress * 100)}% · {Megabytes(Row.Progress * Row.SizeBytes)} / {Megabytes(Row.SizeBytes)} MB",
        DownloadState.Done => $"{Name} · {Localizer.Instance["download.done"]}",
        DownloadState.Cancelled => $"{Name} · {Localizer.Instance["download.cancelled"]}",
        _ => $"{Name} · {Row.Error}",
    };

    public string CancelLabel => Localizer.Instance.Format("download.cancel", Name);

    [RelayCommand]
    private void Cancel() => Row.CancelDownload();

    public void Detach() => Row.PropertyChanged -= OnRowChanged;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelItemViewModel.Progress) or nameof(ModelItemViewModel.Error))
        {
            OnPropertyChanged(nameof(RingSweep));
            OnPropertyChanged(nameof(Summary));
        }
    }

    private static string Megabytes(double bytes)
    {
        var mb = bytes / (1024 * 1024);
        return mb.ToString(mb >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);
    }
}

public sealed class DownloadTray
{
    private static readonly TimeSpan DoneLinger = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CancelledLinger = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailedLinger = TimeSpan.FromSeconds(8);

    private readonly TimeProvider _clock;
    private readonly Action<Action> _post;

    public DownloadTray(ModelRows rows, TimeProvider? clock = null, Action<Action>? post = null)
    {
        _clock = clock ?? TimeProvider.System;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        foreach (var row in rows.Translation.Where(r => r.IsLocal))
            Watch(row, "translate");
        foreach (var row in rows.Transcription)
            Watch(row, "transcript");
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    public void Watch(ModelItemViewModel row, string iconName) =>
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ModelItemViewModel.IsDownloading))
                return;
            if (row.IsDownloading)
                Start(row, iconName);
            else
                Finish(row);
        };

    private void Start(ModelItemViewModel row, string iconName)
    {
        foreach (var old in Items.Where(i => i.Row == row).ToList())
            Remove(old);
        Items.Add(new DownloadItemViewModel(row, iconName));
    }

    private void Finish(ModelItemViewModel row)
    {
        var item = Items.FirstOrDefault(i => i.Row == row && i.IsRunning);
        if (item is null)
            return;

        item.State = row.IsDownloaded ? DownloadState.Done
            : row.Error.Length > 0 ? DownloadState.Failed
            : DownloadState.Cancelled;
        var linger = item.State switch
        {
            DownloadState.Done => DoneLinger,
            DownloadState.Failed => FailedLinger,
            _ => CancelledLinger,
        };
        item.HideTimer = _clock.CreateTimer(_ => _post(() => Remove(item)), null, linger, Timeout.InfiniteTimeSpan);
    }

    private void Remove(DownloadItemViewModel item)
    {
        item.HideTimer?.Dispose();
        item.Detach();
        Items.Remove(item);
    }
}
