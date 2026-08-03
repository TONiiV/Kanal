namespace Kanal.Core.Providers;

/// <summary>
/// Optional capability: a provider whose backing resources load slowly — model weights, a
/// native runtime — and can be brought to a working state before the meeting starts. The host
/// checks for this interface, never for a vendor: whatever implements it gets warmed up, and
/// whatever does not starts as it always did.
/// </summary>
/// <remarks>
/// Idempotent — a second call after a completed load returns at once. Cancellable — a cancelled
/// load is abandoned, not latched, so the next call (or the first real request) loads again.
/// </remarks>
public interface IWarmupProvider
{
    /// <summary>Load whatever the first real request would have loaded, and return when the
    /// provider can answer that request at normal inference latency.</summary>
    Task WarmUpAsync(CancellationToken ct);
}
