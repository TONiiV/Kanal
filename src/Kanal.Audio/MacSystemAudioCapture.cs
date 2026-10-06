using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Kanal.Audio;

[SupportedOSPlatform("macos13.0")]
public sealed class MacSystemAudioCapture : ISystemAudioCaptureService
{
    // A session that outlives its stop keeps calling these thunks; leaking them beats a call into freed memory.
    private static readonly List<object> Leaked = [];

    private readonly IMacSystemAudioNative _native;
    private readonly TimeSpan _stopTimeout;

    public MacSystemAudioCapture(SystemAudioBackend backend)
        : this(backend, MacSystemAudioNative.Instance)
    {
    }

    internal MacSystemAudioCapture(
        SystemAudioBackend backend,
        IMacSystemAudioNative native,
        TimeSpan? stopTimeout = null)
    {
        if (backend is not (SystemAudioBackend.CoreAudioProcessTap or SystemAudioBackend.ScreenCaptureKit))
            throw new ArgumentOutOfRangeException(nameof(backend));
        Backend = backend;
        _native = native;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
    }

    public SystemAudioBackend Backend { get; }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        LinearResampler? resampler = null;
        var resamplerInputRate = 0;

        MacSystemAudioFrameCallback onFrame = (data, byteCount, sampleRate, _) =>
        {
            try
            {
                if (data == IntPtr.Zero || byteCount <= 0)
                    return;
                var bytes = new byte[byteCount];
                Marshal.Copy(data, bytes, 0, byteCount);
                if (sampleRate == AudioCaptureFormat.SampleRateHz)
                {
                    frames.Writer.TryWrite(bytes);
                    return;
                }

                if (resampler is null || resamplerInputRate != sampleRate)
                {
                    resampler = new LinearResampler(sampleRate, AudioCaptureFormat.SampleRateHz);
                    resamplerInputRate = sampleRate;
                }

                var input = PcmConvert.BytesToShorts(bytes);
                var output = new short[resampler.GetMaxOutputCount(input.Length)];
                var count = resampler.Resample(input, output);
                if (count > 0)
                    frames.Writer.TryWrite(PcmConvert.ShortsToBytes(output[..count]));
            }
            catch (Exception ex)
            {
                frames.Writer.TryComplete(ex);
            }
        };
        MacSystemAudioErrorCallback onError = (message, _) =>
        {
            var detail = Marshal.PtrToStringUTF8(message) ?? "unknown native error";
            var denied = IsPermissionFailure(detail);
            var advice = denied
                ? " Check System Settings > Privacy & Security > Screen & System Audio Recording, then restart Kanal."
                : " Start again; if it keeps failing, restart Kanal.";
            frames.Writer.TryComplete(new AudioCaptureException(
                denied ? AudioCaptureFault.PermissionDenied : AudioCaptureFault.SourceFailed,
                $"Computer audio capture stopped: {detail}.{advice}"));
        };

        var handle = _native.Start(Backend, onFrame, onError);
        if (handle == IntPtr.Zero)
            throw new AudioCaptureException(AudioCaptureFault.SourceFailed,
                "Computer audio capture could not allocate its native session.");

        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct))
                yield return frame;
        }
        finally
        {
            try
            {
                await _native.StopAsync(handle).AsTask().WaitAsync(_stopTimeout);
            }
            catch (TimeoutException)
            {
                lock (Leaked)
                {
                    Leaked.Add(onFrame);
                    Leaked.Add(onError);
                }
            }

            GC.KeepAlive(onFrame);
            GC.KeepAlive(onError);
        }
    }

    internal static int LeakedCallbackCount
    {
        get { lock (Leaked) { return Leaked.Count; } }
    }

    private static bool IsPermissionFailure(string detail) =>
        detail.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("declined", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("-3801", StringComparison.Ordinal);
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MacSystemAudioFrameCallback(IntPtr data, int byteCount, int sampleRate, IntPtr context);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MacSystemAudioErrorCallback(IntPtr message, IntPtr context);

internal interface IMacSystemAudioNative
{
    IntPtr Start(
        SystemAudioBackend backend,
        MacSystemAudioFrameCallback onFrame,
        MacSystemAudioErrorCallback onError);

    ValueTask StopAsync(IntPtr handle);
}

internal sealed class MacSystemAudioNative : IMacSystemAudioNative
{
    internal static readonly MacSystemAudioNative Instance = new();

    public IntPtr Start(
        SystemAudioBackend backend,
        MacSystemAudioFrameCallback onFrame,
        MacSystemAudioErrorCallback onError) =>
        NativeStart((int)backend, onFrame, onError, IntPtr.Zero);

    public ValueTask StopAsync(IntPtr handle) =>
        WaitForStopAsync(callback => NativeStop(handle, callback, IntPtr.Zero));

    // The handle is the delegate's only root: native code may complete after the caller timed out.
    internal static async ValueTask WaitForStopAsync(Action<MacSystemAudioStopCallback> stop)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = IntPtr.Zero;
        MacSystemAudioStopCallback callback = _ =>
        {
            stopped.TrySetResult();
            Release(ref root);
        };
        root = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
        try
        {
            stop(callback);
        }
        catch
        {
            Release(ref root);
            throw;
        }

        await stopped.Task.ConfigureAwait(false);
    }

    private static void Release(ref IntPtr root)
    {
        var handle = Interlocked.Exchange(ref root, IntPtr.Zero);
        if (handle != IntPtr.Zero)
            GCHandle.FromIntPtr(handle).Free();
    }

    [DllImport("kanal_audio_native", EntryPoint = "kanal_system_audio_start", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NativeStart(
        int backend,
        MacSystemAudioFrameCallback onFrame,
        MacSystemAudioErrorCallback onError,
        IntPtr context);

    [DllImport("kanal_audio_native", EntryPoint = "kanal_system_audio_stop_with_completion", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeStop(
        IntPtr handle,
        MacSystemAudioStopCallback onStopped,
        IntPtr context);
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MacSystemAudioStopCallback(IntPtr context);
