using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

// Measurement only. It finds what makes a bubble cost more in the real window than in a bare list.
public class RealViewCostDiagnostics(ITestOutputHelper output)
{
    private const int Utterances = 300;

    private void Report(string line)
    {
        output.WriteLine(line);
        Console.WriteLine(line);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private double Run(string name, Action<MeetingRoomView, IReadOnlyList<ItemsControl>>? prepare)
    {
        var vm = TestViewModels.Hermetic();
        foreach (var code in new[] { "zh", "de", "pl" })
            vm.Columns.Add(new ColumnViewModel(code));
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Settle(window);
        var room = window.GetLogicalDescendants().OfType<MeetingRoomView>().Single();
        var lists = room.GetVisualDescendants().OfType<ItemsControl>().Where(i => i.Classes.Contains("bubbles")).ToList();
        prepare?.Invoke(room, lists);
        Settle(window);

        var clock = Stopwatch.StartNew();
        for (var n = 0; n < Utterances; n++)
            foreach (var column in vm.Columns)
            {
                var bubble = column.GetOrAdd($"u{n}");
                bubble.SpeakerName = "S01";
                bubble.SpeakerColor = "#3366CC";
                bubble.SourceLang = "ZH";
                bubble.Text = $"第{n}条：这家公司相当于景江酒店的服务公司，然后还有一些他的几个彩都是通过合作伙伴完成的。";
                bubble.IsPartial = false;
            }

        Settle(window);
        var perBubble = clock.Elapsed.TotalMilliseconds / (Utterances * 3);
        Report($"METRIC real view  {name,-44} {perBubble,6:F2} ms/bubble ({lists.Count} lists)");
        window.Close();
        return perBubble;
    }

    private static void DropStyles(MeetingRoomView room, Func<string, bool> drop)
    {
        foreach (var style in room.Styles.OfType<Style>().ToList())
            if (drop(style.Selector?.ToString() ?? ""))
                room.Styles.Remove(style);
    }

    [AvaloniaFact]
    public void WhatMakesABubbleCostMoreInTheRealWindow()
    {
        Run("warm-up", null);
        Run("as shipped", null);
        Run("as shipped, again", null);
        Run("no view-level styles", (room, _) => room.Styles.Clear());
        Run("no styles with a space (descendant)", (room, _) => DropStyles(room, s => s.Contains(' ')));
        Run("no 'Panel.focused' styles", (room, _) => DropStyles(room, s => s.Contains("focused")));
        Run("template of the C# replica", (_, lists) =>
        {
            var menu = new MenuFlyout();
            foreach (var list in lists)
                list.ItemTemplate = new FuncDataTemplate<BubbleViewModel>((bubble, _) =>
                    BubbleCostDiagnostics.Build(bubble!,
                        BubbleCostDiagnostics.Feature.Bindings | BubbleCostDiagnostics.Feature.Flyout |
                        BubbleCostDiagnostics.Feature.ToolTips | BubbleCostDiagnostics.Feature.MultiBinding |
                        BubbleCostDiagnostics.Feature.SelectableText, menu));
        });
        Run("replica template and no view-level styles", (room, lists) =>
        {
            room.Styles.Clear();
            var menu = new MenuFlyout();
            foreach (var list in lists)
                list.ItemTemplate = new FuncDataTemplate<BubbleViewModel>((bubble, _) =>
                    BubbleCostDiagnostics.Build(bubble!, BubbleCostDiagnostics.Feature.Bindings, menu));
        });
    }
}
