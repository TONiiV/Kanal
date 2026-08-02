using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace Kanal.Host.Localization;

/// <summary>One language the host chrome can be shown in.</summary>
public sealed record AppLanguage(string Code, string NativeName);

/// <summary>
/// The host's own chrome, in the operator's language. Separate from the room's languages: the
/// person driving the laptop is often not one of the people the meeting is being translated for,
/// and a German buyer running a session between a Chinese supplier and a Polish contractor should
/// not have to read English labels to do it.
/// </summary>
/// <remarks>
/// An indexer on a singleton rather than generated resource classes: switching language has to
/// take effect on a window that is already open — mid-meeting, without restarting a room — and
/// raising <see cref="Binding.IndexerName"/> makes every bound string re-read at once. Missing
/// keys fall back to English and then to the key itself, so a gap shows up as a visible
/// identifier rather than as a blank control.
/// </remarks>
public sealed class Localizer : INotifyPropertyChanged
{
    public const string Fallback = "en";

    public static Localizer Instance { get; } = new();

    private string _current = Fallback;

    public static IReadOnlyList<AppLanguage> Available { get; } =
    [
        new("en", "English"),
        new("zh", "中文"),
        new("de", "Deutsch"),
        new("pl", "Polski"),
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>ISO code of the language the chrome is currently in.</summary>
    public string Current
    {
        get => _current;
        set
        {
            var resolved = Available.Any(l => l.Code == value) ? value : Fallback;
            if (resolved == _current)
                return;

            _current = resolved;
            // "Item[]" is the indexer's binding name: every {l:T …} in every open window re-reads
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        }
    }

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (Strings.Tables.TryGetValue(_current, out var table) && table.TryGetValue(key, out var text))
            return text;
        if (Strings.Tables[Fallback].TryGetValue(key, out var english))
            return english;
        return key; // a gap should be visible as an identifier, never as a blank control
    }

    /// <summary>Composed strings — a path, a language name, a count — with the same fallback rules.</summary>
    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// The language to start in when nothing has been chosen: the operating system's, if the
    /// chrome has been translated into it, English otherwise.
    /// </summary>
    public static string FromSystem()
    {
        var os = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Available.Any(l => l.Code == os) ? os : Fallback;
    }
}
