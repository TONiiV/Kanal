namespace Kanal.Core.Relay;

/// <summary>Signs every semantic room message before handing it to the public transport.</summary>
public sealed class SignedRelayPublisher(IRelayPublisher inner, RelaySigningKey signingKey)
    : IRelayPublisher
{
    private int _disposed;

    public Task PublishAsync(RelayMessage message, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        return inner.PublishAsync(signingKey.Sign(message), ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await inner.DisposeAsync();
        signingKey.Dispose();
    }
}
