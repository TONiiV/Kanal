using System.Runtime.InteropServices;
using Kanal.Audio;
using System.Diagnostics;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Providers.LocalAsr;
using Kanal.Providers.Gladia;

// Kanal.Doctor — pipeline diagnostics (PRD D0-A / D0-B helpers).
//   doctor mic [seconds] [deviceIndex]   capture → resample → WAV + level report
//   doctor gladia <wav> [--fast]         stream a WAV to Gladia live, dump raw + normalized events
//   doctor asr <wav> [modelsDir]         run the local Nemotron model over a WAV, print a Markdown report

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
return command switch
{
    "mic" => await MicCheckAsync(
        args.Length > 1 && int.TryParse(args[1], out var s) ? s : 3,
        args.Length > 2 && int.TryParse(args[2], out var d) ? d : -1),
    "gladia" => await GladiaCheckAsync(
        args.Length > 1 ? args[1] : null,
        args.Contains("--fast")),
    "asr" => await AsrCheckAsync(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        Kanal.Doctor
          mic [seconds] [deviceIndex]   capture from the mic, write mic-check.wav, report levels
          gladia <wav> [--fast]         stream a 16 kHz mono WAV to Gladia live and dump messages
          asr <wav> [modelsDir]         download the default local ASR model if needed, transcribe the WAV
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
            new AsrSessionOptions(16_000, ["zh", "de", "pl", "en"]),
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

static async Task<int> AsrCheckAsync(string? wavPath, string? modelsDir)
{
    if (wavPath is null || !File.Exists(wavPath))
    {
        Console.WriteLine("Usage: doctor asr <wav> [modelsDir]");
        return 1;
    }

    var model = AsrModelCatalog.Models[0];
    var downloads = new ModelDownloadManager(modelsDir ?? Path.Combine(Path.GetTempPath(), "kanal-models"));
    var missing = downloads.MissingParts(model.Parts);
    var download = Stopwatch.StartNew();
    if (missing.Count > 0)
        await downloads.DownloadAsync(missing, null, CancellationToken.None);
    download.Stop();

    using var provider = new NemotronAsrProvider(model, downloads);
    var load = Stopwatch.StartNew();
    await provider.WarmUpAsync(CancellationToken.None);
    load.Stop();

    string[][] rooms = [["zh"], ["zh", "de", "pl", "en"]];
    var runs = new List<(string[] Room, double Seconds, List<string> Finals)>();
    foreach (var room in rooms)
    {
        var session = await provider.StartAsync(new AsrSessionOptions(16_000, room), CancellationToken.None);
        var finals = new List<string>();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in session.Events)
                if (e is AsrEvent.Transcript { IsFinal: true } t)
                    finals.Add($"{t.SrcLang}: {t.Text}");
        });

        var run = Stopwatch.StartNew();
        var source = new WavFileAudioSource(wavPath, realtime: false);
        await foreach (var frame in source.CaptureAsync(null, CancellationToken.None))
            await session.PushAudioAsync(frame);
        await session.DisposeAsync();
        await reader;
        runs.Add((room, run.Elapsed.TotalSeconds, finals));
    }

    var duration = WavDuration(wavPath);
    Console.WriteLine($"### Local ASR on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
    Console.WriteLine();
    Console.WriteLine($"- model: `{model.Id}`, CPU, {Math.Clamp(Environment.ProcessorCount / 2, 1, 4)} threads of {Environment.ProcessorCount} logical cores");
    Console.WriteLine($"- NVIDIA GPU: {NvidiaGpu()}");
    Console.WriteLine($"- download {(missing.Count > 0 ? $"{download.Elapsed.TotalSeconds:F1} s" : "cached")}, load {load.Elapsed.TotalSeconds:F1} s");
    Console.WriteLine($"- audio {Path.GetFileName(wavPath)}, {duration:F1} s");
    foreach (var (room, seconds, finals) in runs)
    {
        Console.WriteLine();
        Console.WriteLine($"**room `{string.Join(",", room)}`** — {seconds:F2} s, real-time factor {seconds / duration:F2}, {finals.Count} final(s)");
        foreach (var f in finals)
            Console.WriteLine($"> {f}");
    }

    return runs.All(r => r.Finals.Count > 0) ? 0 : 3;
}

static double WavDuration(string path)
{
    using var stream = File.OpenRead(path);
    var wav = WavFile.Read(stream);
    return wav.Pcm16.Length / 2.0 / wav.Channels / wav.SampleRateHz;
}

static string NvidiaGpu()
{
    try
    {
        using var smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name,driver_version,memory.total --format=csv,noheader")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = smi.StandardOutput.ReadToEnd().Trim();
        smi.WaitForExit();
        return smi.ExitCode == 0 && output.Length > 0 ? output.ReplaceLineEndings("; ") : $"nvidia-smi failed (exit {smi.ExitCode})";
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return "none (nvidia-smi not found)";
    }
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
