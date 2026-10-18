using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Kanal.AsrBenchmark;

[ExcludeFromCodeCoverage]
internal sealed class LoadMonitor : IDisposable
{
    public const double QuietBelow = 3.0;

    private const int IntervalMs = 2000;

    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private readonly List<(DateTime At, double Cores)> _samples = [];
    private readonly int _self = Environment.ProcessId;
    private Dictionary<int, TimeSpan> _last = Snapshot();
    private DateTime _lastAt = DateTime.UtcNow;

    public LoadMonitor() => _timer = new Timer(_ => Sample(), null, IntervalMs, IntervalMs);

    public double Recent(int samples = 3)
    {
        lock (_gate)
            return _samples.Count == 0 ? 0 : _samples.TakeLast(samples).Average(s => s.Cores);
    }

    public double MeanSince(DateTime utc)
    {
        lock (_gate)
        {
            var window = _samples.Where(s => s.At >= utc).ToList();
            return window.Count == 0 ? Recent() : window.Average(s => s.Cores);
        }
    }

    public async Task<double> WaitQuietAsync(string what)
    {
        var waited = Stopwatch.StartNew();
        var lastLog = TimeSpan.Zero;
        while (true)
        {
            double worst;
            lock (_gate)
                worst = _samples.Count < 3 ? 0 : _samples.TakeLast(3).Max(s => s.Cores);
            if (worst < QuietBelow)
                return Recent();
            if (waited.Elapsed - lastLog >= TimeSpan.FromSeconds(60))
            {
                Console.WriteLine($"   waiting before {what}: other processes use {worst:F1} logical CPUs ({Top()}); limit {QuietBelow:F1}. Waited {waited.Elapsed.TotalMinutes:F0} min.");
                lastLog = waited.Elapsed;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose() => _timer.Dispose();

    private static string Top()
    {
        var before = Snapshot(byName: true);
        Thread.Sleep(2000);
        var after = Snapshot(byName: true);
        return string.Join(", ", after.Where(kv => before.ContainsKey(kv.Key))
            .Select(kv => (Name: kv.Key, Cores: (kv.Value - before[kv.Key]).TotalSeconds / 2))
            .Where(x => x.Cores >= 0.5).OrderByDescending(x => x.Cores).Take(3).Select(x => $"{x.Name} {x.Cores:F1}"));
    }

    private void Sample()
    {
        lock (_gate)
        {
            var now = Snapshot();
            var at = DateTime.UtcNow;
            var wall = (at - _lastAt).TotalSeconds;
            if (wall <= 0)
                return;
            var used = now.Where(kv => kv.Key != _self && _last.ContainsKey(kv.Key))
                .Sum(kv => Math.Max(0, (kv.Value - _last[kv.Key]).TotalSeconds));
            _samples.Add((at, used / wall));
            if (_samples.Count > 100_000)
                _samples.RemoveRange(0, 50_000);
            (_last, _lastAt) = (now, at);
        }
    }

    private static Dictionary<int, TimeSpan> Snapshot() =>
        Snapshot(byName: false).ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);

    private static Dictionary<string, TimeSpan> Snapshot(bool byName)
    {
        var result = new Dictionary<string, TimeSpan>();
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                try
                {
                    var key = byName ? process.ProcessName : process.Id.ToString();
                    result[key] = result.GetValueOrDefault(key) + process.TotalProcessorTime;
                }
                catch (Exception)
                {
                }
            }

        return result;
    }
}
