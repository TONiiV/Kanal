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
        await session.ConnectAsync(CancellationToken.None);

        await using var events = session.Events.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.False(Assert.IsType<AsrEvent.Error>(events.Current).Fatal);

        await session.DisposeAsync();

        Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
