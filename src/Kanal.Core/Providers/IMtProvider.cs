using Kanal.Core.Models;

namespace Kanal.Core.Providers;

public interface IMtProvider
{
    string Id { get; }

    /// <summary>
    /// Translate <paramref name="text"/> from <paramref name="from"/> into each language
    /// in <paramref name="to"/>. Context carries recent finals for terminology consistency.
    /// Returns a map of target language → translated text.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        string text,
        string from,
        IReadOnlyList<string> to,
        IReadOnlyList<Utterance> context,
        CancellationToken ct);
}
