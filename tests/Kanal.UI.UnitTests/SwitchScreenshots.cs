using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Core.Models;
using Kanal.Core.Room;
using Kanal.Core.Workspaces;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class SwitchScreenshots : IDisposable
{
    private const int Rows = 45;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kanal-shots-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static readonly string[] Zh =
    [
        "然后冷恋啊相关", "然后还有一些他的几彩都是通过合作伙伴完成的。", "这家公司相当于景江酒店的服务公司。",
        "我们需要确认交期和公差，这个零件编号是KX-4402。", "下周三之前可以把样品寄到波兰。",
    ];

    private static readonly string[] De =
    [
        "Und dann kalte Liebe, verwandt.", "Und dann gibt es noch einige Farben, die über Partner entstehen.",
        "Diese Firma ist gewissermaßen die Dienstleistungsgesellschaft des Hotels.",
        "Wir müssen Liefertermin und Toleranz bestätigen, die Teilenummer ist KX-4402.",
        "Bis nächsten Mittwoch können die Muster nach Polen geschickt werden.",
    ];

    private static readonly string[] Pl =
    [
        "A potem zimna miłość, powiązana.", "A potem jest jeszcze kilka kolorów, które powstają przez partnerów.",
        "Ta firma jest w zasadzie spółką usługową hotelu.",
        "Musimy potwierdzić termin dostawy i tolerancję, numer części to KX-4402.",
        "Do środy przyszłego tygodnia próbki można wysłać do Polski.",
    ];

    private static Utterance Said(string id, int n) =>
        new($"{id}{n}", n % 2 == 0 ? "S01" : "S02", n * 4000L, n * 4000L + 3500, "zh", Zh[n % Zh.Length],
            1, UtteranceState.Final, false, 1.0,
            new Dictionary<string, string>
            {
                ["zh"] = Zh[n % Zh.Length], ["de"] = De[n % De.Length], ["pl"] = Pl[n % Pl.Length],
            });

    private static MeetingRecord Stored(WorkspaceStore store, Workspace workspace, string title, string id)
    {
        var record = store.CreateMeeting(workspace.Id, title).Meeting!;
        var path = Path.Combine(store.MeetingFolder(workspace.Id, record.Id)!, TranscriptLog.FileName);
        using (var log = new TranscriptLogWriter(path, _ => { }))
        {
            for (var n = 0; n < Rows; n++)
                log.Append(Said(id, n));
        }

        return store.SaveMeeting(record with
        {
            StartedAt = new DateTimeOffset(2026, 10, 10, 8, 7, 0, TimeSpan.Zero),
            TranscriptPath = path,
            Languages = ["zh", "de", "pl"],
        }).Meeting!;
    }

    private sealed class HeldYields
    {
        private readonly Queue<TaskCompletionSource> _held = new();

        public int Count => _held.Count;

        public Task Hold()
        {
            var turn = new TaskCompletionSource();
            _held.Enqueue(turn);
            return turn.Task;
        }

        public void Release(Action settle)
        {
            _held.Dequeue().SetResult();
            settle();
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Shoot(Window window, string name)
    {
        Settle(window);
        var dir = Environment.GetEnvironmentVariable("KANAL_SHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name));
    }

    [AvaloniaFact]
    [Trait("Category", "Screenshot")]
    public void TheSwitchFromALongMeetingToAnother()
    {
        if (Environment.GetEnvironmentVariable("KANAL_SKIA") != "1")
            return;

        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        var workspace = store.CreateWorkspace("DimenX", folder).Workspace!;
        var first = Stored(store, workspace, "DimenX 2026-10-10 08:07", "a");
        var second = Stored(store, workspace, "景江集团合作洽谈", "b");
        var yields = new HeldYields();
        var vm = TestViewModels.Hermetic(workspaces: () => store, yieldToUi: yields.Hold);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Settle(window);

        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Id == first.Id);
        while (yields.Count > 0)
            yields.Release(() => Settle(window));
        Shoot(window, "1-a-long-meeting-is-open.png");

        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single(m => m.Id == second.Id);
        Settle(window);
        Shoot(window, "2-after-the-click-body-hidden.png");

        var hidden = window.GetVisualDescendants().OfType<ItemsControl>().Where(i => i.Classes.Contains("loading")).ToList();
        foreach (var list in hidden)
            list.Classes.Set("loading", false);
        Shoot(window, "3-before-the-old-body-stays-under-the-loading-text.png");
        foreach (var list in hidden)
            list.Classes.Set("loading", true);

        for (var n = 0; n < 6 && yields.Count > 0; n++)
            yields.Release(() => Settle(window));
        Shoot(window, "4-the-new-meeting-is-filling.png");

        while (yields.Count > 0)
            yields.Release(() => Settle(window));
        Shoot(window, "5-the-new-meeting-is-complete.png");

        window.Close();
    }
}
