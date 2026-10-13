using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Kanal.Host;

public static class FontStack
{
    // A family that is not installed still costs a lookup in every text measure, on every bubble.
    public static FontFamily Resolve(FontFamily declared) => Resolve(declared, IsInstalled);

    public static FontFamily Resolve(FontFamily declared, Func<string, bool> isInstalled)
    {
        IReadOnlyList<string> names = [.. declared.FamilyNames];
        var kept = names.Where(isInstalled).ToList();
        if (kept.Count == names.Count)
            return declared;

        return kept.Count == 0 ? FontFamily.Default : new FontFamily(string.Join(", ", kept));
    }

    private static bool IsInstalled(string name) =>
        FontManager.Current.TryGetGlyphTypeface(new Typeface(name), out _);
}
