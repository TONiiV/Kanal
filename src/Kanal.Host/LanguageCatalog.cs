using System.Collections.Generic;

namespace Kanal.Host;

/// <summary>
/// The one language table the host UI draws from: ISO code and the language's own name.
/// Order here is presentation order — chips, the flag stack and the column layout all follow it.
/// </summary>
public static class LanguageCatalog
{
    public static readonly IReadOnlyList<(string Code, string NativeName)> Known =
    [
        ("zh", "中文"),
        ("de", "Deutsch"),
        ("pl", "Polski"),
        ("en", "English"),
        ("fr", "Français"),
        ("es", "Español"),
        ("it", "Italiano"),
        ("cs", "Čeština"),
        ("uk", "Українська"),
        ("ru", "Русский"),
        ("ja", "日本語"),
        ("ko", "한국어"),
    ];

    public static string? NativeName(string code)
    {
        foreach (var (known, name) in Known)
        {
            if (string.Equals(known, code, System.StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return null;
    }
}
