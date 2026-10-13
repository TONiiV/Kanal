using System.Text.RegularExpressions;

namespace Kanal.Benchmark;

internal static partial class Scoring
{
    // chrF (Popović 2015): character 1..6-grams, whitespace removed, recall weighted by beta = 2.
    public static double ChrF(string hypothesis, string reference, int maxOrder = 6, double beta = 2)
    {
        var hyp = Whitespace().Replace(hypothesis, "");
        var refText = Whitespace().Replace(reference, "");
        double precision = 0, recall = 0;
        var orders = 0;
        for (var n = 1; n <= maxOrder; n++)
        {
            var h = Grams(hyp, n);
            var r = Grams(refText, n);
            if (h.Count == 0 || r.Count == 0)
                continue;
            var matches = h.Sum(kv => Math.Min(kv.Value, r.GetValueOrDefault(kv.Key)));
            precision += (double)matches / h.Values.Sum();
            recall += (double)matches / r.Values.Sum();
            orders++;
        }

        if (orders == 0)
            return 0;
        precision /= orders;
        recall /= orders;
        var b2 = beta * beta;
        return precision + recall == 0 ? 0 : 100 * (1 + b2) * precision * recall / (b2 * precision + recall);
    }

    public static IReadOnlyList<string> Codes(string source) =>
        CodeToken().Matches(source)
            .Select(m => m.Value)
            .Where(v => v.Any(char.IsDigit) && v.Any(char.IsAsciiLetterUpper))
            .ToList();

    public static bool KeepsCodes(string source, string output) =>
        Codes(source).All(output.Contains);

    public static bool LeaksScript(string targetLang, string output) =>
        targetLang == "zh" ? !output.Any(IsCjk) : output.Any(IsCjk);

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
    }

    private static Dictionary<string, int> Grams(string text, int n)
    {
        var grams = new Dictionary<string, int>();
        for (var i = 0; i + n <= text.Length; i++)
        {
            var g = text.Substring(i, n);
            grams[g] = grams.GetValueOrDefault(g) + 1;
        }
        return grams;
    }

    private static bool IsCjk(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z0-9][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*(?![A-Za-z0-9])")]
    private static partial Regex CodeToken();
}
