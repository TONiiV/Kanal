namespace Kanal.Host.Services;

/// <summary>
/// What the host listens to. This is deliberately separate from <see cref="PipelineMode">:
/// choosing room or call audio must not silently change where transcription or translation runs.
/// </summary>
public enum CaptureProfileId
{
    InRoom,
    OnlineMeeting,
}

public sealed record CaptureProfile(
    CaptureProfileId Id,
    string NameKey,
    string GuidanceKey,
    string MarkdownValue,
    string JsonValue,
    string? UnavailableKey = null);
