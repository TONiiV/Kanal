using System.Diagnostics;
using System.Text;
using Kanal.Audio;

namespace Kanal.Core.UnitTests;

public class MacAudioTimingDiagnostic
{
    [Fact]
    public async Task ReportFrameTiming()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var capture = AudioCaptureFactory.Create();
        var devices = capture.GetDevices();
        var report = new StringBuilder();
        report.AppendLine($"devices: {string.Join(" | ", devices.Select(d => $"{d.Name} [{d.Id}]"))}");
        if (devices.Count == 0)
            Assert.Fail(report.ToString());

        var frames = new List<(double Ms, int Bytes)>();
        var clock = Stopwatch.StartNew();
        double? first = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var frame in capture.CaptureAsync(null, cts.Token))
            {
                var now = clock.Elapsed.TotalMilliseconds;
                first ??= now;
                frames.Add((now, frame.Length));
                if (now - first >= 3000)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }

        report.AppendLine($"first frame after {first:F0} ms, {frames.Count} frames");
        report.AppendLine("first 30 frames (ms since first frame, bytes): " +
            string.Join(" ", frames.Take(30).Select(f => $"{f.Ms - first:F0}:{f.Bytes}")));
        report.AppendLine("frame sizes: " + string.Join(" ", frames.GroupBy(f => f.Bytes).Select(g => $"{g.Key}x{g.Count()}")));

        foreach (var (from, to) in new[] { (0, 700), (0, 3000), (1000, 3000) })
        {
            var bytes = frames.Where(f => f.Ms - first >= from && f.Ms - first < to).Sum(f => f.Bytes);
            report.AppendLine($"window {from}-{to} ms: {bytes} bytes = {bytes / 2.0 / ((to - from) / 1000.0):F0} samples/s");
        }

        Assert.Fail(report.ToString());
    }
}
