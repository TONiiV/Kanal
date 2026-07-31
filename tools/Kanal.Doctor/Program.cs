using System.Runtime.InteropServices;
using Kanal.Audio;
using Kanal.Core.Providers;
using Kanal.Providers.Gladia;

// Kanal.Doctor — pipeline diagnostics (PRD D0-A / D0-B helpers).
//   doctor mic [seconds] [deviceIndex]   capture → resample → WAV + level report
//   doctor gladia <wav> [--fast]         stream a WAV to Gladia live, dump raw + normalized events

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
return command switch
{
    "mic" => await MicCheckAsync(
        args.Length > 1 && int.TryParse(args[1], out var s) ? s : 3,
        args.Length > 2 && int.TryParse(args[2], out var d) ? d : -1),
    "gladia" => await GladiaCheckAsync(
        args.Length > 1 ? args[1] : null,
        args.Contains("--fast")),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        Kanal.Doctor
          mic [seconds] [deviceIndex]   capture from the mic, write mic-check.wav, report levels
          gladia <wav> [--fast]         stream a 16 kHz mono WAV to Gladia live and dump messages
        """);
    return 1;
}

static async Task<int> MicCheckAsync(int seconds, int deviceIndex)
{
    var capture = AudioCaptureFactory.TryCreate();
    if (capture is null)
    {
        Console.WriteLine($"no capture backend for this platform ({RuntimeInformation.OSDescription}).");
        return 1;
    }

    var devices = capture.GetDevices();
    Console.WriteLine($"Capture devices ({devices.Count}):");
    for (var i = 0; i < devices.Count; i++)
        Console.WriteLine($"  [{i}] {devices[i].Name}");
    if (devices.Count == 0)
    {
        Console.WriteLine("NO capture devices found — check the OS sound settings / microphone privacy permissions.");
        return 2;
    }

    var deviceId = deviceIndex >= 0 && deviceIndex < devices.Count ? devices[deviceIndex].Id : null;
    Console.WriteLine($"\nRecording {seconds}s from {(deviceId is null ? "default device" : devices[deviceIndex].Name)} — say something…");

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    var frames = new List<byte>();
    var frameCount = 0;
    try
    {
        await foreach (var frame in capture.CaptureAsync(deviceId, cts.Token))
        {
            frames.AddRange(frame.ToArray());
            frameCount++;
        }
    }
    catch (OperationCanceledException)
    {
        // normal end of timed capture
    }
    catch (Exception ex)
    {
        Console.WriteLine($"CAPTURE FAILED: {ex.GetType().Name}: {ex.Message}");
        return 2;
    }

    var pcm = frames.ToArray();
    var samples = PcmConvert.BytesToShorts(pcm);
    if (samples.Length == 0)
    {
        Console.WriteLine("CAPTURE PRODUCED 0 SAMPLES — the device delivered no data.");
        return 2;
    }

    long sumSq = 0;
    int peak = 0;
    foreach (var s in samples)
    {
        sumSq += (long)s * s;
        peak = Math.Max(peak, Math.Abs((int)s));
    }

    var rms = Math.Sqrt(sumSq / (double)samples.Length);
    var rmsDb = 20 * Math.Log10(Math.Max(rms, 1) / short.MaxValue);
    var path = Path.GetFullPath("mic-check.wav");
    await using (var file = File.Create(path))
    {
        WavFile.Write(file, pcm, 16_000, 1);
    }

    Console.WriteLine($"\nframes: {frameCount}, samples: {samples.Length} ({samples.Length / 16_000.0:F1}s at 16 kHz)");
    Console.WriteLine($"peak: {peak} ({peak / (double)short.MaxValue:P0}), RMS: {rmsDb:F1} dBFS");
    Console.WriteLine($"WAV written: {path}  — play it back to verify.");
    Console.WriteLine(peak < 100
        ? "VERDICT: essentially SILENCE — wrong device, muted mic, or the OS withheld microphone permission."
        : "VERDICT: audio captured OK.");
    return peak < 100 ? 3 : 0;
}

static async Task<int> GladiaCheckAsync(string? wavPath, bool fast)
{
    var key = Environment.GetEnvironmentVariable("GLADIA_API_KEY")
              ?? (OperatingSystem.IsWindows()
                  ? Environment.GetEnvironmentVariable("GLADIA_API_KEY", EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable("GLADIA_API_KEY", EnvironmentVariableTarget.Machine)
                  : null);
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.WriteLine("GLADIA_API_KEY not set.");
        return 1;
    }

    if (wavPath is null || !File.Exists(wavPath))
    {
        Console.WriteLine("Usage: doctor gladia <wav> [--fast]  (run `doctor mic` first to produce mic-check.wav)");
        return 1;
    }

    Console.WriteLine("Initializing Gladia live session…");
    using var provider = new GladiaAsrProvider(new GladiaOptions { ApiKey = key.Trim() });
    IAsrSession session;
    try
    {
        session = await provider.StartAsync(
            new AsrSessionOptions(16_000, ["zh", "de", "pl", "en"], ["zh", "de", "pl", "en"]),
            CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SESSION INIT FAILED: {ex.Message}");
        return 2;
    }

    Console.WriteLine("Session up, websocket connected.");
    if (session is GladiaAsrSession gladia)
        gladia.RawMessageReceived += json =>
        {
            if (!json.Contains("\"audio_chunk\""))
                Console.WriteLine($"  RAW << {Truncate(json, 3000)}");
        };

    var reader = Task.Run(async () =>
    {
        await foreach (var e in session.Events)
        {
            switch (e)
            {
                case AsrEvent.Transcript t:
                    Console.WriteLine($"  EVENT [{(t.IsFinal ? "FINAL" : "partial")}] {t.SpeakerTag} {t.SrcLang}: {t.Text}" +
                                      (t.Translations is { Count: > 0 } tr ? $" | translations: {string.Join(", ", tr.Keys)}" : ""));
                    break;
                case AsrEvent.Error err:
                    Console.WriteLine($"  EVENT [error fatal={err.Fatal}] {err.Message}");
                    break;
                case AsrEvent.Ended end:
                    Console.WriteLine($"  EVENT [ended] {end.Reason}");
                    break;
            }
        }
    });

    Console.WriteLine($"Streaming {wavPath}{(fast ? " (fast)" : " (realtime pace)")}…");
    var source = new WavFileAudioSource(wavPath, realtime: !fast);
    await foreach (var frame in source.CaptureAsync(null, CancellationToken.None))
        await session.PushAudioAsync(frame);

    Console.WriteLine("Audio done; waiting 8s for trailing messages…");
    await Task.Delay(8_000);
    await session.DisposeAsync();
    await Task.WhenAny(reader, Task.Delay(2_000));
    Console.WriteLine("Done.");
    return 0;
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
