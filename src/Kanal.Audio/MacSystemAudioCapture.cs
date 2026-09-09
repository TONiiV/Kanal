using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Kanal.Audio;

[SupportedOSPlatform("macos13.0")]
public sealed class MacSystemAudioCapture : ISystemAudioCaptureService
{
    // A leaked native session keeps the function pointers it was handed, and a marshalled delegate's
    // thunk dies with the delegate. So a session that outlives its stop leaks its callbacks too:
    // two objects against a call into freed memory from the audio thread.
    private static readonly List<object> Leaked = [];

    private readonly IMacSystemAudioNative _native;
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _devices;
    private readonly TimeSpan _stopTimeout;

    public MacSystemAudioCapture(SystemAudioBackend backend)
        : this(backend, MacSystemAudioNative.Instance, MacCoreAudio.GetOutputDevices)
    {
    }

    internal MacSystemAudioCapture(
        SystemAudioBackend backend,
        IMacSystemAudioNative native,
        Func<IReadOnlyList<AudioDeviceInfo>>? devices = null,
        TimeSpan? stopTimeout = null)
    {
        if (backend is not (SystemAudioBackend.CoreAudioProcessTap or SystemAudioBackend.ScreenCaptureKit))
            throw new ArgumentOutOfRangeException(nameof(backend));
        Backend = backend;
        _native = native;
        _devices = devices ?? MacCoreAudio.GetOutputDevices;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
    }

    public SystemAudioBackend Backend { get; }

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var devices = _devices();
        if (Backend == SystemAudioBackend.CoreAudioProcessTap)
            return devices;

        // ScreenCaptureKit captures the current system mix, not an arbitrary HAL endpoint.
        return devices.Count == 0 ? devices : [devices[0]];
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        string? deviceId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (deviceId is not null && !GetDevices().Any(device => device.Id == deviceId))
            throw new InvalidOperationException(
                $"Computer output '{deviceId}' is no longer available; choose an active output before starting.");

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
            var permissionAdvice = IsPermissionFailure(detail)
                ? " Check System Settings > Privacy & Security > Screen & System Audio Recording, then restart Kanal."
                : " Re-select an active computer output and try again.";
            frames.Writer.TryComplete(new InvalidOperationException(
                $"Computer audio capture stopped: {detail}.{permissionAdvice}"));
        };

        var handle = _native.Start(Backend, deviceId, onFrame, onError);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Computer audio capture could not allocate its native session.");

        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct))
                yield return frame;
        }
        finally
        {
            // A native session that never calls its stop completion would otherwise hang the
            // operator's Stop; the leak is preferable to a frozen host.
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
        string? outputDeviceUid,
        MacSystemAudioFrameCallback onFrame,
        MacSystemAudioErrorCallback onError);

    ValueTask StopAsync(IntPtr handle);
}

internal sealed class MacSystemAudioNative : IMacSystemAudioNative
{
    internal static readonly MacSystemAudioNative Instance = new();

    public IntPtr Start(
        SystemAudioBackend backend,
        string? outputDeviceUid,
        MacSystemAudioFrameCallback onFrame,
        MacSystemAudioErrorCallback onError) =>
        NativeStart((int)backend, outputDeviceUid, onFrame, onError, IntPtr.Zero);

    public async ValueTask StopAsync(IntPtr handle)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MacSystemAudioStopCallback callback = _ => stopped.TrySetResult();
        NativeStop(handle, callback, IntPtr.Zero);
        await stopped.Task.ConfigureAwait(false);
        GC.KeepAlive(callback);
    }

    [DllImport("kanal_audio_native", EntryPoint = "kanal_system_audio_start", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NativeStart(
        int backend,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputDeviceUid,
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
