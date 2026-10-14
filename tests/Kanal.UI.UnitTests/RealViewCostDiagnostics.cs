using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
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

    private double Run(string name, Action<Window, MeetingRoomView, IReadOnlyList<ItemsControl>>? prepare, bool chinese = true)
    {
        var vm = TestViewModels.Hermetic();
        foreach (var code in new[] { "zh", "de", "pl" })
            vm.Columns.Add(new ColumnViewModel(code));
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Settle(window);
        var room = window.GetLogicalDescendants().OfType<MeetingRoomView>().Single();
        var lists = room.GetVisualDescendants().OfType<ItemsControl>().Where(i => i.Classes.Contains("bubbles")).ToList();
        prepare?.Invoke(window, room, lists);
        Settle(window);

        var clock = Stopwatch.StartNew();
        for (var n = 0; n < Utterances; n++)
            foreach (var column in vm.Columns)
            {
                var bubble = column.GetOrAdd($"u{n}");
                bubble.SpeakerName = "S01";
                bubble.SpeakerColor = "#3366CC";
                bubble.SourceLang = "ZH";
                bubble.Text = chinese
                    ? $"第{n}条：这家公司相当于景江酒店的服务公司，然后还有一些他的几个彩都是通过合作伙伴完成的。"
                    : $"Point {n}: this company is more or less the service company of the Jingjiang hotel, and some of the colours go through partners.";
                bubble.IsPartial = false;
            }

        Settle(window);
        var perBubble = clock.Elapsed.TotalMilliseconds / (Utterances * 3);
        Report($"METRIC real view  {name,-44} {perBubble,6:F2} ms/bubble ({lists.Count} lists)");
        window.Close();
        return perBubble;
    }

    [AvaloniaFact]
    public void WhatMakesABubbleCostMoreInTheRealWindow()
    {
        Run("warm-up", null);
        Run("font as shipped, Chinese text", null);
        Run("font as shipped, Latin text", null, chinese: false);
        Run("default font, Chinese text", (w, _, _) => w.FontFamily = Avalonia.Media.FontFamily.Default);
        Run("default font, Latin text", (w, _, _) => w.FontFamily = Avalonia.Media.FontFamily.Default, chinese: false);
        Run("only 'Microsoft YaHei', Chinese text", (w, _, _) => w.FontFamily = new Avalonia.Media.FontFamily("Microsoft YaHei"));
        Run("no view-level styles, Chinese text", (_, room, _) => room.Styles.Clear());
    }

    [AvaloniaFact]
    public void WhichOfTheDeclaredFontsDoesThisMachineReallyHave()
    {
        var current = (Avalonia.Media.FontFamily)Avalonia.Application.Current!.Resources["Sans"]!;
        Report($"METRIC fonts  stack in use: {string.Join(" | ", current.FamilyNames)}");
        var systemNames = Avalonia.Media.FontManager.Current.SystemFonts.Select(f => f.Name).ToList();
        Report($"METRIC fonts  system families: {systemNames.Count}, first: {string.Join(" | ", systemNames.Take(12))}");
        foreach (var name in new[]
                 {
                     "Helvetica Neue", "Helvetica", "Segoe UI Variable Text", "Segoe UI", "Arial",
                     "Microsoft YaHei", "PingFang SC", "No Such Font Family",
                 })
        {
            var found = Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(
                new Avalonia.Media.Typeface(name), out var glyphs);
            var listed = systemNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            Report($"METRIC fonts  {name,-24} tryGet={found,-5} gives='{glyphs?.FamilyName}' listed={listed}");
        }
    }
}
