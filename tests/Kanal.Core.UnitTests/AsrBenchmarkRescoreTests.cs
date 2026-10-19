using Kanal.AsrBenchmark;

namespace Kanal.Core.UnitTests;

public class AsrBenchmarkRescoreTests
{
    private static Row Load(string model, string provider, int threads) =>
        new("windows", "machine", model, provider, threads, 1, 0, "-", "-", "-", "load_s", 1, "s");

    private static ClipRow Clip(string model, string provider, string id, string hypothesis) =>
        new(model, provider, "auto", "ascend", "mixed", id, "我 hello", hypothesis, 0, 0, 0, 0, "", 0);

    [Fact]
    public void A_model_on_two_providers_keeps_a_score_and_a_thread_count_for_each_provider()
    {
        var saved = new SavedReport(DateTime.Now, "machine", [],
            [Load("a", "cpu", 4), Load("a", "directml", 2)],
            [Clip("a", "cpu", "1", "我 hello"), Clip("a", "directml", "1", "")]);

        var (rows, clips) = Rescore.Apply([saved]);

        var cpu = rows.Single(r => r.Provider == "cpu" && r.Metric == "mer_pct");
        var directml = rows.Single(r => r.Provider == "directml" && r.Metric == "mer_pct");
        Assert.Equal((0.0, 4), (cpu.Value, cpu.Threads));
        Assert.Equal((100.0, 2), (directml.Value, directml.Threads));
        Assert.Equal(2, clips.Count);
    }

    [Fact]
    public void The_same_clip_in_two_input_files_is_refused()
    {
        var first = new SavedReport(DateTime.Now, "machine", [], [Load("a", "cpu", 4)], [Clip("a", "cpu", "1", "我 hello")]);
        var second = new SavedReport(DateTime.Now, "machine", [], [Load("a", "cpu", 4)], [Clip("a", "cpu", "1", "我")]);

        Assert.Throws<InvalidDataException>(() => Rescore.Apply([first, second]));
    }
}
