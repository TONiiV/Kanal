namespace Kanal.AsrBenchmark;

internal static class Scoring
{
    public static string NormalizeChinese(string text) => throw new NotImplementedException();

    public static string[] Words(string text) => throw new NotImplementedException();

    public static (int Errors, int Length) Errors(string lang, string hypothesis, string reference) =>
        throw new NotImplementedException();

    public static IReadOnlyList<string> Codes(string source) => throw new NotImplementedException();

    public static int CodesKept(string source, string output, bool loose) => throw new NotImplementedException();

    public static double Percentile(IReadOnlyList<double> values, double p) => throw new NotImplementedException();

    public static int SpeechOnset(short[] samples, int frame) => throw new NotImplementedException();
}
