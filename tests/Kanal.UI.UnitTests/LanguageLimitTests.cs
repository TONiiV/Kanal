using Avalonia.Headless.XUnit;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The PRD freezes the host at four language columns. The selection has to enforce that itself:
/// a fifth ticked language used to be dropped silently at Start while still costing a translation
/// target — and Gladia translates targets sequentially, so it cost latency too.
/// </summary>
public class LanguageLimitTests
{
    private static LanguageOption Option(MainViewModel vm, string code) =>
        vm.LanguageOptions.First(o => o.Code == code);

    /// <summary>Fills the selection to the cap: zh/de/pl are selected out of the box.</summary>
    private static MainViewModel AtTheCap()
    {
        var vm = TestViewModels.Demo();
        Option(vm, "en").IsSelected = true;
        Assert.Equal(MainViewModel.MaxLanguages, vm.SelectedLanguages.Count);
        return vm;
    }

    [AvaloniaFact]
    public void FifthSelectionIsRefusedAndSaysWhy()
    {
        var vm = AtTheCap();

        var fifth = Option(vm, "fr");
        fifth.IsSelected = true;

        Assert.False(fifth.IsSelected);
        Assert.Equal(MainViewModel.MaxLanguages, vm.SelectedLanguages.Count);
        Assert.DoesNotContain(vm.SelectedLanguages, o => o.Code == "fr");
        Assert.Contains("four columns maximum", vm.LanguageLimitNotice, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void UnselectedOptionsAreUnselectableAtTheCapAndFreedAgain()
    {
        var vm = AtTheCap();

        Assert.All(vm.LanguageOptions.Where(o => !o.IsSelected), o => Assert.False(o.IsSelectable));
        Assert.All(vm.LanguageOptions.Where(o => o.IsSelected), o => Assert.True(o.IsSelectable));

        Option(vm, "zh").IsSelected = false;

        Assert.All(vm.LanguageOptions, o => Assert.True(o.IsSelectable));
        Assert.Equal("", vm.LanguageLimitNotice);
    }

    [AvaloniaFact]
    public void AddingByIsoCodeIsRefusedAtTheCap()
    {
        var vm = AtTheCap();

        vm.NewLanguageInput = "tr";
        vm.AddLanguageCommand.Execute(null);

        Assert.Equal(MainViewModel.MaxLanguages, vm.SelectedLanguages.Count);
        Assert.DoesNotContain(vm.SelectedLanguages, o => o.Code == "tr");
        Assert.Contains("four columns maximum", vm.LanguageLimitNotice, StringComparison.OrdinalIgnoreCase);
        // the typed code survives the refusal — retyping it after deselecting is not the operator's job
        Assert.Equal("tr", vm.NewLanguageInput);
    }

    [AvaloniaFact]
    public void AddingByIsoCodeStillWorksBelowTheCap()
    {
        var vm = TestViewModels.Demo();

        vm.NewLanguageInput = "tr";
        vm.AddLanguageCommand.Execute(null);

        Assert.Contains(vm.SelectedLanguages, o => o.Code == "tr");
        Assert.Equal("", vm.NewLanguageInput);
    }

    /// <summary>The cap and the column loop read the same constant, so they cannot disagree.</summary>
    [AvaloniaFact]
    public async Task StartNeverBuildsMoreColumnsThanTheCap()
    {
        var vm = TestViewModels.Demo();
        foreach (var option in vm.LanguageOptions)
            option.IsSelected = true; // every language in the catalog is attempted

        Assert.Equal(MainViewModel.MaxLanguages, vm.SelectedLanguages.Count);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(MainViewModel.MaxLanguages, vm.Columns.Count);
        Assert.Equal(
            vm.SelectedLanguages.Select(o => o.Code),
            vm.Columns.Select(c => c.Language));

        await vm.StopCommand.ExecuteAsync(null);
    }
}
