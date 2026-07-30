namespace Kanal.Core.Models;

/// <summary>
/// Room-level configuration broadcast to clients (language dropdown source).
/// </summary>
public sealed record RoomConfig(
    string RoomId,
    IReadOnlyList<string> Languages);
