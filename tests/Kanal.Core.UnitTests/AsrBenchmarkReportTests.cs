using Kanal.AsrBenchmark;

namespace Kanal.Core.UnitTests;

public class AsrBenchmarkReportTests
{
    private static readonly Clip[] Clips = [new("fleurs", "en", "a", "hello", new short[16_000])];

    private static Row R(string model, string provider, string set, string lang, string room, string metric, double value) =>
        new("windows", "machine", model, provider, 4, 1, 0, set, lang, room, metric, value, "x");

    private static string Line(string report, string start) =>
        report.Split('\n').Select(l => l.TrimEnd()).Single(l => l.StartsWith(start, StringComparison.Ordinal));

    [Fact]
    public void A_cloud_engine_shows_no_speed_numbers()
    {
        Row[] rows =
        [
            R("gladia", "cloud", "-", "-", "-", "load_s", 1),
            R("gladia", "cloud", "fleurs", "en", "auto", "wer_pct", 10),
            R("gladia", "cloud", "fleurs", "en", "auto", "rtf", 1.234),
            R("gladia", "cloud", "fleurs", "en", "auto", "decode_p50_ms", 0),
            R("gladia", "cloud", "fleurs", "en", "auto", "decode_p95_ms", 0),
            R("gladia", "cloud", "fleurs", "all", "auto-realtime", "clips", 3),
            R("gladia", "cloud", "fleurs", "all", "auto-realtime", "decode_p50_ms", 0),
            R("gladia", "cloud", "fleurs", "all", "auto-realtime", "decode_p95_ms", 0),
            R("gladia", "cloud", "fleurs", "all", "auto-realtime", "decode_max_ms", 0),
        ];

        var report = Report.Build(DateTime.Now, "machine", Clips, rows, [], []);

        Assert.EndsWith("| – | – | – |", Line(report, "| gladia | cloud | fleurs | en |"));
        Assert.Contains("| –/–/– |", Line(report, "| gladia | cloud | 4 | 3 |"));
        Assert.DoesNotContain("1.234", report);
    }

    [Fact]
    public void A_local_engine_shows_its_speed_numbers()
    {
        Row[] rows =
        [
            R("nemotron", "cpu", "-", "-", "-", "load_s", 1),
            R("nemotron", "cpu", "fleurs", "en", "forced", "wer_pct", 10),
            R("nemotron", "cpu", "fleurs", "en", "forced", "rtf", 0.25),
            R("nemotron", "cpu", "fleurs", "en", "forced", "decode_p50_ms", 120),
            R("nemotron", "cpu", "fleurs", "en", "forced", "decode_p95_ms", 300),
        ];

        var report = Report.Build(DateTime.Now, "machine", Clips, rows, [], []);

        Assert.Contains("| 0.250 |", Line(report, "| nemotron | cpu | fleurs | en |"));
        Assert.EndsWith("| 120/300 |", Line(report, "| nemotron | cpu | fleurs | en |"));
    }

    [Theory]
    [InlineData("forced")]
    [InlineData("auto")]
    public void The_no_digit_clip_count_comes_from_whichever_room_has_it(string room)
    {
        Row[] rows =
        [
            R("cohere", "cpu", "-", "-", "-", "load_s", 1),
            R("cohere", "cpu", "fleurs", "en", room, "wer_pct", 12),
            R("cohere", "cpu", "fleurs", "en", room, "clips_no_digit", 7),
        ];

        var report = Report.Build(DateTime.Now, "machine", Clips, rows, [], []);

        var cells = Line(report, "| cohere | cpu | fleurs | en |").Split('|').Select(c => c.Trim()).ToArray();
        Assert.Equal("7", cells[9]);
    }

    [Fact]
    public void A_plain_fetch_leaves_out_the_paid_and_key_bound_sets()
    {
        Assert.All(Corpus.OptInSets, set => Assert.True(Corpus.Sets.ContainsKey(set)));
        Assert.Empty(Corpus.DefaultSets.Intersect(Corpus.OptInSets));
        Assert.Contains("fleurs", Corpus.DefaultSets);
        Assert.DoesNotContain("meeting-11labs", Corpus.DefaultSets);
        Assert.DoesNotContain("commonvoice", Corpus.DefaultSets);
        Assert.Contains("ascend", Corpus.DefaultSets);
    }

    [Fact]
    public void The_ascend_set_has_its_own_table_and_stays_out_of_the_language_table()
    {
        Clip[] clips =
        [
            new("ascend", "mixed", "a", "你好 hello", new short[16_000]),
            new("ascend", "en", "b", "hello", new short[16_000]),
        ];
        Row[] rows =
        [
            R("whisper", "cpu", "-", "-", "-", "load_s", 1),
            R("whisper", "cpu", "ascend", "mixed", "forced", "mer_pct", 31.26),
            R("whisper", "cpu", "ascend", "mixed", "auto", "mer_pct", 40.5),
            R("whisper", "cpu", "ascend", "mixed", "forced", "english_kept_pct", 88.2),
            R("whisper", "cpu", "ascend", "mixed", "auto", "english_kept_pct", 20),
            R("whisper", "cpu", "ascend", "mixed", "auto", "clips", 60),
            R("whisper", "cpu", "ascend", "en", "auto", "mer_pct", 12),
            R("whisper", "cpu", "ascend", "en", "auto", "clips", 15),
        ];

        var report = Report.Build(DateTime.Now, "machine", clips, rows, [], []);

        var cells = Line(report, "| whisper | cpu | mixed |").Split('|').Select(c => c.Trim()).ToArray();
        Assert.Equal("60", cells[4]);
        Assert.Equal("31.3", cells[5]);
        Assert.Equal("40.5", cells[6]);
        Assert.Equal("88.2/20.0", cells[8]);
        Assert.Contains("| whisper | cpu | en | 15 |", report);
        Assert.DoesNotContain("| whisper | cpu | ascend |", report);
    }
}
