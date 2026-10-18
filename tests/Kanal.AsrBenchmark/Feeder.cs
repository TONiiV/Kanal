using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Kanal.Core.Providers;

namespace Kanal.AsrBenchmark;

[ExcludeFromCodeCoverage]
internal static class Feeder
{
    public static async Task<ClipRun> RunClipAsync(Func<List<double>, IAsrProvider> make, Clip clip, string[] room, bool realtime,
        double padding = 0, bool paced = false, Action<AsrEvent.Transcript, Stopwatch>? onTranscript = null)
    {
        const int frame = Corpus.Rate / 50;
        var decodeMs = new List<double>();
        var session = await make(decodeMs).StartAsync(new AsrSessionOptions(Corpus.Rate, room), CancellationToken.None);
        var clock = Stopwatch.StartNew();
        var events = new List<(double At, AsrEvent.Transcript Event)>();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in session.Events)
                if (e is AsrEvent.Transcript t)
                {
                    events.Add((clock.Elapsed.TotalSeconds, t));
                    onTranscript?.Invoke(t, clock);
                }
                else if (e is AsrEvent.Error error)
                    Console.WriteLine($"   {clip.Id}: {error.Message}");
        });

        var samples = clip.Samples;
        var onset = Scoring.SpeechOnset(samples, frame);
        var end = Scoring.SpeechEnd(samples, frame);
        var total = samples.Length + (int)((realtime ? 4 : paced ? padding : 0) * Corpus.Rate);
        double onsetAt = 0, endAt = 0;
        for (var i = 0; i < total; i += frame)
        {
            if (realtime || paced)
            {
                var wait = TimeSpan.FromSeconds(i / (double)Corpus.Rate) - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait);
            }

            if (i == onset)
                onsetAt = clock.Elapsed.TotalSeconds;
            var chunk = i < samples.Length
                ? Kanal.Audio.PcmConvert.ShortsToBytes(samples.AsSpan(i, Math.Min(frame, samples.Length - i)))
                : new byte[frame * 2];
            await session.PushAudioAsync(chunk);
            if (i + frame == end || (i < end && i + frame > end))
                endAt = clock.Elapsed.TotalSeconds;
        }

        await session.DisposeAsync();
        await reader;
        var seconds = clock.Elapsed.TotalSeconds;
        var finals = events.Where(e => e.Event.IsFinal).ToList();
        var timed = realtime || paced;
        return new ClipRun(
            clip,
            string.Join(" ", finals.Select(f => f.Event.Text)).Trim(),
            finals.Select(f => f.Event.SrcLang).ToList(),
            seconds,
            decodeMs,
            timed && events.Count > 0 ? events[0].At - onsetAt : null,
            timed && finals.Count > 0 ? finals[^1].At - endAt : null,
            padding,
            endAt);
    }

    public static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString()?.Trim() ?? "?";
            var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "sysctl" : "sh",
                OperatingSystem.IsMacOS() ? "-n machdep.cpu.brand_string" : "-c \"grep -m1 'model name' /proc/cpuinfo | cut -d: -f2\"")
            { RedirectStandardOutput = true };
            using var process = Process.Start(info)!;
            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch (Exception)
        {
            return RuntimeInformation.ProcessArchitecture.ToString();
        }
    }
}
