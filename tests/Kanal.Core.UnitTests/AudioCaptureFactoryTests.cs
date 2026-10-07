using Kanal.Audio;

namespace Kanal.Core.UnitTests;

public class AudioCaptureFactoryTests
{
    [Fact]
    public void ResolvesTheBackendForThisPlatform()
    {
        var capture = AudioCaptureFactory.TryCreate();

        if (OperatingSystem.IsWindows())
            Assert.IsType<WasapiAudioCapture>(capture);
        else if (OperatingSystem.IsMacOS())
            Assert.IsType<CoreAudioCapture>(capture);
        else
            Assert.Null(capture);

        Assert.Equal(capture is not null, AudioCaptureFactory.IsSupported);
    }

    [Fact]
    public void EnumeratesInputDevicesWithStableIdentifiers()
    {
        if (!AudioCaptureFactory.IsSupported)
            return;

        foreach (var device in AudioCaptureFactory.Create().GetDevices())
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
        }
    }

    /// <summary>
    /// A stale saved device (headset unplugged between meetings) must surface as an error.
    /// Silently falling back to the built-in mic would capture the wrong room.
    /// </summary>
    [Fact]
    public async Task RejectsAnUnknownDeviceInsteadOfFallingBack()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var capture = new CoreAudioCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Called inline rather than through Assert.ThrowsAsync: the platform analyzer does not
        // flow the OperatingSystem guard above into a lambda.
        try
        {
            await foreach (var _ in capture.CaptureAsync("kanal-no-such-device-uid", cts.Token))
                break;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Assert.Fail("capture accepted an unknown device instead of reporting it");
    }

    /// <summary>Frames arrive as 16 kHz mono PCM16, whatever rate the hardware runs at.</summary>
    [Fact]
    public async Task DeliversSixteenKilohertzMonoFrames()
    {
        if (!AudioCaptureFactory.IsSupported)
            return;

        var capture = AudioCaptureFactory.Create();
        if (capture.GetDevices().Count == 0)
            return;

        // Device start can take well over a second on some Windows drivers, so the
        // measurement window opens at the FIRST frame, not at StartCapture.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var sw = new System.Diagnostics.Stopwatch();
        var bytes = 0;
        try
        {
            await foreach (var frame in capture.CaptureAsync(null, cts.Token))
            {
                Assert.Equal(0, frame.Length % 2);
                if (!sw.IsRunning)
                {
                    sw.Start();
                    // full measurement window even when device start ate most of the 6 s
                    cts.CancelAfter(TimeSpan.FromSeconds(4));
                }
                bytes += frame.Length;
                if (sw.ElapsedMilliseconds >= 2000)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // device never produced a frame within the overall timeout
        }

        // 2 s of 16 kHz mono PCM16 is 64 000 bytes; buffers queued during device start add ~13 000.
        // The upper bound stays below 2x (128 000), so stereo or un-resampled 32 kHz still fails.
        Assert.InRange(bytes, 48_000, 80_000);
    }

    /// <summary>
    /// Registering and removing the real platform listener must neither throw nor corrupt the
    /// process. Firing the native callback remains a manual plug/unplug test.
    /// </summary>
    [Fact]
    public void DeviceWatcherRegistersAndUnregistersCleanly()
    {
        // Null is legitimate on a platform without an audio HAL; throwing is not.
        using var watcher = AudioCaptureFactory.TryCreateDeviceWatcher();
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
            Assert.NotNull(watcher);
    }
}
