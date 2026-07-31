using System.Collections.Generic;
using System.Linq;

namespace Kanal.Host.Services;

/// <summary>Where a stage runs. The operator's real question, asked once per stage.</summary>
public enum StageKind
{
    /// <summary>A scripted stand-in: no network, no key, no model.</summary>
    Scripted,

    /// <summary>An off-machine service.</summary>
    Cloud,

    /// <summary>In this process, on this machine.</summary>
    Local,
}

public enum PipelineModeId
{
    Demo,
    CloudCloud,
    CloudLocal,
    LocalCloud,
    LocalLocal,
}

/// <summary>
/// A preset naming the whole pipeline — transcription and translation — rather than a vendor.
/// Kanal runs two stages that are chosen independently; a mode that named only the ASR company
/// hid half of that, including the one fact an operator has to be sure of before a meeting with
/// an outside supplier: what leaves this machine.
/// </summary>
/// <param name="Leaves">
/// The privacy consequence in one line. Rendered next to every mode, available or not.
/// </param>
public sealed record PipelineMode(
    PipelineModeId Id,
    string Name,
    StageKind Transcription,
    StageKind Translation,
    string Leaves)
{
    private const string Nothing = "nothing leaves this machine";
    private const string Audio = "audio leaves this machine";
    private const string TextOnly = "only text leaves this machine";

    public static IReadOnlyList<PipelineMode> All { get; } =
    [
        new(PipelineModeId.Demo, "Demo — scripted", StageKind.Scripted, StageKind.Scripted, Nothing),
        new(PipelineModeId.CloudCloud, "Cloud transcription · cloud translation",
            StageKind.Cloud, StageKind.Cloud, Audio),
        new(PipelineModeId.CloudLocal, "Cloud transcription · local translation",
            StageKind.Cloud, StageKind.Local, Audio),
        new(PipelineModeId.LocalCloud, "Local transcription · cloud translation",
            StageKind.Local, StageKind.Cloud, TextOnly),
        new(PipelineModeId.LocalLocal, "Local transcription · local translation",
            StageKind.Local, StageKind.Local, Nothing),
    ];

    public static PipelineMode Of(PipelineModeId id) => All.First(m => m.Id == id);

    /// <summary>Scripted transcription needs no input device — and no microphone meter.</summary>
    public bool NeedsMicrophone => Transcription != StageKind.Scripted;
}
