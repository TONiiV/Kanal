using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Kanal.Audio;

[SupportedOSPlatform("windows10.0.19041")]
public sealed class WasapiLoopbackAudioCapture : ISystemAudioCaptureService
{
    private const string ProcessLoopbackDevice = @"VAD\Process_Loopback";
    private const int ProcessLoopbackActivation = 1;
    private const int ExcludeTargetProcessTree = 1;
    private const ushort VariantBlob = 65;
    private static readonly long BufferDuration = TimeSpan.FromMilliseconds(200).Ticks;

    public SystemAudioBackend Backend => SystemAudioBackend.WasapiLoopback;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var capture = new ProcessLoopback(await ActivateAsync().WaitAsync(ct));
        await foreach (var frame in WasapiPcmCapture.RunAsync(capture, ct))
            yield return frame;
    }

    private static async Task<AudioClient> ActivateAsync()
    {
        var parameters = new ActivationParams
        {
            ActivationType = ProcessLoopbackActivation,
            TargetProcessId = (uint)Environment.ProcessId,
            ProcessLoopbackMode = ExcludeTargetProcessTree,
        };
        var size = Marshal.SizeOf<ActivationParams>();
        var data = Marshal.AllocHGlobal(size);
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
        try
        {
            Marshal.StructureToPtr(parameters, data, false);
            Marshal.StructureToPtr(new BlobVariant { VarType = VariantBlob, Size = (uint)size, Data = data }, variant, false);
            var completion = new Completion();
            ActivateAudioInterfaceAsync(ProcessLoopbackDevice, typeof(IAudioClient).GUID, variant, completion, out _);
            return new AudioClient(await completion.Result);
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(data);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlobVariant
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint Size;
        public IntPtr Data;
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    // Activation completes on an arbitrary thread; a handler that is not agile fails with E_ILLEGAL_METHOD_CALL.
    private sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Result => _result.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out var status, out var client);
                if (status < 0)
                    _result.TrySetException(Marshal.GetExceptionForHR(status)!);
                else
                    _result.TrySetResult((IAudioClient)client);
            }
            catch (Exception error)
            {
                _result.TrySetException(error);
            }
        }
    }

    // Process loopback has no mix format to ask for; the stream is converted to whatever Initialize requests.
    private sealed class ProcessLoopback(AudioClient client) : IWaveIn
    {
        private readonly AutoResetEvent _ready = new(false);
        private volatile bool _stopping;
        private Thread? _thread;

        public WaveFormat WaveFormat { get; set; } = new(44_100, 16, 2);

        public event EventHandler<WaveInEventArgs>? DataAvailable;

        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public void StartRecording()
        {
            client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback
                    | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                BufferDuration, 0, WaveFormat, Guid.Empty);
            client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
            client.Start();
            _thread = new Thread(Pump) { IsBackground = true, Name = "Kanal computer audio" };
            _thread.Start();
        }

        public void StopRecording()
        {
            _stopping = true;
            _ready.Set();
            _thread?.Join();
        }

        public void Dispose()
        {
            client.Dispose();
            _ready.Dispose();
        }

        private void Pump()
        {
            Exception? failure = null;
            try
            {
                var capture = client.AudioCaptureClient;
                while (!_stopping)
                {
                    _ready.WaitOne();
                    while (!_stopping && capture.GetNextPacketSize() > 0)
                    {
                        var data = capture.GetBuffer(out var frames, out var flags);
                        var bytes = new byte[frames * WaveFormat.BlockAlign];
                        if ((flags & AudioClientBufferFlags.Silent) == 0)
                            Marshal.Copy(data, bytes, 0, bytes.Length);
                        capture.ReleaseBuffer(frames);
                        DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
            }

            try
            {
                client.Stop();
            }
            catch (Exception error)
            {
                failure ??= error;
            }

            RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
        }
    }
}
