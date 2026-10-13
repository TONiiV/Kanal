using Avalonia.Headless.XUnit;
using Kanal.Core.Models;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class DownloadTrayTests
{
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state) { DueAt = _ticks + dueTime.Ticks };
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _ticks += by.Ticks;
            foreach (var timer in _timers.Where(t => t.DueAt <= _ticks).ToList())
            {
                timer.DueAt = null;
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long? DueAt;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => clock._timers.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static (ModelRows Rows, DownloadTray Tray, ManualClock Clock) Build(HttpMessageHandler? handler = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanal-tests", Guid.NewGuid().ToString("N"));
        var http = new HttpClient(handler ?? new SettingsViewModelDownloadTests.StalledHandler());
        var rows = new ModelRows(new ModelDownloadManager(dir, http));
        var clock = new ManualClock();
        return (rows, new DownloadTray(rows, clock, post: action => action()), clock);
    }

    private static void Finish(ModelItemViewModel row, bool downloaded = false, string error = "")
    {
        row.IsDownloaded = downloaded;
        row.Error = error;
        row.IsDownloading = false;
    }

    [AvaloniaFact]
    public void NothingShowsUntilARowStartsDownloading()
    {
        var (_, tray, _) = Build();

        Assert.Empty(tray.Items);
    }

    [AvaloniaFact]
    public void AnItemAppearsWhenARowStartsDownloading()
    {
        var (rows, tray, _) = Build();
        var row = rows.Transcription[0];

        row.IsDownloading = true;

        var item = Assert.Single(tray.Items);
        Assert.Equal(row.DisplayName, item.Name);
        Assert.Equal(DownloadState.Running, item.State);
    }

    [AvaloniaFact]
    public void TheNoneTranslationRowNeverShows()
    {
        var (rows, tray, _) = Build();

        rows.Translation.First(r => !r.IsLocal).IsDownloading = true;

        Assert.Empty(tray.Items);
    }

    [AvaloniaFact]
    public void TranslationAndTranscriptionDownloadsShowSideBySideWithTheirOwnIcons()
    {
        var (rows, tray, _) = Build();

        rows.Transcription[0].IsDownloading = true;
        rows.Translation.First(r => r.IsLocal).IsDownloading = true;

        Assert.Equal(["transcript", "translate"], tray.Items.Select(i => i.IconName));
    }

    [AvaloniaFact]
    public void TheSummaryNamesTheModelThePercentAndTheMegabytes()
    {
        var (rows, tray, _) = Build();
        var row = rows.Transcription[0];
        row.IsDownloading = true;

        row.Progress = 0.5;

        var summary = Assert.Single(tray.Items).Summary;
        Assert.StartsWith($"{row.DisplayName} · 50% · ", summary);
        Assert.Contains(" / ", summary);
        Assert.EndsWith(" MB", summary);
    }

    [AvaloniaFact]
    public void TheRowAndTheTraySayTheSameProgress()
    {
        var (rows, tray, _) = Build();
        var row = rows.Transcription[0];
        row.IsDownloading = true;

        row.Progress = 0.5;

        Assert.Matches(@"^50% · [\d.]+ / [\d.]+ MB$", row.ProgressLabel);
        Assert.Equal($"{row.DisplayName} · {row.ProgressLabel}", Assert.Single(tray.Items).Summary);
        Assert.Equal(row.ProgressLabel, row.StatusLabel);
    }

    [AvaloniaFact]
    public void ARowFailureMessageStillShowsInTheRow()
    {
        var (rows, _, _) = Build();
        var row = rows.Transcription[0];

        Finish(row, error: "network lost");

        Assert.Equal("network lost", row.StatusLabel);
    }

    [AvaloniaFact]
    public async Task TheRowRingHasTheTrayIconAndCancelsTheRow()
    {
        var (rows, _, _) = Build();
        var row = rows.Transcription[0];
        var ring = row.Download;
        Assert.Equal("transcript", ring.IconName);
        Assert.Equal("translate", rows.Translation.First(r => r.IsLocal).Download.IconName);

        var download = row.DownloadCommand.ExecuteAsync(null);
        row.Progress = 0.25;
        Assert.True(ring.IsRunning);
        Assert.Equal(90, ring.RingSweep, precision: 6);

        ring.CancelCommand.Execute(null);
        await download;

        Assert.False(row.IsDownloading);
    }

    [AvaloniaFact]
    public void TheRingSweepFollowsProgress()
    {
        var (rows, tray, _) = Build();
        var row = rows.Transcription[0];
        row.IsDownloading = true;

        row.Progress = 0.25;

        Assert.Equal(90, Assert.Single(tray.Items).RingSweep, precision: 6);
    }

    [AvaloniaFact]
    public async Task CancellingThroughTheIndicatorCancelsTheRow()
    {
        var handler = new SettingsViewModelDownloadTests.StalledHandler();
        var (rows, tray, _) = Build(handler);
        var row = rows.Transcription[0];
        var download = row.DownloadCommand.ExecuteAsync(null);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(tray.Items).CancelCommand.Execute(null);
        await download.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(row.IsDownloading);
        Assert.Equal("", row.Error);
        Assert.Equal(DownloadState.Cancelled, Assert.Single(tray.Items).State);
    }

    [AvaloniaFact]
    public void ADoneItemTurnsDoneAndLeavesAfterThreeSeconds()
    {
        var (rows, tray, clock) = Build();
        var row = rows.Transcription[0];
        row.IsDownloading = true;

        Finish(row, downloaded: true);

        Assert.Equal(DownloadState.Done, Assert.Single(tray.Items).State);
        clock.Advance(TimeSpan.FromMilliseconds(2999));
        Assert.Single(tray.Items);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(tray.Items);
    }

    [AvaloniaFact]
    public void ACancelledItemLeavesAfterTwoSeconds()
    {
        var (rows, tray, clock) = Build();
        var row = rows.Transcription[0];
        row.IsDownloading = true;

        Finish(row);

        Assert.Equal(DownloadState.Cancelled, Assert.Single(tray.Items).State);
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Single(tray.Items);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(tray.Items);
    }

    [AvaloniaFact]
    public void AFailedItemShowsTheReasonAndLeavesAfterEightSeconds()
    {
        var (rows, tray, clock) = Build();
        var row = rows.Transcription[0];
        row.IsDownloading = true;

        Finish(row, error: "network lost");

        var item = Assert.Single(tray.Items);
        Assert.Equal(DownloadState.Failed, item.State);
        Assert.Contains("network lost", item.Summary);
        Assert.Equal(360, item.RingSweep, precision: 0);
        clock.Advance(TimeSpan.FromMilliseconds(7999));
        Assert.Single(tray.Items);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(tray.Items);
    }

    [AvaloniaFact]
    public void RestartingADownloadReplacesItsFinishedItem()
    {
        var (rows, tray, clock) = Build();
        var row = rows.Transcription[0];
        row.IsDownloading = true;
        Finish(row, error: "network lost");

        row.IsDownloading = true;
        clock.Advance(TimeSpan.FromSeconds(10));

        var item = Assert.Single(tray.Items);
        Assert.Equal(DownloadState.Running, item.State);
    }

    [AvaloniaFact]
    public void TheMainViewModelOwnsATrayFedByItsRows()
    {
        var vm = TestViewModels.Hermetic();

        vm.ModelRows.Transcription[0].IsDownloading = true;

        Assert.Single(vm.DownloadTray.Items);
    }
}
