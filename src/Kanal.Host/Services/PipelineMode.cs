using System.Collections.Generic;
using System.Linq;
using Kanal.Host.Localization;

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
/// <param name="LeavesKey">
/// The privacy consequence in one line. Rendered next to every mode, available or not.
/// </param>
/// <param name="HelpKey">
/// What this mode actually does, for the help flyout. Three of the five cannot run yet, so a row
/// the operator cannot pick still has to explain what it would do — and, like every other string
/// here, without naming the company that would do it, in any of the four languages.
/// </param>
/// <remarks>
/// Modes carry localisation keys rather than text, and resolve on read: the operator can change
/// the application's language mid-meeting and the list has to follow rather than stay in whatever
/// language it happened to be built in.
/// </remarks>
public sealed record PipelineMode(
    PipelineModeId Id,
    string NameKey,
    StageKind Transcription,
    StageKind Translation,
    string LeavesKey,
    string HelpKey)
{
    private const string Nothing = "leaves.nothing";
    private const string Audio = "leaves.audio";
    private const string TextOnly = "leaves.text";

    public string Name => Localizer.Instance[NameKey];

    public string Leaves => Localizer.Instance[LeavesKey];

    public string Help => Localizer.Instance[HelpKey];

    public static IReadOnlyList<PipelineMode> All { get; } =
    [
        new(PipelineModeId.Demo, "mode.demo.name",
            StageKind.Scripted, StageKind.Scripted, Nothing, "mode.demo.help"),
        new(PipelineModeId.CloudCloud, "mode.cloudcloud.name",
            StageKind.Cloud, StageKind.Cloud, Audio, "mode.cloudcloud.help"),
        new(PipelineModeId.CloudLocal, "mode.cloudlocal.name",
            StageKind.Cloud, StageKind.Local, Audio, "mode.cloudlocal.help"),
        new(PipelineModeId.LocalCloud, "mode.localcloud.name",
            StageKind.Local, StageKind.Cloud, TextOnly, "mode.localcloud.help"),
        new(PipelineModeId.LocalLocal, "mode.locallocal.name",
            StageKind.Local, StageKind.Local, Nothing, "mode.locallocal.help"),
    ];

    public static PipelineMode Of(PipelineModeId id) => All.First(m => m.Id == id);

    /// <summary>Scripted transcription needs no input device — and no microphone meter.</summary>
    public bool NeedsMicrophone => Transcription != StageKind.Scripted;
}
