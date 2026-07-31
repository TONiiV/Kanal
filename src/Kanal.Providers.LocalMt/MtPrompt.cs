namespace Kanal.Providers.LocalMt;

/// <summary>
/// Prompt construction for local translation models. The wording is the tested
/// baseline for small (2–4B) instruct models: English language names, a hard
/// "translation only" instruction, and an explicit part-number guard.
/// </summary>
public static class MtPrompt
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "Chinese",
        ["de"] = "German",
        ["pl"] = "Polish",
        ["en"] = "English",
        ["fr"] = "French",
        ["es"] = "Spanish",
        ["it"] = "Italian",
        ["pt"] = "Portuguese",
        ["ja"] = "Japanese",
        ["ko"] = "Korean",
    };

    public static string LanguageName(string code) =>
        Names.TryGetValue(code, out var name) ? name : code;

    public static string Build(string text, string targetLang) =>
        $"Translate the following into {LanguageName(targetLang)}. " +
        "Output ONLY the translation, no explanations. " +
        "Keep part numbers, standards and units exactly as written.\n\n" +
        text;
}
