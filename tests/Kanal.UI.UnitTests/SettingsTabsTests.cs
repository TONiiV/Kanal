using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class SettingsTabsTests
{
    private static readonly string[] EverySettingThatExisted =
    [
        "AppLanguage", "Version", "Changelog", "Licenses",
        "ColourScheme", "AccentTheme",
        "InputDevice", "InputTest",
        "ApiKeys", "NewKeyName", "NewKeyValue", "AddKey",
        "TranslationModels",
        "TranscriptFolder", "RecordAudio", "RecordOnlineAudio",
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
        Assert.Equal(7, items.Count);
        Assert.All(items, tab => Assert.False(
            string.IsNullOrWhiteSpace(AutomationProperties.GetName(tab)),
            $"tab {items.IndexOf(tab)} has no accessible name"));
        Assert.All(items, tab => Assert.True(tab.Focusable, $"{AutomationProperties.GetName(tab)} cannot be reached by keyboard"));

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
    public void AppearanceIsTheSecondTabAndShowsTheColourSchemeCards()
    {
        var window = new SettingsWindow(new SettingsViewModel(new AppSettings(), () => null));
        window.Show();

        var tabs = Tabs(window);
        var appearance = tabs.Items.OfType<TabItem>().ElementAt(1);
        Assert.Equal(Localizer.Instance["settings.tab.appearance"], AutomationProperties.GetName(appearance));

        tabs.SelectedItem = appearance;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(OnScreen(window), name => name == "ColourScheme");
        var cards = window.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(item => item.DataContext is ColourSchemeOption)
            .ToList();
        Assert.Equal(3, cards.Count);
        Assert.All(cards, card => Assert.Equal(
            ((ColourSchemeOption)card.DataContext!).Name,
            ControlAutomationPeer.CreatePeerForElement(card).GetName()));

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ColourScheme")]
    [InlineData("AccentTheme")]
    public void ACtrlClickOnTheSelectedCardKeepsTheSelection(string list)
    {
        var vm = new SettingsViewModel(new AppSettings(), () => null);
        var window = new SettingsWindow(vm);
        window.Show();
        var tabs = Tabs(window);
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().ElementAt(1);
        Dispatcher.UIThread.RunJobs();
        var cards = window.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == list);
        var selected = cards.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.IsSelected);
        var centre = selected.TranslatePoint(new Point(selected.Bounds.Width / 2, selected.Bounds.Height / 2), window)!.Value;

        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.True(cards.SelectedIndex >= 0);
        Assert.NotNull(vm.Scheme);
        Assert.NotNull(vm.Theme);
        vm.ApplyTo(new AppSettings());
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

    [AvaloniaFact]
    public void EveryTabCarriesAnIconOfItsOwnBesideItsName()
    {
        var window = new SettingsWindow(new SettingsViewModel());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = Tabs(window).Items.OfType<TabItem>().ToList();
        var icons = tabs.Select(tab => Assert.Single(
            Assert.IsAssignableFrom<Control>(tab.Header).GetLogicalDescendants()
                .OfType<Avalonia.Controls.Shapes.Path>(),
            path => path.Data is not null).Data).ToList();

        Assert.Equal(tabs.Count, icons.Distinct().Count());
        Assert.All(tabs, tab => Assert.Contains(
            Assert.IsAssignableFrom<Control>(tab.Header).GetLogicalDescendants().OfType<TextBlock>(),
            text => text.Text == AutomationProperties.GetName(tab)));

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheIconAndTheNameOfATabShareOneColourInEveryState(bool selected, bool hovered)
    {
        var window = new SettingsWindow(new SettingsViewModel());
        window.Show();

        var tabs = Tabs(window);
        var items = tabs.Items.OfType<TabItem>().ToList();
        var tab = selected ? items[1] : items[0];
        tabs.SelectedItem = selected ? tab : items[2];
        ((IPseudoClasses)tab.Classes).Set(":pointerover", hovered);
        Dispatcher.UIThread.RunJobs();

        var header = Assert.IsAssignableFrom<Control>(tab.Header).GetLogicalDescendants();
        var icon = Assert.IsAssignableFrom<ISolidColorBrush>(header.OfType<Avalonia.Controls.Shapes.Path>().Single().Fill);
        var name = Assert.IsAssignableFrom<ISolidColorBrush>(header.OfType<TextBlock>().Single().Foreground);

        Assert.Equal(icon.Color, name.Color);

        window.Close();
    }
}
