using Kanal.Core.Providers;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class TranslationKeySettingsTests
{
    [Fact]
    public void TheStoredKeyIsShownAndSavedAgain()
    {
        var settings = new AppSettings();
        settings.ApiKeys.Add(new ApiKeyEntry("DeepL", "deepl", "d:fx"));
        var vm = new SettingsViewModel(settings, () => null);
        Assert.Equal("d:fx", vm.DeepLKey);

        vm.DeepLKey = "  other  ";
        var saved = new AppSettings();
        vm.ApplyTo(saved);

        var entry = Assert.Single(saved.ApiKeys, k => k.Provider == "deepl");
        Assert.Equal("other", entry.Key);
    }

    [Fact]
    public void ABlankKeyRemovesTheStoredEntryAndLeavesGladiaAlone()
    {
        var settings = new AppSettings();
        settings.ApiKeys.Add(new ApiKeyEntry("DeepL", "deepl", "d:fx"));
        settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "g"));
        var vm = new SettingsViewModel(settings, () => null) { DeepLKey = " " };

        vm.ApplyTo(settings);

        Assert.DoesNotContain(settings.ApiKeys, k => k.Provider == "deepl");
        Assert.Contains(settings.ApiKeys, k => k.Provider == "gladia" && k.Key == "g");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public void EveryTranslationErrorCodeHasAMessageInEveryLanguage(string code)
    {
        foreach (var errorCode in new[] { MtProviderException.QuotaExceeded, MtProviderException.InvalidKey, MtProviderException.Unavailable })
            Assert.False(string.IsNullOrWhiteSpace(Strings.Tables[code]["error." + errorCode]));
    }
}
