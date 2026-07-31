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
/// <param name="Help">
/// What this mode actually does, for the help flyout. Three of the five cannot run yet, so a row
/// the operator cannot pick still has to explain what it would do — and, like every other string
/// here, without naming the company that would do it.
/// </param>
public sealed record PipelineMode(
    PipelineModeId Id,
    string Name,
    StageKind Transcription,
    StageKind Translation,
    string Leaves,
    string Help)
{
    private const string Nothing = "nothing leaves this machine";
    private const string Audio = "audio leaves this machine";
    private const string TextOnly = "only text leaves this machine";

    public static IReadOnlyList<PipelineMode> All { get; } =
    [
        new(PipelineModeId.Demo, "Demo — scripted", StageKind.Scripted, StageKind.Scripted, Nothing,
            "A fixed script of six utterances, looping. No microphone, no account, no network — "
            + "for checking the room's screens, the join QR and the phones before anyone arrives."),
        new(PipelineModeId.CloudCloud, "Cloud transcription · cloud translation",
            StageKind.Cloud, StageKind.Cloud, Audio,
            "Both stages run off this machine in one streaming session. The fastest and the most "
            + "accurate option available today, and the one that sends the room's audio out. "
            + "Needs an API key."),
        new(PipelineModeId.CloudLocal, "Cloud transcription · local translation",
            StageKind.Cloud, StageKind.Local, Audio,
            "Speech is transcribed off this machine; the transcript is then translated here by a "
            + "downloaded model, so the wording never leaves. The audio still does. "
            + "Needs an API key and a translation model."),
        new(PipelineModeId.LocalCloud, "Local transcription · cloud translation",
            StageKind.Local, StageKind.Cloud, TextOnly,
            "Speech never leaves the room — only the transcript is sent out to be translated. "
            + "The cheapest way to buy good translation, and the only mode that satisfies the "
            + "goal this tool was built around. Waiting on local transcription."),
        new(PipelineModeId.LocalLocal, "Local transcription · local translation",
            StageKind.Local, StageKind.Local, Nothing,
            "Everything runs on this laptop and nothing is sent anywhere. The strongest privacy "
            + "position and the heaviest on the machine. Waiting on local transcription."),
    ];

    public static PipelineMode Of(PipelineModeId id) => All.First(m => m.Id == id);

    /// <summary>Scripted transcription needs no input device — and no microphone meter.</summary>
    public bool NeedsMicrophone => Transcription != StageKind.Scripted;
}
