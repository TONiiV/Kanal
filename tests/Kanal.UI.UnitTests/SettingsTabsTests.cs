using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class SettingsTabsTests
{
    private static readonly string[] EverySettingThatExisted =
    [
        "AppLanguage", "Version", "Changelog", "Licenses",
        "InputDevice", "InputTest",
        "ApiKeys", "NewKeyName", "NewKeyValue", "AddKey",
        "TranslationModels",
        "TranscriptFolder", "AudioFolder", "RecordAudio", "RecordOnlineAudio",
        "LogLevel", "LogSize", "OpenLogFolder",
    ];

    private static TabControl Tabs(Window window) =>
        Assert.Single(window.GetLogicalDescendants().OfType<TabControl>());

    private static IEnumerable<string> OnScreen(Window window) =>
        window.GetVisualDescendants().OfType<Control>()
            .Where(control => !string.IsNullOrEmpty(control.Name))
            .Select(control => control.Name!);

    private static IEnumerable<Control> Inside(TabItem tab) =>
        tab.Content is Control content
            ? content.GetLogicalDescendants().OfType<Control>()
            : [];

    [AvaloniaFact]
    public void TheWindowIsVerticalTabsRatherThanOneLongScroll()
    {
        var window = new SettingsWindow(new SettingsViewModel());
        window.Show();

        var tabs = Tabs(window);
        Assert.Equal(Dock.Left, tabs.TabStripPlacement);

        var items = tabs.Items.OfType<TabItem>().ToList();
        Assert.Equal(6, items.Count);
        Assert.All(items, tab => Assert.False(
            string.IsNullOrWhiteSpace(AutomationProperties.GetName(tab)),
            $"a tab has no accessible name: {tab.Header}"));
        Assert.All(items, tab => Assert.True(tab.Focusable, $"{tab.Header} cannot be reached by keyboard"));

        window.Close();
    }

    [AvaloniaFact]
    public void NoSettingWasDroppedInTheReorganisation()
    {
        var window = new SettingsWindow(new SettingsViewModel());
        window.Show();

        var homes = Tabs(window).Items.OfType<TabItem>()
            .SelectMany(tab => Inside(tab)
                .Where(control => !string.IsNullOrEmpty(control.Name))
                .Select(control => (Name: control.Name!, Tab: tab)))
            .ToLookup(found => found.Name, found => found.Tab);

        Assert.All(EverySettingThatExisted, name =>
            Assert.True(homes[name].Count() == 1, $"'{name}' has {homes[name].Count()} homes, not one"));

        window.Close();
    }

    [AvaloniaFact]
    public void SelectingATabPutsItsPaneOnScreenAndTakesTheLastOneOff()
    {
        var window = new SettingsWindow(new SettingsViewModel());
        window.Show();

        var tabs = Tabs(window);
        var audio = tabs.Items.OfType<TabItem>()
            .Single(tab => Inside(tab).Any(control => control.Name == "InputDevice"));
        var workspace = tabs.Items.OfType<TabItem>()
            .Single(tab => Inside(tab).Any(control => control.Name == "LogLevel"));

        // Every pane stays a logical child of its tab whether or not it is selected, so only the
        // visual tree answers the question the operator is asking.
        tabs.SelectedItem = audio;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(OnScreen(window), name => name == "InputDevice");
        Assert.DoesNotContain(OnScreen(window), name => name == "LogLevel");

        tabs.SelectedItem = workspace;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(OnScreen(window), name => name == "LogLevel");
        Assert.DoesNotContain(OnScreen(window), name => name == "InputDevice");

        window.Close();
    }

    [AvaloniaFact]
    public void APaneForSomethingUnbuiltSaysSoInsteadOfOfferingDeadControls()
    {
        var window = new SettingsWindow(new SettingsViewModel());
        window.Show();

        var unbuilt = Tabs(window).Items.OfType<TabItem>()
            .Where(tab => !Inside(tab).Any(control =>
                control is Button or ComboBox or TextBox or CheckBox or NumericUpDown or RadioButton))
            .ToList();

        Assert.NotEmpty(unbuilt);
        Assert.All(unbuilt, tab => Assert.Contains(
            Inside(tab).OfType<TextBlock>(),
            text => !string.IsNullOrWhiteSpace(text.Text)));

        window.Close();
    }
}
