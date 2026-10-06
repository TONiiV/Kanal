using System.Text.Encodings.Web;
using System.Text.Json;
using Kanal.Audio;

internal static class OnlineAudioDoctor
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string Usage =
        "Use: system <seconds:1..120> | online <seconds:1..120> [microphoneIndex]. Run devices first.";

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0].ToLowerInvariant();
        try
        {
            var microphones = AudioCaptureFactory.TryCreate();
            var computer = SystemAudioCaptureFactory.TryCreate();
            if (command == "devices")
            {
                List(OnlineMeetingCapture.MicrophoneSource, microphones?.GetDevices() ?? []);
                Console.WriteLine(JsonSerializer.Serialize(SystemAudioCaptureFactory.Support, Json));
                return CaptureCheck.Ok;
            }

            if (args.Length < 2 || !int.TryParse(args[1], out var seconds) || seconds is < 1 or > 120)
                return UsageError("seconds must be a whole number from 1 to 120.");
            if (!TryIndex(args, 2, out var microphoneIndex))
                return UsageError("the microphone index must be a whole number.");
            if (computer is null)
                throw new AudioCaptureException(AudioCaptureFault.SourceFailed,
                    SystemAudioCaptureFactory.Support.Reason ?? "Computer-audio capture is unavailable on this platform.",
                    OnlineMeetingCapture.SystemSource);

            Console.WriteLine("Local diagnostic only: nothing is written to disk and nothing is sent over the network.");
            return command == "online"
                ? await OnlineAsync(microphones, computer, microphoneIndex, seconds)
                : await SystemAsync(computer, seconds);
        }
        catch (Exception error)
        {
            var fault = AudioCaptureException.Classify(error, OnlineMeetingCapture.SystemSource);
            WriteFault(fault);
            return CaptureCheck.Failed;
        }
    }

    private static async Task<int> SystemAsync(ISystemAudioCaptureService computer, int seconds)
    {
        var check = new CaptureCheck(TimeProvider.System, OnlineMeetingCapture.SystemSource);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var received = 0L;
        try
        {
            await foreach (var frame in computer.CaptureAsync(stop.Token))
            {
                received += frame.Length / sizeof(short);
                var peak = 0;
                foreach (var sample in PcmConvert.BytesToShorts(frame.Span))
                    peak = Math.Max(peak, Math.Abs((int)sample));
                check.Observe(OnlineMeetingCapture.SystemSource, received, peak / 32768.0);
            }

            if (!stop.IsCancellationRequested)
                throw new AudioCaptureException(AudioCaptureFault.SourceEnded,
                    "The computer audio stopped arriving.", OnlineMeetingCapture.SystemSource);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            var fault = AudioCaptureException.Classify(error, OnlineMeetingCapture.SystemSource);
            check.Fail(fault);
            WriteFault(fault);
        }

        return Conclude(check, seconds);
    }

    private static async Task<int> OnlineAsync(
        IAudioCaptureService? microphones, ISystemAudioCaptureService computer, int microphoneIndex, int seconds)
    {
        if (microphones is null)
            throw new AudioCaptureException(AudioCaptureFault.SourceFailed,
                "Microphone capture is unavailable on this platform.", OnlineMeetingCapture.MicrophoneSource);
        var microphone = Pick(microphones.GetDevices(), microphoneIndex, OnlineMeetingCapture.MicrophoneSource);
        var check = new CaptureCheck(
            TimeProvider.System, OnlineMeetingCapture.MicrophoneSource, OnlineMeetingCapture.SystemSource);
        var capture = new OnlineMeetingCapture(microphones, computer);
        capture.Diagnostic += diagnostic =>
        {
            if (check.Observe(diagnostic))
                Console.WriteLine(JsonSerializer.Serialize(diagnostic, Json));
        };

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            await foreach (var _ in capture.CaptureAsync(microphone.Id, stop.Token))
            {
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            var fault = AudioCaptureException.Classify(error, OnlineMeetingCapture.MixerSource);
            check.Fail(fault);
            WriteFault(fault);
        }

        return Conclude(check, seconds);
    }

    private static int Conclude(CaptureCheck check, int seconds)
    {
        foreach (var source in check.Summaries)
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                @event = "summary",
                source = source.Source,
                state = source.State,
                received = source.ReceivedSamples,
                dropped = source.DroppedSamples,
                padded = source.PaddedSamples,
                peak = Math.Round(source.Peak, 4),
                fault = source.Fault,
                seconds,
                sampleRate = AudioCaptureFormat.SampleRateHz,
            }, Json));

        var exit = check.ExitCode;
        if (exit == CaptureCheck.Failed)
            Console.WriteLine("FAULT: see the fault line above and docs/online-meeting-audio.md.");
        else if (exit == CaptureCheck.Silent)
            foreach (var source in check.SilentSources)
                Console.WriteLine(source == OnlineMeetingCapture.MicrophoneSource
                    ? "SILENT microphone: speak into it; check the selected microphone, its mute switch and the microphone permission."
                    : "SILENT system: play speech on this computer; check the meeting app's volume, mute and the capture permission.");
        else
            Console.WriteLine("OK: every source carried sound.");
        return exit;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { @event = "fault", code = "usage", message = $"{message} {Usage}" }, Json));
        return CaptureCheck.Failed;
    }

    private static void WriteFault(AudioCaptureException fault) =>
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            @event = "fault",
            code = fault.Code,
            source = fault.SourceName,
            type = (fault.InnerException ?? fault).GetType().Name,
            message = fault.Message,
        }, Json));

    private static bool TryIndex(string[] args, int position, out int index)
    {
        index = 0;
        return args.Length <= position || int.TryParse(args[position], out index);
    }

    private static AudioDeviceInfo Pick(IReadOnlyList<AudioDeviceInfo> devices, int index, string source)
    {
        if (index < 0 || index >= devices.Count)
            throw new AudioCaptureException(AudioCaptureFault.DeviceUnavailable,
                $"There is no active {source} device at index {index} ({devices.Count} listed); run devices and pick one of its indices.",
                source);
        return devices[index];
    }

    private static void List(string source, IReadOnlyList<AudioDeviceInfo> devices)
    {
        for (var i = 0; i < devices.Count; i++)
            Console.WriteLine(JsonSerializer.Serialize(new { source, index = i, id = devices[i].Id, name = devices[i].Name }, Json));
    }
}
