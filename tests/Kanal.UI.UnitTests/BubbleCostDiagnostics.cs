using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Kanal.Host.Converters;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

// Measurement only. It names the part of the bubble template that costs the most per instance.
public class BubbleCostDiagnostics(ITestOutputHelper output)
{
    private const int Count = 300;

    [Flags]
    private enum Feature
    {
        None = 0,
        Bindings = 1,
        Flyout = 2,
        ToolTips = 4,
        MultiBinding = 8,
        SelectableText = 16,
    }

    private static Binding Localised(string key) =>
        new($"[{key}]") { Source = Localizer.Instance, Mode = BindingMode.OneWay };

    private static Control Build(BubbleViewModel bubble, Feature features, MenuFlyout menu)
    {
        TextBlock Label(string property)
        {
            var label = new TextBlock { FontSize = 11 };
            label.Classes.Add("who");
            if (features.HasFlag(Feature.Bindings))
                label.Bind(TextBlock.TextProperty, new Binding(property));
            else
                label.Text = "S01";
            return label;
        }

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var who = Label(nameof(BubbleViewModel.SpeakerName));
        if (features.HasFlag(Feature.MultiBinding))
            who.Bind(TextBlock.ForegroundProperty, new MultiBinding
            {
                Converter = SpeakerTones.Name,
                Bindings = { new Binding(nameof(BubbleViewModel.SpeakerColor)), new Binding(nameof(BubbleViewModel.SpeakerName)) },
            });
        head.Children.Add(who);
        head.Children.Add(Label(nameof(BubbleViewModel.SourceLang)));
        var original = Label(nameof(BubbleViewModel.SourceLang));
        if (features.HasFlag(Feature.ToolTips))
            original.Bind(ToolTip.TipProperty, Localised("column.original.tip"));
        head.Children.Add(original);
        var codeSwitch = Label(nameof(BubbleViewModel.SourceLang));
        if (features.HasFlag(Feature.ToolTips))
            codeSwitch.Bind(ToolTip.TipProperty, Localised("column.codeswitch.tip"));
        head.Children.Add(codeSwitch);

        TextBlock Body()
        {
            var text = features.HasFlag(Feature.SelectableText) ? new SelectableTextBlock() : new TextBlock();
            text.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            text.FontSize = 15;
            text.LineHeight = 28;
            if (features.HasFlag(Feature.Bindings))
                text.Bind(TextBlock.TextProperty, new Binding(nameof(BubbleViewModel.Text)));
            else
                text.Text = "这家公司相当于景江酒店的服务公司，然后还有一些他的几个彩都是通过合作伙伴完成的。";
            if (features.HasFlag(Feature.Flyout))
                text.ContextFlyout = menu;
            return text;
        }

        var source = new Border
        {
            BorderThickness = new Avalonia.Thickness(2, 0, 0, 0),
            Padding = new Avalonia.Thickness(11, 0, 0, 0),
            Child = Body(),
        };
        var column = new StackPanel { Spacing = 5, Children = { head, Body(), source } };
        var row = new Border
        {
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            Padding = new Avalonia.Thickness(0, 0, 0, 19),
            Margin = new Avalonia.Thickness(0, 0, 0, 19),
            Child = column,
        };
        row.Classes.Add("rec");
        if (features.HasFlag(Feature.Flyout))
            row.ContextFlyout = menu;
        return row;
    }

    private static async Task<double> Time(Window window, Feature features)
    {
        var menu = new MenuFlyout { Items = { new MenuItem { Header = "a" }, new MenuItem { Header = "b" } } };
        var items = new ObservableCollection<BubbleViewModel>(
            Enumerable.Range(0, Count).Select(n => new BubbleViewModel
            {
                UtteranceId = $"u{n}",
                SpeakerName = "S01",
                SpeakerColor = "#3366CC",
                SourceLang = "ZH",
                Text = "这家公司相当于景江酒店的服务公司，然后还有一些他的几个彩都是通过合作伙伴完成的。",
            }));
        var list = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<BubbleViewModel>((bubble, _) => Build(bubble!, features, menu)),
        };
        window.Content = new ScrollViewer { Content = list };
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var clock = Stopwatch.StartNew();
        list.ItemsSource = items;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        await Task.Yield();
        return clock.Elapsed.TotalMilliseconds / Count;
    }

    [AvaloniaFact]
    public async Task WhichPartOfTheBubbleCostsTheMost()
    {
        var window = new Window { Width = 600, Height = 800 };
        window.Show();

        await Time(window, Feature.None);
        var variants = new (string Name, Feature Features)[]
        {
            ("plain controls", Feature.None),
            ("plain controls again", Feature.None),
            ("+ data bindings", Feature.Bindings),
            ("+ shared context flyout", Feature.Flyout),
            ("+ localised tooltips", Feature.ToolTips),
            ("+ multibinding converter", Feature.MultiBinding),
            ("+ selectable text", Feature.SelectableText),
            ("everything", Feature.Bindings | Feature.Flyout | Feature.ToolTips | Feature.MultiBinding | Feature.SelectableText),
        };
        foreach (var (name, features) in variants)
        {
            var perBubble = await Time(window, features);
            var line = $"METRIC bubble cost  {name,-28} {perBubble,7:F2} ms/bubble";
            output.WriteLine(line);
            Console.WriteLine(line);
        }

        window.Close();
    }
}
