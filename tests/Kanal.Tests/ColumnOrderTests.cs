using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.Tests;

/// <summary>
/// Column order is host-local presentation: the operator drags the language they need in front of
/// them, mid-meeting, and nothing about that reaches the phones. What must hold is that no
/// utterance is lost or re-rendered into the wrong language, and that the flag stack never
/// disagrees with the columns.
/// </summary>
public class ColumnOrderTests
{
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static Border ColumnHead(MainWindow window, ColumnViewModel column) =>
        window.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("colhead") && ReferenceEquals(b.DataContext, column));

    [AvaloniaFact]
    public async Task MovingAColumnReordersColumnsAndFlagStackWithoutLosingBubbles()
    {
        var vm = TestViewModels.Demo();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Columns.All(c => c.Bubbles.Count > 0));

        Assert.Equal(["zh", "de", "pl"], vm.Columns.Select(c => c.Language));
        var moved = vm.Columns[1];
        var bubbles = moved.Bubbles.ToList();

        vm.MoveColumn(1, 0);

        Assert.Equal(["de", "zh", "pl"], vm.Columns.Select(c => c.Language));
        // the flag stack must never disagree with the columns
        Assert.Equal(["de", "zh", "pl"], vm.SelectedLanguages.Select(o => o.Code));
        Assert.Equal("DE · ZH · PL", vm.SelectedLanguageSummary);

        // the column object itself moved: nothing was re-created, so nothing was lost
        Assert.Same(moved, vm.Columns[0]);
        Assert.Equal(bubbles, vm.Columns[0].Bubbles);

        // still live: new utterances keep landing in the right column, once each
        Assert.True(vm.IsRunning);
        var mark = vm.Columns[0].Bubbles.Count;
        await WaitForAsync(() => vm.Columns[0].Bubbles.Count > mark);
        Assert.All(vm.Columns, c => Assert.Equal(
            c.Bubbles.Count, c.Bubbles.Select(b => b.UtteranceId).Distinct().Count()));
        // a bubble marked ORIGINAL was spoken in its own column's language
        Assert.All(vm.Columns, c => Assert.All(
            c.Bubbles.Where(b => b.IsTranscript), b => Assert.Equal(c.Code, b.SourceLang)));

        await vm.StopCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OrderSurvivesIntoTheNextSession()
    {
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);
        vm.MoveColumn(2, 0);
        await vm.StopCommand.ExecuteAsync(null);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(["pl", "zh", "de"], vm.Columns.Select(c => c.Language));
        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>A language selected after a reorder joins at the end, and the order holds.</summary>
    [AvaloniaFact]
    public async Task ReorderThenSelectKeepsTheOperatorsOrder()
    {
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);
        vm.MoveColumn(0, 2);
        await vm.StopCommand.ExecuteAsync(null);

        vm.LanguageOptions.First(o => o.Code == "en").IsSelected = true;

        Assert.Equal(["de", "pl", "zh", "en"], vm.SelectedLanguages.Select(o => o.Code));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(["de", "pl", "zh", "en"], vm.Columns.Select(c => c.Language));
        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>The drop target is a rule on one edge of one column — never two, never left behind.</summary>
    [AvaloniaFact]
    public async Task DropTargetRuleTracksThePointerAndClearsOnDrop()
    {
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);

        vm.BeginColumnDrag(0);
        vm.UpdateColumnDropTarget(2, before: false);

        Assert.True(vm.Columns[2].IsDropAfter);
        Assert.False(vm.Columns[2].IsDropBefore);
        Assert.Single(vm.Columns, c => c.IsDropBefore || c.IsDropAfter);

        vm.DropColumn(2, before: false);

        Assert.Equal(["de", "pl", "zh"], vm.Columns.Select(c => c.Language));
        Assert.All(vm.Columns, c => Assert.False(c.IsDropBefore || c.IsDropAfter));

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task CancelledDragLeavesTheOrderAlone()
    {
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);

        vm.BeginColumnDrag(0);
        vm.UpdateColumnDropTarget(1, before: true);
        vm.CancelColumnDrag();

        Assert.Equal(["zh", "de", "pl"], vm.Columns.Select(c => c.Language));
        Assert.All(vm.Columns, c => Assert.False(c.IsDropBefore || c.IsDropAfter));

        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Keyboard route to the same operation, for the trackpad-in-a-meeting case: focus a column
    /// head, Alt+→ walks it right. This is the one reorder path a headless run can drive
    /// end-to-end — a real drag needs a platform drag source, which the headless platform has not.
    /// </summary>
    [AvaloniaFact]
    public async Task AltArrowMovesTheFocusedColumn()
    {
        var vm = TestViewModels.Demo();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Columns.All(c => c.Bubbles.Count > 0));

        var head = ColumnHead(window, vm.Columns[0]);
        head.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Right, RawInputModifiers.Alt, PhysicalKey.ArrowRight, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["de", "zh", "pl"], vm.Columns.Select(c => c.Language));
        Assert.Equal(["de", "zh", "pl"], vm.SelectedLanguages.Select(o => o.Code));

        // and back
        window.KeyPress(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["zh", "de", "pl"], vm.Columns.Select(c => c.Language));

        await vm.StopCommand.ExecuteAsync(null);
        window.Close();
    }

    /// <summary>
    /// The pointer half of the drag handler: pressing and dragging a column head must reach
    /// <c>DragDrop.DoDragDrop</c> without throwing and without disturbing the running session.
    /// Headless registers no platform drag source, so the gesture completes as a no-op — which is
    /// exactly the regression worth pinning, since the handler runs on every press in the header.
    /// </summary>
    [AvaloniaFact]
    public async Task DraggingAColumnHeadIsHarmlessWithoutAPlatformDragSource()
    {
        var vm = TestViewModels.Demo();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Columns.All(c => c.Bubbles.Count > 0));

        var head = ColumnHead(window, vm.Columns[0]);
        var origin = head.TranslatePoint(new Point(6, 6), window)!.Value;

        window.MouseDown(origin, MouseButton.Left);
        window.MouseMove(origin + new Vector(40, 0));
        window.MouseUp(origin + new Vector(40, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsRunning);
        Assert.Equal(["zh", "de", "pl"], vm.Columns.Select(c => c.Language));
        Assert.All(vm.Columns, c => Assert.False(c.IsDropBefore || c.IsDropAfter));

        await vm.StopCommand.ExecuteAsync(null);
        window.Close();
    }
}
