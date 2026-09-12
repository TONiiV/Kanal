using System;
using System.Collections.Generic;
using System.Linq;
using Kanal.Core.Models;
using Kanal.Core.Room;
using Kanal.Core.Workspaces;

namespace Kanal.Host.ViewModels;

public static class StoredTranscript
{
    public static IReadOnlyList<ColumnViewModel> Of(MeetingRecord record) =>
        Columns(
            record.TranscriptPath is { } path ? TranscriptLog.Read(path) : [],
            record.Languages ?? []);

    public static IReadOnlyList<ColumnViewModel> Columns(
        IReadOnlyList<Utterance> utterances, IReadOnlyList<string> languages)
    {
        if (utterances.Count == 0)
            return [];

        var codes = (languages.Count > 0 ? languages : Spoken(utterances))
            .Select(c => c.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MainViewModel.MaxLanguages)
            .ToList();

        var colours = ColoursByTag(utterances);
        var columns = codes.Select(code => new ColumnViewModel(code)).ToList();
        foreach (var utterance in utterances)
        {
            foreach (var column in columns)
            {
                var isSource = string.Equals(
                    column.Language, utterance.SrcLang, StringComparison.OrdinalIgnoreCase);
                var translation = utterance.Translations.GetValueOrDefault(column.Language);

                var bubble = column.GetOrAdd(utterance.Id);
                bubble.SpeakerTag = utterance.SpeakerTag;
                bubble.SpeakerName = utterance.SpeakerTag;
                bubble.SpeakerColor = colours[utterance.SpeakerTag];
                bubble.SourceLang = utterance.SrcLang.ToUpperInvariant();
                bubble.IsPartial = false;
                bubble.CodeSwitch = utterance.CodeSwitch;
                bubble.IsTranscript = isSource;
                bubble.AwaitingTranslation = false;
                bubble.Text = isSource ? utterance.SrcText : translation ?? "";
                bubble.SourceText = isSource || translation is null ? "" : utterance.SrcText;
            }
        }

        // Nothing in a finished transcript is live; the weight that marks the newest line would
        // read here as a meeting still going on.
        foreach (var bubble in columns.SelectMany(c => c.Bubbles))
            bubble.IsLive = false;

        return columns;
    }

    private static IReadOnlyList<string> Spoken(IReadOnlyList<Utterance> utterances) =>
        [.. utterances.Select(u => u.SrcLang)
            .Concat(utterances.SelectMany(u => u.Translations.Keys))
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static Dictionary<string, string> ColoursByTag(IReadOnlyList<Utterance> utterances)
    {
        var colours = new Dictionary<string, string>();
        foreach (var utterance in utterances)
        {
            if (!colours.ContainsKey(utterance.SpeakerTag))
                colours[utterance.SpeakerTag] =
                    RoomState.Palette[colours.Count % RoomState.Palette.Count];
        }

        return colours;
    }
}
