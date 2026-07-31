using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.Tests;

/// <summary>
/// The host chrome in the operator's own language. Separate from the room's languages: the person
/// driving the laptop is often not one of the people the meeting is being translated for.
/// </summary>
public class LocalizationTests
{
    private static IReadOnlyDictionary<string, string> Table(string code) => Strings.Tables[code];

    [Fact]
    public void FourLanguagesAreOffered()
    {
        Assert.Equal(["en", "zh", "de", "pl"], Localizer.Available.Select(l => l.Code));
        Assert.All(Localizer.Available, l => Assert.False(string.IsNullOrWhiteSpace(l.NativeName)));
    }

    /// <summary>
    /// The guard that makes this maintainable: a string added to one screen cannot quietly go
    /// missing on three others. English is the source of truth; the rest must match it exactly.
    /// </summary>
    [Theory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public void EveryLanguageCarriesExactlyTheEnglishKeys(string code)
    {
        var english = Table("en").Keys.ToHashSet();
        var other = Table(code).Keys.ToHashSet();

        var missing = english.Except(other).Order().ToList();
        var extra = other.Except(english).Order().ToList();

        Assert.True(missing.Count == 0, $"{code} is missing: {string.Join(", ", missing)}");
        Assert.True(extra.Count == 0, $"{code} has keys English does not: {string.Join(", ", extra)}");
    }

    /// <summary>
    /// The handful of strings that genuinely are the same word in the target language — "Start"
    /// and "Pause" are ordinary German, "Start" is ordinary Polish. Exempted per language, not
    /// per key: a Chinese 开始 accidentally reverted to "Start" must still fail.
    /// </summary>
    private static readonly HashSet<(string Code, string Key)> SameWordInTargetLanguage =
    [
        ("de", "transport.start"),
        ("de", "transport.pause"),
        ("de", "export.button"),
        ("de", "column.original"),
        ("pl", "transport.start"),
    ];

    [Theory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public void NothingIsLeftUntranslated(string code)
    {
        var english = Table("en");
        var other = Table(code);

        foreach (var (key, text) in other)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{code}/{key} is blank.");
            // Everything not exempted above must actually differ, which is what catches a
            // string added to English and forgotten in the other three.
            if (SameWordInTargetLanguage.Contains((code, key)))
                continue;
            Assert.True(
                text != english[key],
                $"{code}/{key} is still the English string.");
        }
    }

    /// <summary>
    /// Two modes send audio out — CloudCloud and CloudLocal — so no language may present
    /// CloudCloud as the only one. The first translation did, in German and Polish, which
    /// inverts the one fact this tool exists to keep straight.
    /// </summary>
    [Fact]
    public void NoLanguageClaimsOnlyOneModeSendsAudioOut()
    {
        Assert.DoesNotContain("einzige", Table("de")["mode.cloudcloud.help"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jedyn", Table("pl")["mode.cloudcloud.help"], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A format string that loses a placeholder in translation loses the path, the model name or
    /// the decibel figure it was carrying — and does so silently, since string.Format ignores
    /// arguments it was not asked for.
    /// </summary>
    [Theory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public void PlaceholdersSurviveTranslation(string code)
    {
        var english = Table("en");
        var other = Table(code);

        foreach (var (key, text) in english)
        {
            var expected = Placeholders(text);
            var actual = Placeholders(other[key]);
            Assert.True(
                expected.SetEquals(actual),
                $"{code}/{key} carries {{{string.Join(",", actual.Order())}}}, English has {{{string.Join(",", expected.Order())}}}");
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>The unbranded rule holds in every language, not only the one it was written in.</summary>
    [Theory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    [InlineData("en")]
    public void ModeStringsNameNoVendorInAnyLanguage(string code)
    {
        var table = Table(code);
        foreach (var (key, text) in table.Where(e => e.Key.StartsWith("mode.") || e.Key.StartsWith("leaves.")))
        {
            foreach (var vendor in PipelineModeTests.VendorNames)
                Assert.False(
                    text.Contains(vendor, StringComparison.OrdinalIgnoreCase),
                    $"{code}/{key} names {vendor}.");
        }
    }

    [Fact]
    public void AMissingKeyFallsBackToEnglishThenToTheKeyItself()
    {
        var localizer = Localizer.Instance;
        var previous = localizer.Current;
        try
        {
            localizer.Current = "de";
            Assert.Equal(Table("de")["transport.stop"], localizer["transport.stop"]);

            // a key nothing defines shows up as an identifier, never as a blank control
            Assert.Equal("no.such.key", localizer["no.such.key"]);
        }
        finally
        {
            localizer.Current = previous;
        }
    }

    [Fact]
    public void AnUnknownLanguageFallsBackToEnglish()
    {
        var localizer = Localizer.Instance;
        var previous = localizer.Current;
        try
        {
            localizer.Current = "de";
            localizer.Current = "kl";
            Assert.Equal("en", localizer.Current);
        }
        finally
        {
            localizer.Current = previous;
        }
    }

    /// <summary>
    /// Switching has to reach windows that are already open — the operator changes it mid-meeting
    /// and the screen follows, without restarting a room.
    /// </summary>
    [Fact]
    public void SwitchingRaisesTheIndexerSoBoundTextRereads()
    {
        var localizer = Localizer.Instance;
        var previous = localizer.Current;
        var raised = new List<string?>();
        localizer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        try
        {
            localizer.Current = "pl";
            Assert.Contains("Item[]", raised);
        }
        finally
        {
            localizer.Current = previous;
        }
    }
}

/// <summary>The chosen language survives a restart, and the modes follow it while running.</summary>
public class AppLanguageTests
{
    [AvaloniaFact]
    public void SettingsRoundTripsTheChosenLanguage()
    {
        // Choosing in this view model applies to the whole application by design, so the test
        // has to put it back — without this it leaked into every test that ran afterwards.
        var previous = Localizer.Instance.Current;
        try
        {
            var settings = new AppSettings { AppLanguage = "pl" };
            var vm = new SettingsViewModel(settings, () => null);

            Assert.Equal("pl", vm.AppLanguage!.Code);

            vm.AppLanguage = Localizer.Available.First(l => l.Code == "de");
            var written = new AppSettings();
            vm.ApplyTo(written);

            Assert.Equal("de", written.AppLanguage);
        }
        finally
        {
            Localizer.Instance.Current = previous;
        }
    }

    /// <summary>Choosing it in the dialog takes effect at once, not on the next launch.</summary>
    [AvaloniaFact]
    public void ChoosingALanguageAppliesImmediately()
    {
        var previous = Localizer.Instance.Current;
        try
        {
            var vm = new SettingsViewModel(new AppSettings(), () => null);
            vm.AppLanguage = Localizer.Available.First(l => l.Code == "zh");

            Assert.Equal("zh", Localizer.Instance.Current);
        }
        finally
        {
            Localizer.Instance.Current = previous;
        }
    }

    /// <summary>
    /// The mode list is built once at construction; without re-reading, a language change left
    /// five English rows on an otherwise translated screen.
    /// </summary>
    [AvaloniaFact]
    public void ModeRowsFollowTheLanguage()
    {
        var previous = Localizer.Instance.Current;
        try
        {
            Localizer.Instance.Current = "en";
            var vm = TestViewModels.Hermetic();
            var english = vm.Modes[0].Name;

            Localizer.Instance.Current = "de";

            Assert.NotEqual(english, vm.Modes[0].Name);
            Assert.Equal(Strings.Tables["de"]["mode.demo.name"], vm.Modes[0].Name);
        }
        finally
        {
            Localizer.Instance.Current = previous;
        }
    }

    /// <summary>
    /// The switch happens inside the Settings window, so that window least of all may stay in
    /// the old language. Everything built at construction — the env-var note, the processing
    /// note, the folder note, the untested verdict, the model rows — has to follow the change,
    /// not wait for the dialog to be reopened.
    /// </summary>
    [AvaloniaFact]
    public void SwitchingLanguageRefreshesTheSettingsWindowItself()
    {
        var previous = Localizer.Instance.Current;
        try
        {
            Localizer.Instance.Current = "en";
            var vm = new SettingsViewModel(new AppSettings(), () => null);

            vm.AppLanguage = Localizer.Available.First(l => l.Code == "de");

            var de = Strings.Tables["de"];
            Assert.Equal(de["settings.input.note"], vm.ProcessingNote);
            Assert.Equal(de["mic.untested"], vm.VerdictLabel);
            Assert.Equal(de["mic.untested.detail"], vm.VerdictDetail);
            Assert.StartsWith("Ersatzweise:", vm.EnvFallback);
            Assert.Equal(
                string.Format(de["settings.files.default"], SettingsStore.DefaultOutputFolder),
                vm.DefaultFolderNote);
            Assert.Equal(de["settings.model.none"], vm.TranslationModels[0].DisplayName);
            Assert.Equal(de["settings.model.none.note"], vm.TranslationModels[0].MetaLabel);
        }
        finally
        {
            Localizer.Instance.Current = previous;
        }
    }

    /// <summary>
    /// The model rows were the one part of Settings still hard-coded in English: "None",
    /// "downloaded", "not downloaded" appeared verbatim on an otherwise translated screen.
    /// </summary>
    [AvaloniaFact]
    public void ModelRowsSpeakTheApplicationLanguage()
    {
        var previous = Localizer.Instance.Current;
        try
        {
            Localizer.Instance.Current = "zh";
            var vm = new SettingsViewModel(new AppSettings(), () => null);

            var zh = Strings.Tables["zh"];
            Assert.Equal(zh["settings.model.none"], vm.TranslationModels[0].DisplayName);
            Assert.Equal(zh["settings.model.none.note"], vm.TranslationModels[0].MetaLabel);

            // whichever download state this machine happens to be in, the label is Chinese
            var row = vm.TranslationModels[1];
            Assert.Contains(
                row.StatusLabel,
                new[] { zh["settings.model.downloaded"], zh["settings.model.notdownloaded"] });
        }
        finally
        {
            Localizer.Instance.Current = previous;
        }
    }
}
