using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Kanal.Audio;

/// <summary>
/// P/Invoke surface for the two macOS frameworks we need: CoreAudio's HAL for device
/// enumeration and AudioToolbox's AudioQueue for capture. Deliberately no third-party
/// native dependency — PRD §07 listed PortAudio / OpenAL / SoundFlow as the fallbacks,
/// none of which turned out to be necessary.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacCoreAudio
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint SystemObject = 1;
    private const uint EncodingUtf8 = 0x08000100;

    private const uint FlagSignedInteger = 4;
    private const uint FlagPacked = 8;

    // 100 ms per buffer at 16 kHz mono PCM16. Small enough for the latency budget,
    // large enough that the callback is not the bottleneck.
    private const uint BufferBytes = 3_200;
    private const int BufferCount = 3;

    private static uint FourCC(string tag) =>
        ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    // Selectors. 'aqcd' is kAudioQueueProperty_CurrentDevice — note it is *not* 'aqdv',
    // which silently yields kAudioQueueErr_InvalidProperty and captures the default device.
    private static uint SelectorDevices => FourCC("dev#");
    private static uint SelectorDeviceUid => FourCC("uid ");
    private static uint SelectorName => FourCC("lnam");
    private static uint SelectorStreamConfiguration => FourCC("slay");
    private static uint SelectorDefaultInput => FourCC("dIn ");
    private static uint PropertyCurrentDevice => FourCC("aqcd");
    private static uint ScopeGlobal => FourCC("glob");
    private static uint ScopeInput => FourCC("inpt");

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public int PacketDescriptionCount;
    }

    internal delegate void AudioQueueInputCallback(
        IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint packets, IntPtr packetDescriptions);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyDataSize(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint size);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint size, IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte[] cString, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, long bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr reference);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewInput(
        ref StreamDescription format, AudioQueueInputCallback callback, IntPtr userData,
        IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bytes, out IntPtr buffer);

    [DllImport(AudioToolbox)]
    internal static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packets, IntPtr descriptions);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStop(IntPtr queue, bool immediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(IntPtr queue, bool immediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueSetProperty(IntPtr queue, uint propertyId, ref IntPtr data, uint size);

    /// <summary>Input-capable devices, identified by their persistent UID.</summary>
    internal static List<AudioDeviceInfo> GetInputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        var address = new PropertyAddress { Selector = SelectorDevices, Scope = ScopeGlobal, Element = 0 };
        if (AudioObjectGetPropertyDataSize(SystemObject, ref address, 0, IntPtr.Zero, out var size) != 0 || size == 0)
            return devices;

        var block = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref size, block) != 0)
                return devices;

            for (var i = 0; i < size / sizeof(uint); i++)
            {
                var id = (uint)Marshal.ReadInt32(block, i * sizeof(uint));
                if (InputChannelCount(id) == 0)
                    continue;

                var uid = GetStringProperty(id, SelectorDeviceUid);
                if (string.IsNullOrEmpty(uid))
                    continue;

                var name = GetStringProperty(id, SelectorName);
                devices.Add(new AudioDeviceInfo(uid, string.IsNullOrEmpty(name) ? uid : name));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }

        return devices;
    }

    /// <summary>UID of the system default input, or null if there is none.</summary>
    internal static string? GetDefaultInputUid()
    {
        var address = new PropertyAddress { Selector = SelectorDefaultInput, Scope = ScopeGlobal, Element = 0 };
        var size = (uint)sizeof(uint);
        var block = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref size, block) != 0)
                return null;
            var uid = GetStringProperty((uint)Marshal.ReadInt32(block), SelectorDeviceUid);
            return string.IsNullOrEmpty(uid) ? null : uid;
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>
    /// Open a running-ready input queue delivering 16 kHz mono PCM16. AudioQueue performs the
    /// rate conversion from whatever the device runs at, so <see cref="LinearResampler"/> is
    /// not needed on this platform.
    /// </summary>
    internal static IntPtr OpenInputQueue(string? deviceUid, AudioQueueInputCallback callback)
    {
        var format = new StreamDescription
        {
            SampleRate = CoreAudioCapture.TargetRateHz,
            FormatId = FourCC("lpcm"),
            FormatFlags = FlagSignedInteger | FlagPacked,
            BytesPerPacket = 2,
            FramesPerPacket = 1,
            BytesPerFrame = 2,
            ChannelsPerFrame = 1,
            BitsPerChannel = 16,
        };

        var status = AudioQueueNewInput(ref format, callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var queue);
        if (status != 0)
            throw new InvalidOperationException($"AudioQueueNewInput failed: {Describe(status)}");

        if (deviceUid is not null)
            BindToDevice(queue, deviceUid);

        for (var i = 0; i < BufferCount; i++)
        {
            var allocated = AudioQueueAllocateBuffer(queue, BufferBytes, out var buffer);
            if (allocated != 0)
            {
                AudioQueueDispose(queue, true);
                throw new InvalidOperationException($"AudioQueueAllocateBuffer failed: {Describe(allocated)}");
            }

            AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
        }

        return queue;
    }

    internal static void Start(IntPtr queue)
    {
        var status = AudioQueueStart(queue, IntPtr.Zero);
        if (status != 0)
        {
            AudioQueueDispose(queue, true);
            throw new InvalidOperationException(
                $"AudioQueueStart failed: {Describe(status)}. If the app is bundled, check that " +
                "Info.plist declares NSMicrophoneUsageDescription.");
        }
    }

    internal static void Close(IntPtr queue)
    {
        if (queue == IntPtr.Zero)
            return;
        AudioQueueStop(queue, true);
        AudioQueueDispose(queue, true);
    }

    private static void BindToDevice(IntPtr queue, string deviceUid)
    {
        var reference = CFStringCreateWithCString(IntPtr.Zero, Encoding.UTF8.GetBytes(deviceUid + "\0"), EncodingUtf8);
        try
        {
            var status = AudioQueueSetProperty(queue, PropertyCurrentDevice, ref reference, (uint)IntPtr.Size);
            if (status == 0)
                return;

            AudioQueueDispose(queue, true);
            // Falling back to the default device would silently capture the wrong room.
            throw new InvalidOperationException($"Capture device '{deviceUid}' could not be selected: {Describe(status)}");
        }
        finally
        {
            if (reference != IntPtr.Zero)
                CFRelease(reference);
        }
    }

    private static int InputChannelCount(uint deviceId)
    {
        // kAudioDevicePropertyStreamConfiguration hands back an AudioBufferList; sum its channels.
        var address = new PropertyAddress { Selector = SelectorStreamConfiguration, Scope = ScopeInput, Element = 0 };
        if (AudioObjectGetPropertyDataSize(deviceId, ref address, 0, IntPtr.Zero, out var size) != 0 || size == 0)
            return 0;

        var block = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, block) != 0)
                return 0;

            var buffers = Marshal.ReadInt32(block);
            var channels = 0;
            for (var i = 0; i < buffers; i++)
            {
                // AudioBufferList is { UInt32; pad; AudioBuffer[] }, AudioBuffer is { UInt32; UInt32; void* }.
                var offset = 8 + (i * 16);
                if (offset + sizeof(uint) > size)
                    break;
                channels += Marshal.ReadInt32(block, offset);
            }

            return channels;
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    private static string GetStringProperty(uint objectId, uint selector)
    {
        var address = new PropertyAddress { Selector = selector, Scope = ScopeGlobal, Element = 0 };
        var size = (uint)IntPtr.Size;
        var block = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, block) != 0)
                return string.Empty;

            var reference = Marshal.ReadIntPtr(block);
            if (reference == IntPtr.Zero)
                return string.Empty;

            try
            {
                var buffer = new byte[1024];
                return CFStringGetCString(reference, buffer, buffer.Length, EncodingUtf8)
                    ? Encoding.UTF8.GetString(buffer).TrimEnd('\0')
                    : string.Empty;
            }
            finally
            {
                CFRelease(reference);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>CoreAudio reports errors as packed FourCC ints; render both forms.</summary>
    private static string Describe(int status)
    {
        var chars = new[] { (char)(byte)(status >> 24), (char)(byte)(status >> 16), (char)(byte)(status >> 8), (char)(byte)status };
        return Array.TrueForAll(chars, c => c is >= ' ' and < (char)127)
            ? $"{status} ('{new string(chars)}')"
            : status.ToString();
    }
}
