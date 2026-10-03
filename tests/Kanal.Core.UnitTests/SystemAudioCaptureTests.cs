using Kanal.Audio;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kanal.Core.UnitTests;

public class SystemAudioCaptureTests
{
    [Fact]
    public void LateNativeStopCompletionRemainsRootedAfterTheCallerTimesOut()
    {
        var (callback, wait) = AbandonStopWait();
        Collect();

        Assert.True(callback.IsAlive, "the stop completion was collected while native code still held it");
        Assert.True(InvokeAndWait(callback, wait, times: 1));

        Collect();
        Assert.False(callback.IsAlive, "a completed stop left its completion rooted");
    }

    [Fact]
    public void ARepeatedNativeStopCompletionDoesNotFreeTheRootTwice()
    {
        var (callback, wait) = AbandonStopWait();
        Collect();

        Assert.True(InvokeAndWait(callback, wait, times: 2));
        Collect();
        Assert.False(callback.IsAlive);
    }

    [Fact]
    public async Task AThrowingNativeStopReleasesItsCompletion()
    {
        var handed = await ThrowingStop(invokeFirst: false);
        Collect();
        Assert.False(handed.IsAlive, "a stop that threw left its completion rooted");
    }

    [Fact]
    public async Task ANativeStopThatCompletesThenThrowsDoesNotFreeTheRootTwice()
    {
        var handed = await ThrowingStop(invokeFirst: true);
        Collect();
        Assert.False(handed.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Callback, WeakReference Wait) AbandonStopWait()
    {
        MacSystemAudioStopCallback? handed = null;
        var wait = MacSystemAudioNative.WaitForStopAsync(callback => handed = callback).AsTask();
        Assert.False(wait.IsCompleted);
        return (new WeakReference(handed), new WeakReference(wait));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool InvokeAndWait(WeakReference callback, WeakReference wait, int times)
    {
        var completion = Assert.IsType<MacSystemAudioStopCallback>(callback.Target);
        var pending = Assert.IsAssignableFrom<Task>(wait.Target);
        for (var i = 0; i < times; i++)
            completion(IntPtr.Zero);
        return pending.Wait(TimeSpan.FromSeconds(2));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ThrowingStop(bool invokeFirst)
    {
        WeakReference? handed = null;
        var error = await Assert.ThrowsAsync<IOException>(() => MacSystemAudioNative.WaitForStopAsync(callback =>
        {
            handed = new WeakReference(callback);
            if (invokeFirst)
                callback(IntPtr.Zero);
            throw new IOException("native stop refused");
        }).AsTask());
        Assert.Equal("native stop refused", error.Message);
        return handed!;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

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

        await using (var frames = capture.CaptureAsync("stable-output-uid", cts.Token).GetAsyncEnumerator(cts.Token))
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

        var error = await Assert.ThrowsAsync<AudioCaptureException>(async () =>
        {
            await foreach (var _ in capture.CaptureAsync(null, TestContext.Current.CancellationToken))
                break;
        });

        Assert.Equal(AudioCaptureFault.PermissionDenied, error.Fault);
        Assert.Contains("permission denied", error.Message, StringComparison.Ordinal);
        Assert.Contains("Privacy & Security", error.Message, StringComparison.Ordinal);
        Assert.True(native.Stopped);
    }

    [Fact]
    [SupportedOSPlatform("macos13.0")]
    public async Task MacBridgeReportsAnyOtherNativeFailureAsASourceFailure()
    {
        var native = new FakeMacNative("tap creation failed (OSStatus 560947818)");
        var capture = new MacSystemAudioCapture(SystemAudioBackend.CoreAudioProcessTap, native);

        var error = await Assert.ThrowsAsync<AudioCaptureException>(async () =>
        {
            await foreach (var _ in capture.CaptureAsync(null, TestContext.Current.CancellationToken))
                break;
        });

        Assert.Equal(AudioCaptureFault.SourceFailed, error.Fault);
        Assert.Contains("Re-select an active computer output", error.Message, StringComparison.Ordinal);
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

        var error = await Assert.ThrowsAsync<AudioCaptureException>(async () =>
        {
            await foreach (var _ in capture.CaptureAsync("unplugged", TestContext.Current.CancellationToken))
                break;
        });

        Assert.Equal(AudioCaptureFault.DeviceUnavailable, error.Fault);
        Assert.Contains("no longer available", error.Message, StringComparison.Ordinal);
        Assert.Contains(AudioDeviceIds.Hash("unplugged"), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unplugged", error.Message, StringComparison.Ordinal);
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

    [Fact]
    [SupportedOSPlatform("macos13.0")]
    public async Task MacBridgeGivesUpOnANativeSessionThatNeverReportsTeardown()
    {
        var native = new FakeMacNative(stopHangs: true);
        var capture = new MacSystemAudioCapture(
            SystemAudioBackend.CoreAudioProcessTap,
            native,
            stopTimeout: TimeSpan.FromMilliseconds(50));

        var frames = capture.CaptureAsync(null, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await frames.MoveNextAsync());

        await frames.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
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
        // Plist contents are covered by InstallerLayoutTests; this asserts a dev build produced a bundle.
        if (!OperatingSystem.IsMacOS())
            return;

        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = output.Parent!.Name;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var contents = Path.Combine(
            root, $"src/Kanal.Host/bin/{configuration}/{output.Name}/Kanal.app/Contents");
        var plistPath = Path.Combine(contents, "Info.plist");

        Assert.True(
            File.Exists(plistPath),
            $"no bundle at {plistPath} — build Kanal.slnx, not this project alone");

        var plist = File.ReadAllText(plistPath);
        Assert.Contains("NSAudioCaptureUsageDescription", plist, StringComparison.Ordinal);
        Assert.Contains("NSMicrophoneUsageDescription", plist, StringComparison.Ordinal);
        Assert.DoesNotContain("__VERSION__", plist, StringComparison.Ordinal);

        var macOs = Path.Combine(contents, "MacOS");
        Assert.True(File.Exists(Path.Combine(macOs, "Kanal.Host")));
        Assert.True(File.Exists(Path.Combine(macOs, "libkanal_audio_native.dylib")));
        Assert.True(
            Directory.Exists(Path.Combine(macOs, "runtimes")),
            "a bundle without the native runtime assets cannot launch");
    }

    [Fact]
    [SupportedOSPlatform("macos13.0")]
    public async Task MacBridgeKeepsTheCallbacksOfALeakedSessionReachable()
    {
        var native = new FakeMacNative(stopHangs: true);
        var capture = new MacSystemAudioCapture(
            SystemAudioBackend.CoreAudioProcessTap,
            native,
            () => [new AudioDeviceInfo("out", "Output")],
            TimeSpan.FromMilliseconds(50));

        await using (var frames = capture
            .CaptureAsync(null, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await frames.MoveNextAsync());
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(native.FrameCallback!.IsAlive, "the frame callback of a leaked session was collected");
        Assert.True(native.ErrorCallback!.IsAlive, "the error callback of a leaked session was collected");
    }

    [Fact]
    [SupportedOSPlatform("macos13.0")]
    public async Task MacBridgeDoesNotRootTheCallbacksOfASessionThatStoppedCleanly()
    {
        var native = new FakeMacNative();
        var capture = new MacSystemAudioCapture(
            SystemAudioBackend.CoreAudioProcessTap,
            native,
            () => [new AudioDeviceInfo("out", "Output")],
            TimeSpan.FromSeconds(5));

        var before = MacSystemAudioCapture.LeakedCallbackCount;

        await using (var frames = capture
            .CaptureAsync(null, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await frames.MoveNextAsync());
        }

        Assert.True(native.Stopped);
        Assert.Equal(before, MacSystemAudioCapture.LeakedCallbackCount);
    }

    private sealed class FakeMacNative(
        string? failure = null,
        int[]? sampleRates = null,
        bool stopHangs = false) : IMacSystemAudioNative
    {
        public string? DeviceUid { get; private set; }
        public bool Stopped { get; private set; }
        public int StartCount { get; private set; }

        // Weak, so the test observes whether the bridge roots them rather than rooting them itself.
        public WeakReference? FrameCallback { get; private set; }
        public WeakReference? ErrorCallback { get; private set; }

        public IntPtr Start(
            SystemAudioBackend backend,
            string? outputDeviceUid,
            MacSystemAudioFrameCallback onFrame,
            MacSystemAudioErrorCallback onError)
        {
            StartCount++;
            DeviceUid = outputDeviceUid;
            FrameCallback = new WeakReference(onFrame);
            ErrorCallback = new WeakReference(onError);
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
