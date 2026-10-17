using System.Net.WebSockets;
using Kanal.Core.Providers;
using Kanal.Providers.Gladia;

namespace Kanal.Core.UnitTests;

public class GladiaSessionDisposeTests
{
    [Fact]
    public async Task EventsEndWhenTheSessionIsDisposedDuringAReconnectBackoff()
    {
        var session = new GladiaAsrSession(
            _ => Task.FromResult(WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions())),
            maxReconnectAttempts: 3);
        await session.ConnectAsync(TestContext.Current.CancellationToken);

        await using var events = session.Events.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await events.MoveNextAsync());
        Assert.False(Assert.IsType<AsrEvent.Error>(events.Current).Fatal);

        await session.DisposeAsync();

        Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposingAnOpenSessionEndsEventsWithoutAReconnect()
    {
        var session = new GladiaAsrSession(
            _ => Task.FromResult(WebSocket.CreateFromStream(new HangUpOnFirstWriteStream(), new WebSocketCreationOptions())),
            maxReconnectAttempts: 3);
        await session.ConnectAsync(CancellationToken.None);

        await session.DisposeAsync();

        var seen = new List<AsrEvent>();
        await using var events = session.Events.GetAsyncEnumerator();
        while (await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            seen.Add(events.Current);
        Assert.DoesNotContain(seen, e => e is AsrEvent.Error);
    }

    private sealed class HangUpOnFirstWriteStream : Stream
    {
        private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _written.Task.WaitAsync(ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            // The close frame waits, as on a real network, so the receive loop sees the hang-up before DisposeAsync cancels it.
            if (!_written.TrySetResult())
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
