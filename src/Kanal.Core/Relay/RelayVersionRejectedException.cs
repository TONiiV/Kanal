namespace Kanal.Core.Relay;

public sealed class RelayVersionRejectedException(string message) : InvalidOperationException(message);
