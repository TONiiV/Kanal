using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Kanal.Host.Localization;

/// <summary>
/// Every string the operator reads, in the four languages the host is offered in — loaded from
/// <c>Localization/i18n/{code}.json</c>, one file per language, embedded in the assembly.
/// English is the source of truth; a test asserts the other three carry exactly the same keys,
/// so a string added to one screen cannot quietly go missing on three others.
/// </summary>
/// <remarks>
/// Flat JSON files rather than RESX or C# dictionaries: the four languages diff side by side,
/// a translator needs no compiler, and nothing has to be regenerated to add a string. The files
/// are embedded resources, so the executable still ships as a single file and a missing table is
/// a build error, not a runtime surprise. The register is the one <c>.impeccable.md</c> asks for
/// in every language — terse and factual, an instrument rather than a product. Where a string
/// tells the operator what went wrong it also says what to do about it, in all four.
/// </remarks>
public static class Strings
{
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; } = Load();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var assembly = typeof(Strings).Assembly;
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var language in Localizer.Available)
        {
            var name = $"Kanal.Host.Localization.i18n.{language.Code}.json";
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded language table {name}.");
            tables[language.Code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException($"Language table {name} is empty.");
        }

        return tables;
    }
}
