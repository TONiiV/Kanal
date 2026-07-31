using Kanal.Audio;

namespace Kanal.Tests;

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
        // 700 ms measurement window opens at the FIRST frame, not at StartCapture.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
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
                    cts.CancelAfter(TimeSpan.FromSeconds(2));
                }
                bytes += frame.Length;
                if (sw.ElapsedMilliseconds >= 700)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // device never produced a frame within the overall timeout
        }

        // ~700 ms at 16 kHz mono PCM16 is 22 400 bytes plus at most one trailing buffer.
        // The upper bound must stay below 2× rate (44 800) so an un-downmixed stereo or
        // un-resampled 32 kHz stream still fails.
        Assert.InRange(bytes, 8_000, 32_000);
    }
}
