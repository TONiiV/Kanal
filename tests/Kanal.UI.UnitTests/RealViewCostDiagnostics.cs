using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

// Measurement only. It splits the cost of a long meeting in the real window: bubbles against ruler ticks.
public class RealViewCostDiagnostics(ITestOutputHelper output)
{
    private const int Utterances = 300;

    private void Report(string line)
    {
        output.WriteLine(line);
        Console.WriteLine(line);
    }

    private static Utterance Said(int n) =>
        new($"u{n}", "S01", n * 4000L, n * 4000L + 3500, "zh", $"第{n}条：这家公司相当于景江酒店的服务公司。",
            1, UtteranceState.Final, false, 1.0, new Dictionary<string, string>());

    private static double Settle(Window window)
    {
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return clock.Elapsed.TotalMilliseconds;
    }

    [AvaloniaFact]
    public void BubblesAgainstRulerTicksInTheRealWindow()
    {
        var vm = TestViewModels.Hermetic();
        foreach (var code in new[] { "zh", "de", "pl" })
            vm.Columns.Add(new ColumnViewModel(code));
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
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

        var bubbleAddMs = clock.Elapsed.TotalMilliseconds;
        var bubbleLayoutMs = Settle(window);
        Report($"METRIC real view  bubbles only   x{Utterances * 3}: add={bubbleAddMs,7:F0} ms layout={bubbleLayoutMs,7:F0} ms " +
               $"-> {(bubbleAddMs + bubbleLayoutMs) / (Utterances * 3),6:F2} ms/bubble");

        clock.Restart();
        for (var n = 0; n < Utterances; n++)
            vm.Ruler.Observe(Said(n));
        var tickAddMs = clock.Elapsed.TotalMilliseconds;
        var tickLayoutMs = Settle(window);
        Report($"METRIC real view  ruler ticks   x{Utterances}: add={tickAddMs,7:F0} ms layout={tickLayoutMs,7:F0} ms " +
               $"-> {(tickAddMs + tickLayoutMs) / Utterances,6:F2} ms/tick");

        window.Close();
    }
}
