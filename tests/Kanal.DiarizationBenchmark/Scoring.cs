using System.Globalization;
using Kanal.Core.Meetings;
using Kanal.Core.Models;

namespace Kanal.DiarizationBenchmark;

internal sealed record Segment(double Start, double End, string Speaker)
{
    public double Seconds => End - Start;
}

internal sealed record DerResult(double Scored, double Missed, double FalseAlarm, double Confusion)
{
    public double Der => Rate(Missed + FalseAlarm + Confusion);

    public double Rate(double seconds) => Scored == 0 ? 0 : seconds / Scored;
}

internal static class Scoring
{
    public const double Step = 0.01;

    // Frame-based, as md-eval: overlapped reference speech counts once per speaker, and the
    // collar removes frames within ± collar of every reference boundary.
    public static DerResult Der(IReadOnlyList<Segment> reference, IReadOnlyList<Segment> hypothesis, double collar)
    {
        var frames = (int)Math.Ceiling(reference.Concat(hypothesis).Select(s => s.End).DefaultIfEmpty(0).Max() / Step);
        var refSpeakers = reference.Select(s => s.Speaker).Distinct().ToList();
        var hypSpeakers = hypothesis.Select(s => s.Speaker).Distinct().ToList();
        var r = Activity(reference, refSpeakers, frames);
        var h = Activity(hypothesis, hypSpeakers, frames);

        var scored = Enumerable.Repeat(true, frames).ToArray();
        if (collar > 0)
            foreach (var boundary in reference.SelectMany(s => new[] { s.Start, s.End }))
                for (var f = Math.Max(0, (int)((boundary - collar) / Step) - 1); f < frames && (f + 0.5) * Step < boundary + collar; f++)
                    if (Math.Abs((f + 0.5) * Step - boundary) < collar)
                        scored[f] = false;

        var overlap = new double[refSpeakers.Count, hypSpeakers.Count];
        for (var f = 0; f < frames; f++)
            if (scored[f])
                for (var i = 0; i < refSpeakers.Count; i++)
                    if (r[i][f])
                        for (var j = 0; j < hypSpeakers.Count; j++)
                            if (h[j][f])
                                overlap[i, j]++;
        var map = Match(overlap);

        double total = 0, missed = 0, falseAlarm = 0, confusion = 0;
        for (var f = 0; f < frames; f++)
        {
            if (!scored[f])
                continue;
            int nRef = 0, nHyp = 0, correct = 0;
            for (var i = 0; i < refSpeakers.Count; i++)
            {
                if (!r[i][f])
                    continue;
                nRef++;
                if (map[i] >= 0 && h[map[i]][f])
                    correct++;
            }

            for (var j = 0; j < hypSpeakers.Count; j++)
                if (h[j][f])
                    nHyp++;
            total += nRef;
            missed += Math.Max(0, nRef - nHyp);
            falseAlarm += Math.Max(0, nHyp - nRef);
            confusion += Math.Min(nRef, nHyp) - correct;
        }

        return new DerResult(total * Step, missed * Step, falseAlarm * Step, confusion * Step);
    }

    public static Result? Live(IEnumerable<Result> results, string model, double threshold) =>
        results.Last(r => r.System == "live" && r.Model == model && Math.Abs(r.Threshold - threshold) < 1e-6);

    // Hungarian method on the padded square matrix; returns the column of each row, or -1.
    public static int[] Match(double[,] weight)
    {
        int rows = weight.GetLength(0), cols = weight.GetLength(1), n = Math.Max(rows, cols);
        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, n + 1).ToArray();
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                var delta = double.PositiveInfinity;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j])
                        continue;
                    var cur = Cost(i0, j) - u[i0] - v[j];
                    if (cur < minv[j])
                        (minv[j], way[j]) = (cur, j0);
                    if (minv[j] < delta)
                        (delta, j1) = (minv[j], j);
                }

                for (var j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            } while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }

        var match = Enumerable.Repeat(-1, rows).ToArray();
        for (var j = 1; j <= cols; j++)
            if (p[j] >= 1 && p[j] <= rows && weight[p[j] - 1, j - 1] > 0)
                match[p[j] - 1] = j - 1;
        return match;

        double Cost(int i, int j) => i <= rows && j <= cols ? -weight[i - 1, j - 1] : 0;
    }

    // The operator's view: each utterance takes the tag that SpeakerReidentification.Assign gives it.
    public static string?[] Attribute(IReadOnlyList<Segment> utterances, IReadOnlyList<SpeakerTurn> turns)
    {
        var list = utterances.Select((u, i) => new Utterance(
            i.ToString(CultureInfo.InvariantCulture), u.Speaker, (long)Math.Round(u.Start * 1000), (long)Math.Round(u.End * 1000),
            "", "", 0, UtteranceState.Final, false, 0, new Dictionary<string, string>())).ToList();
        var tags = SpeakerReidentification.Assign(list, turns);
        return [.. list.Select(u => tags.GetValueOrDefault(u.Id))];
    }

    public static IReadOnlyDictionary<string, string> MapTags(IReadOnlyList<(string Truth, string? Tag)> utterances)
    {
        var truths = utterances.Select(u => u.Truth).Distinct().ToList();
        var tags = utterances.Where(u => u.Tag is not null).Select(u => u.Tag!).Distinct().ToList();
        var counts = new double[truths.Count, tags.Count];
        foreach (var (truth, tag) in utterances)
            if (tag is not null)
                counts[truths.IndexOf(truth), tags.IndexOf(tag)]++;
        var match = Match(counts);
        return Enumerable.Range(0, truths.Count).Where(i => match[i] >= 0).ToDictionary(i => tags[match[i]], i => truths[i]);
    }

    public static (int Correct, int Total) Accuracy(
        IEnumerable<(string Truth, string? Tag)> utterances, IReadOnlyDictionary<string, string> map)
    {
        int correct = 0, total = 0;
        foreach (var (truth, tag) in utterances)
        {
            total++;
            if (tag is not null && map.TryGetValue(tag, out var mapped) && mapped == truth)
                correct++;
        }

        return (correct, total);
    }

    private static bool[][] Activity(IReadOnlyList<Segment> segments, List<string> speakers, int frames)
    {
        var active = speakers.Select(_ => new bool[frames]).ToArray();
        foreach (var s in segments)
        {
            var row = active[speakers.IndexOf(s.Speaker)];
            for (var f = Math.Max(0, (int)(s.Start / Step) - 1); f < frames && (f + 0.5) * Step < s.End; f++)
                if ((f + 0.5) * Step >= s.Start)
                    row[f] = true;
        }

        return active;
    }
}
