using Kanal.Audio;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kanal.Core.UnitTests;

public class SystemAudioCaptureTests
{
    [Theory]
    [InlineData(SystemAudioPlatform.Windows, 10, 0, SystemAudioBackend.WasapiLoopback)]
    [InlineData(SystemAudioPlatform.MacOS, 14, 2, SystemAudioBackend.CoreAudioProcessTap)]
    [InlineData(SystemAudioPlatform.MacOS, 14, 1, SystemAudioBackend.ScreenCaptureKit)]
    [InlineData(SystemAudioPlatform.MacOS, 13, 0, SystemAudioBackend.ScreenCaptureKit)]
    [InlineData(SystemAudioPlatform.MacOS, 12, 6, SystemAudioBackend.Unavailable)]
    [InlineData(SystemAudioPlatform.Other, 0, 0, SystemAudioBackend.Unavailable)]
    public void SelectsTheDocumentedNativeBackend(
        SystemAudioPlatform platform,
        int major,
        int minor,
        SystemAudioBackend expected)
    {
        var support = SystemAudioCaptureFactory.DescribeSupport(platform, new Version(major, minor));

        Assert.Equal(expected, support.Backend);
        Assert.Equal(expected != SystemAudioBackend.Unavailable, support.IsAvailable);
        Assert.Equal(expected == SystemAudioBackend.Unavailable, support.Reason is not null);
    }

    [Fact]
    public void ResolvesTheSystemAudioBackendForThisPlatform()
    {
        var capture = SystemAudioCaptureFactory.TryCreate();
        var support = SystemAudioCaptureFactory.Support;

        Assert.Equal(support.IsAvailable, capture is not null);
        if (OperatingSystem.IsWindows())
            Assert.IsType<WasapiLoopbackAudioCapture>(capture);
        else if (OperatingSystem.IsMacOSVersionAtLeast(14, 2))
            Assert.Equal(SystemAudioBackend.CoreAudioProcessTap, capture?.Backend);
        else if (OperatingSystem.IsMacOSVersionAtLeast(13))
            Assert.Equal(SystemAudioBackend.ScreenCaptureKit, capture?.Backend);
        else
            Assert.Null(capture);
    }

    [Fact]
    public void EnumeratesOnlyStableNamedOutputEndpoints()
    {
        var capture = SystemAudioCaptureFactory.TryCreate();
        if (capture is null)
            return;

        foreach (var output in capture.GetDevices())
        {
            Assert.False(string.IsNullOrWhiteSpace(output.Id));
            Assert.False(string.IsNullOrWhiteSpace(output.Name));
        }
    }

    [Fact]
    public void UnsupportedMacExplainsTheMinimumVersion()
    {
        var support = SystemAudioCaptureFactory.DescribeSupport(
            SystemAudioPlatform.MacOS,
            new Version(12, 6));

        Assert.Contains("macOS 13", support.Reason, StringComparison.Ordinal);
    }

    [Fact]
    [SupportedOSPlatform("macos13.0")]
    public async Task MacBridgeNormalizesFramesAndAlwaysStopsTheNativeSession()
    {
        var native = new FakeMacNative();
        var capture = new MacSystemAudioCapture(
            SystemAudioBackend.CoreAudioProcessTap,
            native,
            () => [new("stable-output-uid", "Speakers")]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await using (var frames = capture.CaptureAsync("stable-output-uid", cts.Token).GetAsyncEnumerator())
        {
            Assert.True(await frames.MoveNextAsync());
            Assert.Equal(0, frames.Current.Length % 2);
            Assert.InRange(frames.Current.Length, 50, 90); // 100 samples at 48 kHz -> about 33 at 16 kHz.
        }

        Assert.Equal("stable-output-uid", native.DeviceUid);
        Assert.True(native.Stopped);
    }

    [Fact]
    [SupportedOSPlatform("macos13.0")]
    public async Task MacBridgeTurnsNativeFailureIntoActionablePermissionAdvice()
    {
        var native = new FakeMacNative("permission denied");
        var capture = new MacSystemAudioCapture(SystemAudioBackend.ScreenCaptureKit, native);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in capture.CaptureAsync(null, TestContext.Current.CancellationToken))
                break;
        });

        Assert.Contains("permission denied", error.Message, StringComparison.Ordinal);
        Assert.Contains("Privacy & Security", error.Message, StringComparison.Ordinal);
        Assert.True(native.Stopped);
    }

    [Fact]
    [SupportedOSPlatform("macos13.0")]
    public async Task MacBridgeRejectsAStaleOutputBeforeOpeningNativeCapture()
    {
        var native = new FakeMacNative();
        var capture = new MacSystemAudioCapture(
            SystemAudioBackend.CoreAudioProcessTap,
            native,
            () => [new("current", "Current speakers")]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in capture.CaptureAsync("unplugged", TestContext.Current.CancellationToken))
                break;
        });

        Assert.Contains("no longer available", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, native.StartCount);
    }

    [Fact]
    [SupportedOSPlatform("macos13.0")]
    public async Task MacBridgeResamplesEachFrameAtTheRateItArrivedWith()
    {
        var native = new FakeMacNative(sampleRates: [48_000, 24_000]);
        var capture = new MacSystemAudioCapture(SystemAudioBackend.ScreenCaptureKit, native);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var samples = new List<int>();
        await foreach (var frame in capture.CaptureAsync(null, cts.Token))
        {
            samples.Add(frame.Length / sizeof(short));
            if (samples.Count == 2)
                break;
        }

        Assert.InRange(samples[0], 30, 36); // 100 samples at 48 kHz
        Assert.InRange(samples[1], 62, 70); // the same 100 samples at 24 kHz
    }

    /// <summary>A native session that never reports teardown must not freeze the operator's Stop.</summary>
    [Fact]
    [SupportedOSPlatform("macos13.0")]
    public async Task MacBridgeGivesUpOnANativeSessionThatNeverReportsTeardown()
    {
        var native = new FakeMacNative(stopHangs: true);
        var capture = new MacSystemAudioCapture(
            SystemAudioBackend.CoreAudioProcessTap,
            native,
            stopTimeout: TimeSpan.FromMilliseconds(50));

        var frames = capture.CaptureAsync(null, TestContext.Current.CancellationToken).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());

        await frames.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void MacBuildCarriesBothNativeCEntryPoints()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var path = Path.Combine(AppContext.BaseDirectory, "libkanal_audio_native.dylib");
        var library = NativeLibrary.Load(path);
        try
        {
            Assert.NotEqual(IntPtr.Zero, NativeLibrary.GetExport(library, "kanal_system_audio_start"));
            Assert.NotEqual(IntPtr.Zero, NativeLibrary.GetExport(library, "kanal_system_audio_stop_with_completion"));
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [Fact]
    public void BuiltMacAppCarriesBothAudioPermissionPurposes()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = output.Parent!.Name;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var plistPath = OperatingSystem.IsMacOS()
            ? Path.Combine(root, $"src/Kanal.Host/bin/{configuration}/{output.Name}/Kanal.app/Contents/Info.plist")
            : Path.Combine(root, "src/Kanal.Host/Info.plist");
        var plist = File.ReadAllText(plistPath);

        Assert.Contains("NSAudioCaptureUsageDescription", plist, StringComparison.Ordinal);
        Assert.Contains("NSMicrophoneUsageDescription", plist, StringComparison.Ordinal);

        if (OperatingSystem.IsMacOS())
        {
            var macOs = Path.Combine(Path.GetDirectoryName(plistPath)!, "MacOS");
            Assert.True(
                File.Exists(Path.Combine(macOs, "Kanal.Host")),
                $"no host executable beside {plistPath} — build Kanal.slnx, not this project alone");
            Assert.True(File.Exists(Path.Combine(macOs, "libkanal_audio_native.dylib")));
            Assert.True(
                Directory.Exists(Path.Combine(macOs, "runtimes")),
                "a bundle without the native runtime assets cannot launch");
        }
    }

    private sealed class FakeMacNative(
        string? failure = null,
        int[]? sampleRates = null,
        bool stopHangs = false) : IMacSystemAudioNative
    {
        public string? DeviceUid { get; private set; }
        public bool Stopped { get; private set; }
        public int StartCount { get; private set; }

        public IntPtr Start(
            SystemAudioBackend backend,
            string? outputDeviceUid,
            MacSystemAudioFrameCallback onFrame,
            MacSystemAudioErrorCallback onError)
        {
            StartCount++;
            DeviceUid = outputDeviceUid;
            if (failure is not null)
            {
                var message = Marshal.StringToCoTaskMemUTF8(failure);
                try
                {
                    onError(message, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(message);
                }
            }
            else
            {
                var samples = Enumerable.Range(0, 100).Select(i => (short)(i * 100)).ToArray();
                var bytes = samples.Length * sizeof(short);
                var data = Marshal.AllocHGlobal(bytes);
                try
                {
                    Marshal.Copy(samples, 0, data, samples.Length);
                    foreach (var rate in sampleRates ?? [48_000])
                        onFrame(data, bytes, rate, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeHGlobal(data);
                }
            }

            return new IntPtr(42);
        }

        public ValueTask StopAsync(IntPtr handle)
        {
            Assert.Equal(new IntPtr(42), handle);
            Stopped = true;
            return stopHangs
                ? new ValueTask(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task)
                : ValueTask.CompletedTask;
        }
    }
}
