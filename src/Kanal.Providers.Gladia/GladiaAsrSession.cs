using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Kanal.Core.Providers;

namespace Kanal.Providers.Gladia;

public sealed class GladiaAsrSession : IAsrSession
{
    private readonly Uri _url;
    private readonly int _maxReconnectAttempts;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<AsrEvent> _events = Channel.CreateUnbounded<AsrEvent>();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly GladiaWire _wire = new();
    private ClientWebSocket _socket = null!;
    private Task? _receiveLoop;

    internal GladiaAsrSession(Uri url, int maxReconnectAttempts)
    {
        _url = url;
        _maxReconnectAttempts = maxReconnectAttempts;
    }

    public IAsyncEnumerable<AsrEvent> Events => ReadEventsAsync();

    /// <summary>Raw JSON messages as received — diagnostics only (Kanal.Doctor).</summary>
    public event Action<string>? RawMessageReceived;

    internal async Task ConnectAsync(CancellationToken ct)
    {
        _socket = await OpenSocketAsync(ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task<ClientWebSocket> OpenSocketAsync(CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await socket.ConnectAsync(_url, ct);
        return socket;
    }

    public async ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.SendAsync(pcm16, WebSocketMessageType.Binary, endOfMessage: true, ct);
            // frames pushed while reconnecting are dropped — live captions can't wait
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async IAsyncEnumerable<AsrEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in _events.Reader.ReadAllAsync(ct))
            yield return e;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        var reconnects = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _events.Writer.WriteAsync(new AsrEvent.Ended(
                        _socket.CloseStatusDescription ?? "closed by server"), ct);
                    break;
                }

                reconnects = 0;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                RawMessageReceived?.Invoke(json);

                foreach (var e in _wire.Parse(json))
                    await _events.Writer.WriteAsync(e, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                message.SetLength(0);
                if (++reconnects > _maxReconnectAttempts)
                {
                    await _events.Writer.WriteAsync(new AsrEvent.Error(
                        $"Connection lost and {_maxReconnectAttempts} reconnect attempts failed: {ex.Message}",
                        Fatal: true), CancellationToken.None);
                    await _events.Writer.WriteAsync(new AsrEvent.Ended("connection lost"), CancellationToken.None);
                    break;
                }

                await _events.Writer.WriteAsync(new AsrEvent.Error(
                    $"Connection lost, reconnecting (attempt {reconnects}/{_maxReconnectAttempts})…",
                    Fatal: false), CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, reconnects - 1)), ct);

                await _sendLock.WaitAsync(ct);
                try
                {
                    _socket.Dispose();
                    _socket = await OpenSocketAsync(ct);
                }
                catch (Exception reconnectEx)
                {
                    await _events.Writer.WriteAsync(new AsrEvent.Error(
                        $"Reconnect failed: {reconnectEx.Message}", Fatal: false), CancellationToken.None);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
        }

        _events.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                var stop = Encoding.UTF8.GetBytes("""{"type":"stop_recording"}""");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await _socket.SendAsync(stop, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                }
                catch
                {
                    // best-effort graceful stop
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }

        _cts.Cancel();
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _socket.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
