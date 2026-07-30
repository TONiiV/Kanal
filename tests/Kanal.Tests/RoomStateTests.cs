using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Room;

namespace Kanal.Tests;

public class RoomStateTests
{
    private static RoomState NewRoom() =>
        new(new RoomConfig("test", ["zh", "de", "pl"]));

    private static AsrEvent.Transcript Transcript(
        string id, string text, bool isFinal = false, string speaker = "S01", string lang = "zh") =>
        new(id, speaker, text, lang, 0, isFinal ? 1000 : null, isFinal,
            CodeSwitch: false, SpeakerConfidence: 0.9, Translations: null);

    [Fact]
    public void PartialRewriteIncrementsRevisionAndReplacesInPlace()
    {
        var room = NewRoom();

        room.ApplyTranscript(Transcript("u1", "这批"));
        room.ApplyTranscript(Transcript("u1", "这批支架"));
        var final = room.ApplyTranscript(Transcript("u1", "这批支架的料号", isFinal: true));

        Assert.Equal(3, final.Revision);
        Assert.Equal(UtteranceState.Final, final.State);
        Assert.Single(room.Snapshot().Utterances);
    }

    [Fact]
    public void StaleTranslationIsDropped()
    {
        var room = NewRoom();
        room.ApplyTranscript(Transcript("u1", "第一版", isFinal: true)); // revision 1
        room.ApplyTranscript(Transcript("u1", "第二版", isFinal: true)); // revision 2

        var result = room.ApplyTranslations("u1", sourceRevision: 1, new Dictionary<string, string>
        {
            ["de"] = "veraltet",
        });

        Assert.Null(result);
        Assert.Empty(room.Snapshot().Utterances.Single().Translations);
    }

    [Fact]
    public void CurrentTranslationIsMerged()
    {
        var room = NewRoom();
        var u = room.ApplyTranscript(Transcript("u1", "料号 KX-4402", isFinal: true));

        var updated = room.ApplyTranslations("u1", u.Revision, new Dictionary<string, string>
        {
            ["de"] = "Teilenummer KX-4402",
        });

        Assert.NotNull(updated);
        Assert.Equal("Teilenummer KX-4402", updated!.Translations["de"]);
    }

    [Fact]
    public void MergeReassignsFutureEventsAndKeepsHistoryResolvable()
    {
        var room = NewRoom();
        room.ApplyTranscript(Transcript("u1", "a", speaker: "S01", isFinal: true));
        room.ApplyTranscript(Transcript("u2", "b", speaker: "S03", isFinal: true));

        var merged = room.MergeSpeakers("S03", "S01");

        Assert.Contains("S03", merged.MergedFrom);
        // future ASR events carrying S03 resolve to S01
        var next = room.ApplyTranscript(Transcript("u3", "c", speaker: "S03", isFinal: true));
        Assert.Equal("S01", next.SpeakerTag);
        // historical utterance keeps its tag; clients resolve via MergedFrom
        Assert.Equal("S03", room.Snapshot().Utterances.Single(u => u.Id == "u2").SpeakerTag);
        // merged speaker no longer listed separately
        Assert.DoesNotContain(room.Snapshot().Speakers, s => s.Tag == "S03");
    }

    [Fact]
    public void MergeChainsResolveTransitively()
    {
        var room = NewRoom();
        room.ApplyTranscript(Transcript("u1", "a", speaker: "S01"));
        room.ApplyTranscript(Transcript("u2", "b", speaker: "S02"));
        room.ApplyTranscript(Transcript("u3", "c", speaker: "S03"));

        room.MergeSpeakers("S03", "S02");
        room.MergeSpeakers("S02", "S01");

        Assert.Equal("S01", room.ResolveTag("S03"));
        Assert.Equal("S01", room.ResolveTag("S02"));
        var s01 = room.Snapshot().Speakers.Single();
        Assert.Contains("S02", s01.MergedFrom);
        Assert.Contains("S03", s01.MergedFrom);
    }

    [Fact]
    public void RenameSurvivesSnapshot()
    {
        var room = NewRoom();
        room.ApplyTranscript(Transcript("u1", "a", speaker: "S01"));

        room.RenameSpeaker("S01", "王工");

        Assert.Equal("王工", room.Snapshot().Speakers.Single().DisplayName);
    }

    [Fact]
    public void RecentFinalsSkipsPartialsAndExcludedId()
    {
        var room = NewRoom();
        room.ApplyTranscript(Transcript("u1", "a", isFinal: true));
        room.ApplyTranscript(Transcript("u2", "b", isFinal: false));
        room.ApplyTranscript(Transcript("u3", "c", isFinal: true));

        var recent = room.RecentFinals(10, excludeId: "u3");

        Assert.Equal(["u1"], recent.Select(u => u.Id).ToArray());
    }
}
