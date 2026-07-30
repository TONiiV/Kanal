namespace Kanal.Core.Relay;

/// <summary>
/// The replaceable relay layer (hosted pub/sub, tunnel, domestic service…).
/// Swapping transport means swapping one implementation of this interface.
/// </summary>
public interface IRelayPublisher : IAsyncDisposable
{
    Task PublishAsync(RelayMessage message, CancellationToken ct = default);
}

/// <summary>No-op relay for offline use and tests.</summary>
public sealed class NullRelayPublisher : IRelayPublisher
{
    public Task PublishAsync(RelayMessage message, CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
