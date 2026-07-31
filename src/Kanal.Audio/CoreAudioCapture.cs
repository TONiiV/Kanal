using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Kanal.Audio;

/// <summary>
/// macOS capture via AudioToolbox's AudioQueue. Unlike the WASAPI path this asks CoreAudio
/// for 16 kHz mono PCM16 up front and lets the framework do the rate conversion, so no
/// resampling or downmixing happens here.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class CoreAudioCapture : IAudioCaptureService
{
    public const int TargetRateHz = 16_000;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var devices = MacCoreAudio.GetInputDevices();
        var defaultUid = MacCoreAudio.GetDefaultInputUid();
        if (defaultUid is null)
            return devices;

        // The operator should not have to hunt for the device the system already chose.
        var index = devices.FindIndex(d => d.Id == defaultUid);
        if (index > 0)
        {
            var preferred = devices[index];
            devices.RemoveAt(index);
            devices.Insert(0, preferred);
        }

        return devices;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        string? deviceId, [EnumeratorCancellation] CancellationToken ct)
    {
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        // AudioQueue calls back on its own thread and holds this delegate for the life of the
        // queue; it must stay rooted until Close, hence the GC.KeepAlive below.
        MacCoreAudio.AudioQueueInputCallback callback = (_, queue, buffer, _, _, _) =>
        {
            try
            {
                var descriptor = Marshal.PtrToStructure<MacCoreAudio.AudioQueueBuffer>(buffer);
                if (descriptor.AudioDataByteSize > 0)
                {
                    var pcm = new byte[descriptor.AudioDataByteSize];
                    Marshal.Copy(descriptor.AudioData, pcm, 0, pcm.Length);
                    frames.Writer.TryWrite(pcm);
                }

                MacCoreAudio.AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                frames.Writer.TryComplete(ex);
            }
        };

        var handle = MacCoreAudio.OpenInputQueue(deviceId, callback);
        MacCoreAudio.Start(handle);
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct))
                yield return frame;
        }
        finally
        {
            MacCoreAudio.Close(handle);
            GC.KeepAlive(callback);
        }
    }
}
