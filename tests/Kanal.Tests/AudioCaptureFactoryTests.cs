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

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var bytes = 0;
        try
        {
            await foreach (var frame in capture.CaptureAsync(null, cts.Token))
            {
                Assert.Equal(0, frame.Length % 2);
                bytes += frame.Length;
            }
        }
        catch (OperationCanceledException)
        {
            // expected end of the timed capture
        }

        // 700 ms at 16 kHz mono PCM16 is 22 400 bytes; allow for startup latency and the tail.
        Assert.InRange(bytes, 3_200, 32_000);
    }
}
