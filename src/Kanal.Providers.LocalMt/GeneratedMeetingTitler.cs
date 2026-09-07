using System.Text.RegularExpressions;
using Kanal.Core.Meetings;

namespace Kanal.Providers.LocalMt;

public sealed partial class GeneratedMeetingTitler(ITextGenerator generator) : IMeetingTitler
{
    private const int MaxLines = 40;
    private const int MaxWords = 8;

    [GeneratedRegex(@"^\s*(Title|Titel|Tytuł|标题|主题)\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TitleLabel();

    public async Task<string?> SuggestAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        var opening = lines.Count > MaxLines ? lines.Take(MaxLines) : lines;
        var raw = await generator.GenerateAsync(Prompt(string.Join("\n", opening)), ct).ConfigureAwait(false);

        var text = MtOutputCleaner.Clean(raw);
        var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        first = MtOutputCleaner.Clean(TitleLabel().Replace(first, "")).TrimEnd('.', '。');

        // A model that answered with a sentence did not understand the question; a
        // sentence cut to eight words names the meeting something it is not.
        return first.Length > 0 && first.Split(' ').Length <= MaxWords ? first : null;
    }

    private static string Prompt(string transcript) =>
        "Give this meeting a short title of at most six words, in the language it is spoken in. " +
        "Output ONLY the title, no explanation, no quotes.\n\n" +
        transcript;
}
