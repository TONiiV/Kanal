using System.Text;
using System.Text.RegularExpressions;
using Kanal.Core.Text;

namespace Kanal.AsrBenchmark;

internal static partial class Scoring
{
    public static string NormalizeChinese(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in SimplifiedChinese.Normalize(text).ToLowerInvariant())
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        return sb.ToString();
    }

    public static string[] Words(string text) =>
        NotWordChar().Split(text.ToLowerInvariant()).Where(w => w.Length > 0).ToArray();

    // CER for Chinese, WER for every other language; Length is the reference length.
    public static (int Errors, int Length) Errors(string lang, string hypothesis, string reference)
    {
        if (lang == "zh")
        {
            var r = NormalizeChinese(reference);
            return (Distance(NormalizeChinese(hypothesis).ToCharArray(), r.ToCharArray()), r.Length);
        }

        var words = Words(reference);
        return (Distance(Words(hypothesis), words), words.Length);
    }

    public static IReadOnlyList<string> Codes(string source) =>
        CodeToken().Matches(source)
            .Select(m => m.Value)
            .Where(v => v.Any(char.IsDigit) && v.Any(char.IsAsciiLetterUpper))
            .ToList();

    public static int CodesKept(string source, string output, bool loose)
    {
        if (!loose)
            return Codes(source).Count(output.Contains);
        var flat = Flat(output);
        return Codes(source).Count(c => flat.Contains(Flat(c)));
    }

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }

    public static int SpeechOnset(short[] samples, int frame)
    {
        var loud = Loud(samples, frame);
        return loud.Count == 0 ? 0 : loud.IndexOf(true) * frame;
    }

    public static int SpeechEnd(short[] samples, int frame)
    {
        var loud = Loud(samples, frame);
        return loud.Count == 0 ? samples.Length : (loud.LastIndexOf(true) + 1) * frame;
    }

    private static List<bool> Loud(short[] samples, int frame)
    {
        var rms = new List<double>();
        for (var i = 0; i + frame <= samples.Length; i += frame)
        {
            double sum = 0;
            for (var j = i; j < i + frame; j++)
                sum += (double)samples[j] * samples[j];
            rms.Add(Math.Sqrt(sum / frame));
        }

        var threshold = rms.DefaultIfEmpty().Max() / 10;
        return rms.Select(r => r >= threshold).ToList();
    }

    private static int Distance<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        var previous = Enumerable.Range(0, b.Count + 1).ToArray();
        var current = new int[b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                var substitution = previous[j - 1] + (EqualityComparer<T>.Default.Equals(a[i - 1], b[j - 1]) ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j], current[j - 1]) + 1);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Count];
    }

    private static string Flat(string text) => new(text.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWordChar();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z0-9][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*(?![A-Za-z0-9])")]
    private static partial Regex CodeToken();
}
