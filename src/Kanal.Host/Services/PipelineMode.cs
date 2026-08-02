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
/// here, without naming the company that would do it. The captions reach the phones through the
/// relay in every mode, so help may only describe what the *pipeline* sends out — never promise
/// "no network" or "nothing is sent": the operator repeats these words to the other side of the
/// table.
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
            "A fixed script of six utterances, looping. No microphone, no account, no key — only "
            + "the scripted captions go out, so the room's screens, the join QR and the phones "
            + "can be checked before anyone arrives."),
        new(PipelineModeId.CloudCloud, "Cloud transcription · cloud translation",
            StageKind.Cloud, StageKind.Cloud, Audio,
            "Both stages run off this machine in one streaming session — the lowest-latency "
            + "pairing today, and the one that sends the room's audio out. Needs an API key."),
        new(PipelineModeId.CloudLocal, "Cloud transcription · local translation",
            StageKind.Cloud, StageKind.Local, Audio,
            "Speech is transcribed off this machine; the transcript is then translated here by a "
            + "downloaded model, so no text is sent out for translation — the audio still is. "
            + "Needs an API key and a translation model."),
        new(PipelineModeId.LocalCloud, "Local transcription · cloud translation",
            StageKind.Local, StageKind.Cloud, TextOnly,
            "Speech never leaves the room — only the transcript is sent out to be translated, so "
            + "every network hop is text. The pairing this tool was built to reach. "
            + "Waiting on local transcription."),
        new(PipelineModeId.LocalLocal, "Local transcription · local translation",
            StageKind.Local, StageKind.Local, Nothing,
            "Both stages run on this laptop: the pipeline sends nothing out, and only the "
            + "captions cross the network on their way to the phones. The heaviest mode on the "
            + "machine. Waiting on local transcription."),
    ];

    public static PipelineMode Of(PipelineModeId id) => All.First(m => m.Id == id);

    /// <summary>Scripted transcription needs no input device — and no microphone meter.</summary>
    public bool NeedsMicrophone => Transcription != StageKind.Scripted;
}
