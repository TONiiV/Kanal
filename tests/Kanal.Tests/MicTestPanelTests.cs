using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Audio;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.Tests;

/// <summary>
/// The microphone test the operator runs before anyone sits down. The one question it exists to
/// answer is the one that cannot be answered by staring at the columns during the meeting: is
/// this device going to work in this room.
/// </summary>
public class MicTestPanelTests : IDisposable
{
    private readonly string _previousLanguage = Localizer.Instance.Current;

    /// <summary>
    /// Every verdict here is asserted verbatim in English, and the application language defaults
    /// to the machine's own — so without this the whole class passes or fails by desktop locale.
    /// </summary>
    public MicTestPanelTests() => Localizer.Instance.Current = "en";

    public void Dispose() => Localizer.Instance.Current = _previousLanguage;

    /// <summary>Plays generated audio down the capture interface — no device, no room.</summary>
    private sealed class ScriptedCapture(IReadOnlyList<byte[]> frames) : IAudioCaptureService
    {
        public string? OpenedDevice;
        public bool Closed;

        public IReadOnlyList<AudioDeviceInfo> GetDevices() =>
        [
            new("mic-1", "Table microphone"),
            new("mic-2", "Laptop microphone"),
        ];

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
            string? deviceId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            OpenedDevice = deviceId;
            try
            {
                foreach (var frame in frames)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return frame;
                    await Task.Yield();
                }

                // stays open the way a real device does, until cancelled
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                Closed = true;
            }
        }
    }

    private static byte[] Tone(double amplitude, int samples = 1600)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)Math.Round(short.MaxValue * amplitude * Math.Sin(i * 0.12));
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return pcm;
    }

    /// <summary>Digital zeros — a dead device, not a quiet room.</summary>
    private static List<byte[]> Silence(int frames) =>
        Enumerable.Range(0, frames).Select(_ => new byte[3200]).ToList();

    /// <summary>What a real room sounds like between sentences: quiet, but not nothing.</summary>
    private static List<byte[]> RoomTone(int frames) =>
        Enumerable.Range(0, frames).Select(_ => Tone(0.004)).ToList();

    /// <summary>
    /// A capture with one frame still in flight when the stop lands: it delivers a full-scale
    /// frame after cancellation before dying, the way a real device's already-queued buffer can.
    /// The test holds <see cref="Release"/> until the next session has started, pinning the
    /// interleaving this exists to catch.
    /// </summary>
    private sealed class LingeringCapture : IAudioCaptureService
    {
        public readonly TaskCompletionSource Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AudioDeviceInfo> GetDevices() => [new("mic-1", "Table microphone")];

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
            string? deviceId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new byte[3200];
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            await Release.Task;
            yield return Tone(1.0); // the frame that was already on its way when Stop landed
            throw new OperationCanceledException(ct);
        }
    }

    /// <summary>Wording is pinned to one platform so the assertions hold on any CI OS.</summary>
    private static SettingsViewModel Panel(IAudioCaptureService capture) =>
        new(new AppSettings(), () => capture, isMacOs: false);

    private static async Task PumpAsync(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DevicesAreListedAndOneIsPreselected()
    {
        var vm = Panel(new ScriptedCapture([]));

        Assert.Equal(2, vm.Devices.Count);
        Assert.Equal("Table microphone", vm.TestDevice!.Name);
        Assert.False(vm.IsTesting);
        Assert.False(vm.StopTestCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task AHealthyMicrophoneInAQuietRoomPasses()
    {
        var frames = RoomTone(30).Concat(Enumerable.Range(0, 30).Select(_ => Tone(0.35))).ToList();
        var vm = Panel(new ScriptedCapture(frames));

        vm.StartTestCommand.Execute(null);
        await PumpAsync(400);

        Assert.True(vm.IsTesting);
        Assert.Equal("Good", vm.VerdictLabel);
        Assert.Contains("above the room", vm.VerdictDetail);
        Assert.Contains("margin", vm.LevelReadout);
        vm.StopTestCommand.Execute(null);
    }

    /// <summary>
    /// Gaps of digital zeros are a device gating or delivering nothing, not a very quiet room.
    /// Quoting a margin measured against the clamp would be precision the number does not have.
    /// </summary>
    [AvaloniaFact]
    public async Task PerfectlySilentGapsAreNotQuotedAsAMeasuredRoom()
    {
        var frames = Silence(30).Concat(Enumerable.Range(0, 30).Select(_ => Tone(0.35))).ToList();
        var vm = Panel(new ScriptedCapture(frames));

        vm.StartTestCommand.Execute(null);
        await PumpAsync(400);

        Assert.Equal("Good", vm.VerdictLabel);
        Assert.DoesNotContain("above the room", vm.VerdictDetail);
        Assert.Contains("gating", vm.VerdictDetail);
        Assert.Contains("room silent", vm.LevelReadout);
        vm.StopTestCommand.Execute(null);
    }

    /// <summary>The failure this panel exists to catch before the meeting rather than during it.</summary>
    [AvaloniaFact]
    public async Task ADeadMicrophoneIsNamedAsSuch()
    {
        var vm = Panel(new ScriptedCapture(Silence(40)));

        vm.StartTestCommand.Execute(null);
        await PumpAsync(400);

        Assert.Equal("Nothing is arriving", vm.VerdictLabel);
        Assert.Contains("muted", vm.VerdictDetail);
        vm.StopTestCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task ClippingIsNamedAndExplained()
    {
        var frames = Silence(20).Concat(Enumerable.Range(0, 20).Select(_ => Tone(1.0))).ToList();
        var vm = Panel(new ScriptedCapture(frames));

        vm.StartTestCommand.Execute(null);
        await PumpAsync(400);

        Assert.Equal("Clipping", vm.VerdictLabel);
        Assert.Contains("Lower the input level", vm.VerdictDetail);
        vm.StopTestCommand.Execute(null);
    }

    /// <summary>The panel tests the device it was pointed at, not whatever is default.</summary>
    [AvaloniaFact]
    public async Task TheChosenDeviceIsTheOneOpened()
    {
        var capture = new ScriptedCapture(Silence(5));
        var vm = Panel(capture);
        vm.TestDevice = vm.Devices[1];

        vm.StartTestCommand.Execute(null);
        await PumpAsync(200);

        Assert.Equal("mic-2", capture.OpenedDevice);
        vm.StopTestCommand.Execute(null);
    }

    /// <summary>
    /// A microphone left open behind a closed dialog is invisible, uncancellable, and holds the
    /// device the meeting is about to want — the same failure the model downloads had.
    /// </summary>
    [AvaloniaFact]
    public async Task ClosingTheDialogReleasesTheMicrophone()
    {
        var capture = new ScriptedCapture(Silence(5));
        var vm = Panel(capture);

        vm.StartTestCommand.Execute(null);
        await PumpAsync(200);
        Assert.True(vm.IsTesting);

        vm.CancelDownloads(); // what SettingsWindow.OnClosed calls
        await PumpAsync(200);

        Assert.False(vm.IsTesting);
        Assert.True(capture.Closed, "the capture stream was never closed.");
    }

    [AvaloniaFact]
    public void WithNoBackendThePanelSaysSoRatherThanLookingBroken()
    {
        var vm = new SettingsViewModel(new AppSettings(), () => null);

        vm.StartTestCommand.Execute(null);

        Assert.False(vm.IsTesting);
        Assert.Equal("No audio backend", vm.VerdictLabel);
    }

    /// <summary>Kanal has no noise processing of its own, and the panel has to say so.</summary>
    [AvaloniaFact]
    public void TheProcessingNoteIsHonestAboutWhatKanalDoes()
    {
        var vm = Panel(new ScriptedCapture([]));

        Assert.Contains("no noise suppression", vm.ProcessingNote);
        Assert.Contains("Windows sound settings", vm.ProcessingNote);
    }

    /// <summary>
    /// On macOS a denied microphone permission delivers exactly what a dead device delivers:
    /// zeros. "Check that Windows has not muted it" sends the operator to a settings page that
    /// does not exist; the one actionable cause on this platform has to be named.
    /// </summary>
    [AvaloniaFact]
    public async Task OnMacASilentDeviceNamesMicrophonePermission()
    {
        var vm = new SettingsViewModel(
            new AppSettings(), () => new ScriptedCapture(Silence(40)), isMacOs: true);

        vm.StartTestCommand.Execute(null);
        await PumpAsync(300);

        Assert.Equal("Nothing is arriving", vm.VerdictLabel);
        Assert.Contains("Privacy & Security", vm.VerdictDetail);
        Assert.DoesNotContain("Windows", vm.VerdictDetail);
        vm.StopTestCommand.Execute(null);
    }

    /// <summary>The advice must point at the sound settings this machine actually has.</summary>
    [AvaloniaFact]
    public void TheSoundSettingsWordingFollowsThePlatform()
    {
        var mac = new SettingsViewModel(new AppSettings(), () => null, isMacOs: true);
        var win = new SettingsViewModel(new AppSettings(), () => null, isMacOs: false);

        Assert.Contains("System Settings", mac.ProcessingNote);
        Assert.DoesNotContain("Windows", mac.ProcessingNote);
        Assert.Contains("Windows sound settings", win.ProcessingNote);
    }

    /// <summary>
    /// Stop, then immediately Test again: a frame the old capture already had in flight must
    /// land in the old session's meter, not condemn the new device's fresh one.
    /// </summary>
    [AvaloniaFact]
    public async Task AStoppedTestCannotContaminateTheNextOne()
    {
        var lingering = new LingeringCapture();
        var silent = new ScriptedCapture(Silence(20));
        var first = true;
        var vm = new SettingsViewModel(new AppSettings(), () =>
        {
            if (!first)
                return silent;
            first = false;
            return lingering;
        }, isMacOs: false);

        vm.StartTestCommand.Execute(null);
        await PumpAsync(100);
        vm.StopTestCommand.Execute(null);
        vm.StartTestCommand.Execute(null); // before the old loop has finished dying
        await PumpAsync(100);
        lingering.Release.TrySetResult(); // the old capture's queued frame arrives now
        await PumpAsync(200);

        Assert.Equal("Nothing is arriving", vm.VerdictLabel);
        vm.StopTestCommand.Execute(null);
    }
}
