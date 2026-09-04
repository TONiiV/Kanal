using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Kanal.Audio;

[SupportedOSPlatform("macos13.0")]
public sealed class MacSystemAudioCapture : ISystemAudioCaptureService
{
    private readonly IMacSystemAudioNative _native;
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _devices;

    public MacSystemAudioCapture(SystemAudioBackend backend)
        : this(backend, MacSystemAudioNative.Instance, MacCoreAudio.GetOutputDevices)
    {
    }

    internal MacSystemAudioCapture(
        SystemAudioBackend backend,
        IMacSystemAudioNative native,
        Func<IReadOnlyList<AudioDeviceInfo>>? devices = null)
    {
        if (backend is not (SystemAudioBackend.CoreAudioProcessTap or SystemAudioBackend.ScreenCaptureKit))
            throw new ArgumentOutOfRangeException(nameof(backend));
        Backend = backend;
        _native = native;
        _devices = devices ?? MacCoreAudio.GetOutputDevices;
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

                resampler ??= new LinearResampler(sampleRate, AudioCaptureFormat.SampleRateHz);
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
            frames.Writer.TryComplete(new InvalidOperationException(
                $"Computer audio capture could not start: {detail} " +
                "Check System Settings > Privacy & Security > Screen & System Audio Recording, then restart Kanal."));
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
            _native.Stop(handle);
            GC.KeepAlive(onFrame);
            GC.KeepAlive(onError);
        }
    }
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

    void Stop(IntPtr handle);
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

    public void Stop(IntPtr handle) => NativeStop(handle);

    [DllImport("kanal_audio_native", EntryPoint = "kanal_system_audio_start", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NativeStart(
        int backend,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? outputDeviceUid,
        MacSystemAudioFrameCallback onFrame,
        MacSystemAudioErrorCallback onError,
        IntPtr context);

    [DllImport("kanal_audio_native", EntryPoint = "kanal_system_audio_stop", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeStop(IntPtr handle);
}
