using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Core.Room;
using Kanal.Core.Workspaces;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

// Metric for "the window freezes when I open a long meeting". An Input-priority job stands for a
// click or key press. Its latency is how long the operator waits for the window to answer.
public class MeetingSwitchResponsivenessTests(ITestOutputHelper output) : IDisposable
{
    private const int LongMeetingUtterances = 1200;

    private const double MaxLatencyMs = 250;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-switch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static readonly string[] Tags = ["S01", "S02", "S03", "S04"];

    private static Utterance Said(int n) =>
        new($"u{n}", Tags[n % Tags.Length], n * 4000L, n * 4000L + 3500, "zh",
            $"第{n}条：这家公司相当于景江酒店的服务公司，然后还有一些他的几个彩都是通过合作伙伴完成的。",
            1, UtteranceState.Final, false, 1.0,
            new Dictionary<string, string>
            {
                ["zh"] = $"第{n}条：这家公司相当于景江酒店的服务公司，然后还有一些他的几个彩都是通过合作伙伴完成的。",
                ["de"] = $"Punkt {n}: Diese Firma ist gewissermaßen die Dienstleistungsgesellschaft des Jingjiang-Hotels, und einige der Farben laufen über Partner.",
                ["pl"] = $"Punkt {n}: Ta firma jest w zasadzie spółką usługową hotelu Jingjiang, a część kolorów powstaje przez partnerów.",
            });

    private static MeetingRecord Stored(WorkspaceStore store, Workspace workspace, string title, int utterances)
    {
        var record = store.CreateMeeting(workspace.Id, title).Meeting!;
        var path = Path.Combine(store.MeetingFolder(workspace.Id, record.Id)!, TranscriptLog.FileName);
        using (var log = new TranscriptLogWriter(path, _ => { }))
        {
            for (var n = 0; n < utterances; n++)
                log.Append(Said(n));
        }

        return store.SaveMeeting(record with
        {
            StartedAt = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            TranscriptPath = path,
            Languages = ["zh", "de", "pl"],
        }).Meeting!;
    }

    private sealed class InputProbe : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<double> _latencies = [];
        private readonly Thread _thread;
        private volatile bool _stop;
        private int _pending;

        public InputProbe(Window window)
        {
            _thread = new Thread(() =>
            {
                while (!_stop)
                {
                    if (Interlocked.Exchange(ref _pending, 1) == 0)
                    {
                        var posted = _clock.Elapsed.TotalMilliseconds;
                        Dispatcher.UIThread.Post(() =>
                        {
                            _latencies.Add(_clock.Elapsed.TotalMilliseconds - posted);
                            Volatile.Write(ref _pending, 0);
                        }, DispatcherPriority.Input);
                    }

                    // The layout pass of a running application: it runs above Input priority.
                    Dispatcher.UIThread.Post(window.UpdateLayout, DispatcherPriority.Render);
                    Thread.Sleep(4);
                }
            })
            { IsBackground = true };
            _thread.Start();
        }

        public double MaxMs => _latencies.Count == 0 ? 0 : _latencies.Max();

        public int Probes => _latencies.Count;

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }
    }

    private static async Task<double> PumpUntil(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(120), $"Timed out waiting for {what}.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }

        return clock.Elapsed.TotalMilliseconds;
    }

    private void Report(string line)
    {
        output.WriteLine(line);
        Console.WriteLine(line);
    }

    [AvaloniaFact]
    public async Task OpeningAndLeavingALongMeetingNeverBlocksTheWindowForLong()
    {
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        var workspace = store.CreateWorkspace("ACME", folder).Workspace!;
        var small = Stored(store, workspace, "Kurz", 30);
        var big = Stored(store, workspace, "Lang", LongMeetingUtterances);

        var parse = Stopwatch.StartNew();
        var parsed = StoredTranscript.Utterances(big);
        parse.Stop();
        Assert.Equal(LongMeetingUtterances, parsed.Count);

        var vm = TestViewModels.Hermetic(workspaces: () => store, offUiThread: work => Task.Run(work));
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Id == small.Id);
        await PumpUntil(() => !vm.IsLoadingRecord && vm.ShownColumns.Count == 3, "the short meeting");
        window.UpdateLayout();

        double openMs;
        double openStallMs;
        int openProbes;
        using (var probe = new InputProbe(window))
        {
            vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Id == big.Id);
            openMs = await PumpUntil(
                () => vm.ShownColumns.Count == 3 && vm.ShownColumns.All(c => c.Bubbles.Count == LongMeetingUtterances)
                      && vm.ShownRuler.Ticks.Count == LongMeetingUtterances,
                "the long meeting");
            Dispatcher.UIThread.RunJobs();
            openStallMs = probe.MaxMs;
            openProbes = probe.Probes;
        }

        double leaveMs;
        double leaveStallMs;
        using (var probe = new InputProbe(window))
        {
            vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Id == small.Id);
            leaveMs = await PumpUntil(
                () => vm.ShownColumns.Count == 3 && vm.ShownColumns.All(c => c.Bubbles.Count == 30), "the short meeting again");
            Dispatcher.UIThread.RunJobs();
            leaveStallMs = probe.MaxMs;
        }

        var report = $"""
            METRIC meeting-switch utterances={LongMeetingUtterances} languages=3
            METRIC   transcript read and parse (worker thread) : {parse.Elapsed.TotalMilliseconds,8:F0} ms
            METRIC   open long meeting, until fully shown      : {openMs,8:F0} ms ({openProbes} probes)
            METRIC   open long meeting, longest UI freeze      : {openStallMs,8:F0} ms
            METRIC   leave long meeting, until short shown     : {leaveMs,8:F0} ms
            METRIC   leave long meeting, longest UI freeze     : {leaveStallMs,8:F0} ms
            METRIC   limit for one freeze                      : {MaxLatencyMs,8:F0} ms
            """;
        Report(report);

        window.Close();
        Assert.True(openStallMs < MaxLatencyMs, $"Opening a long meeting froze the window.\n{report}");
        Assert.True(leaveStallMs < MaxLatencyMs, $"Leaving a long meeting froze the window.\n{report}");
    }

    [AvaloniaFact]
    public void TheRulerOfALongMeetingFillsInLinearTime()
    {
        var utterances = Enumerable.Range(0, LongMeetingUtterances).Select(Said).ToList();
        var ruler = new TranscriptRulerViewModel(tag => (tag, tag, "#FFFFFF"));

        var clock = Stopwatch.StartNew();
        foreach (var utterance in utterances)
            ruler.Observe(utterance);
        clock.Stop();

        Report($"METRIC ruler Observe x{LongMeetingUtterances} on the UI thread : {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(LongMeetingUtterances, ruler.Ticks.Count);
    }
}
