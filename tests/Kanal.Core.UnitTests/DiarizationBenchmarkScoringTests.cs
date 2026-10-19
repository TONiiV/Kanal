using Kanal.Core.Meetings;
using Kanal.DiarizationBenchmark;

namespace Kanal.Core.UnitTests;

public class DiarizationBenchmarkScoringTests
{
    private static Segment S(double start, double end, string speaker) => new(start, end, speaker);

    [Fact]
    public void A_perfect_hypothesis_under_other_labels_scores_zero()
    {
        var der = Scoring.Der([S(0, 10, "A"), S(10, 20, "B")], [S(0, 10, "7"), S(10, 20, "3")], collar: 0);

        Assert.Equal(20, der.Scored, 3);
        Assert.Equal(0, der.Der, 3);
    }

    [Fact]
    public void Speech_the_hypothesis_lacks_is_missed()
    {
        var der = Scoring.Der([S(0, 10, "A")], [S(0, 8, "x")], collar: 0);

        Assert.Equal(2, der.Missed, 3);
        Assert.Equal(0.2, der.Der, 3);
    }

    [Fact]
    public void Speech_the_hypothesis_adds_is_a_false_alarm()
    {
        var der = Scoring.Der([S(0, 10, "A")], [S(0, 12, "x")], collar: 0);

        Assert.Equal(2, der.FalseAlarm, 3);
        Assert.Equal(0.2, der.Der, 3);
    }

    [Fact]
    public void Speech_under_the_wrong_mapped_speaker_is_confusion()
    {
        var der = Scoring.Der([S(0, 10, "A"), S(10, 20, "B")], [S(0, 15, "x"), S(15, 20, "y")], collar: 0);

        Assert.Equal(5, der.Confusion, 3);
        Assert.Equal(0.25, der.Der, 3);
    }

    [Fact]
    public void A_split_speaker_leaves_the_smaller_cluster_as_confusion()
    {
        var der = Scoring.Der([S(0, 10, "A")], [S(0, 6, "x"), S(6, 10, "y")], collar: 0);

        Assert.Equal(4, der.Confusion, 3);
    }

    [Fact]
    public void The_collar_ignores_frames_near_every_reference_boundary()
    {
        var der = Scoring.Der([S(0, 10, "A")], [S(0, 10.2, "x")], collar: 0.25);

        Assert.Equal(9.5, der.Scored, 3);
        Assert.Equal(0, der.Der, 3);
        Assert.Equal(0.02, Scoring.Der([S(0, 10, "A")], [S(0, 10.2, "x")], collar: 0).Der, 3);
    }

    [Fact]
    public void Overlapped_reference_speech_counts_once_per_speaker()
    {
        var der = Scoring.Der([S(0, 10, "A"), S(5, 10, "B")], [S(0, 10, "x")], collar: 0);

        Assert.Equal(15, der.Scored, 3);
        Assert.Equal(5, der.Missed, 3);
    }

    [Fact]
    public void The_mapping_is_the_global_optimum_not_the_greedy_one()
    {
        Assert.Equal([1, 0], Scoring.Match(new double[,] { { 10, 9 }, { 9, 0 } }));
        Assert.Equal([2, -1], Scoring.Match(new double[,] { { 1, 0, 5 }, { 0, 0, 0 } }));
        Assert.Equal([0, -1, -1], Scoring.Match(new double[,] { { 3 }, { 0 }, { 0 } }));
    }

    [Fact]
    public void Each_utterance_takes_the_diarized_speaker_with_the_largest_overlap()
    {
        var tags = Scoring.Attribute(
            [S(0, 4, "A"), S(4, 6, "B"), S(10, 11, "A")],
            [new SpeakerTurn(0, 3.5, 5), new SpeakerTurn(3.5, 6, 2)]);

        Assert.Equal(["D01", "D02", null], tags);
    }

    [Fact]
    public void Utterance_accuracy_uses_one_mapping_and_counts_an_untagged_utterance_as_wrong()
    {
        (string, string?)[] utterances = [("A", "D01"), ("A", "D01"), ("A", "D02"), ("B", "D02"), ("B", null)];

        var map = Scoring.MapTags(utterances);

        Assert.Equal("A", map["D01"]);
        Assert.Equal("B", map["D02"]);
        Assert.Equal((3, 5), Scoring.Accuracy(utterances, map));
        Assert.Equal((1, 2), Scoring.Accuracy(utterances[3..], map));
    }

    [Fact]
    public void Scribe_words_of_one_speaker_join_until_the_speaker_changes_or_a_long_gap()
    {
        var result = System.Text.Json.Nodes.JsonNode.Parse("""
            {"words": [
              {"type": "word", "start": 0.0, "end": 0.4, "speaker_id": "speaker_1"},
              {"type": "spacing", "start": 0.4, "end": 0.5, "speaker_id": "speaker_1"},
              {"type": "word", "start": 0.5, "end": 1.0, "speaker_id": "speaker_1"},
              {"type": "word", "start": 2.0, "end": 2.5, "speaker_id": "speaker_1"},
              {"type": "word", "start": 2.6, "end": 3.0, "speaker_id": "speaker_0"}
            ]}
            """)!;

        Assert.Equal(
            [new SpeakerTurn(0, 1.0, 0), new SpeakerTurn(2.0, 2.5, 0), new SpeakerTurn(2.6, 3.0, 1)],
            Cloud.ScribeTurns(result));
    }

    private static Result LiveResult(string model, double threshold) =>
        new("m1", "zh", 2, false, false, model, "live", threshold, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            2, 8, 10, 0, 0, 8, 10, 0);

    [Fact]
    public void The_live_result_at_a_threshold_is_found_by_model_and_threshold()
    {
        var results = new[] { LiveResult("a", 0.5), LiveResult("b", 0.6), LiveResult("a", 0.6), LiveResult("a", 0.7) };

        var live = Scoring.Live(results, "a", 0.6);

        Assert.Same(results[2], live);
    }

    [Fact]
    public void No_live_result_at_the_default_threshold_gives_null_and_does_not_throw()
    {
        var results = new[] { LiveResult("a", 0.5), LiveResult("a", 0.7) };

        Assert.Null(Scoring.Live(results, "a", 0.6));
    }
}
