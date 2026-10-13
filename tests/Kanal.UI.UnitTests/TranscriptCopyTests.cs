using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Core.Models;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class TranscriptCopyTests
{
    private sealed class FakeClipboard
    {
        public List<string> Written { get; } = [];

        public Task Write(string text)
        {
            Written.Add(text);
            return Task.CompletedTask;
        }
    }

    private static (BubbleViewModel Bubble, FakeClipboard Clipboard) Spoken()
    {
        var clipboard = new FakeClipboard();
        var bubble = new ColumnViewModel("de", clipboard.Write).GetOrAdd("u1");
        bubble.SpeakerName = "Anna";
        bubble.Text = "Das Bauteil passt nicht.";
        bubble.SourceText = "零件不合适。";
        bubble.StartMs = 3_723_000;
        return (bubble, clipboard);
    }

    [AvaloniaFact]
    public void CopyTakesTheSelectionWhenThereIsOne()
    {
        var (bubble, clipboard) = Spoken();
        bubble.SelectedText = "Bauteil";

        bubble.CopyCommand.Execute(null);

        Assert.Equal(["Bauteil"], clipboard.Written);
    }

    [AvaloniaFact]
    public void CopyTakesTheSelectionOfTheSourceLineToo()
    {
        var (bubble, clipboard) = Spoken();
        bubble.SelectedSourceText = "零件";

        bubble.CopyCommand.Execute(null);

        Assert.Equal(["零件"], clipboard.Written);
    }

    [AvaloniaFact]
    public void CopyTakesTheWholeLineWhenNothingIsSelected()
    {
        var (bubble, clipboard) = Spoken();

        bubble.CopyCommand.Execute(null);

        Assert.Equal(["Das Bauteil passt nicht."], clipboard.Written);
    }

    [AvaloniaFact]
    public void CopyWithSpeakerAndTimeWritesTheColumnLanguageLine()
    {
        var (bubble, clipboard) = Spoken();
        bubble.SelectedText = "Bauteil";

        bubble.CopyWithSpeakerCommand.Execute(null);

        Assert.Equal(["[01:02:03] Anna: Das Bauteil passt nicht."], clipboard.Written);
    }

    [AvaloniaFact]
    public void ABrowsedTranscriptCopiesThroughTheSameClipboard()
    {
        var clipboard = new FakeClipboard();
        var utterances = new[]
        {
            new Utterance("u1", "s1", 65_000, 66_000, "zh", "零件不合适。", 1, UtteranceState.Final, false, 1.0,
                new Dictionary<string, string> { ["de"] = "Das Bauteil passt nicht." }),
        };

        var columns = StoredTranscript.Columns(utterances, ["zh", "de"], clipboard.Write);
        columns[1].Bubbles[0].CopyWithSpeakerCommand.Execute(null);

        Assert.Equal(["[00:01:05] s1: Das Bauteil passt nicht."], clipboard.Written);
    }

    [AvaloniaFact]
    public void ALiveRoomCopiesThroughTheClipboardSeamOfTheViewModel()
    {
        var clipboard = new FakeClipboard();
        var vm = TestViewModels.Hermetic();
        vm.WriteClipboard = clipboard.Write;
        vm.Columns.Add(new ColumnViewModel("de", vm.WriteClipboard));
        var bubble = vm.Columns[0].GetOrAdd("u1");
        bubble.Text = "Hallo";

        bubble.CopyCommand.Execute(null);

        Assert.Equal(["Hallo"], clipboard.Written);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static (Window Window, List<SelectableTextBlock> Blocks) Shown()
    {
        var vm = TestViewModels.Hermetic();
        vm.Columns.Add(new ColumnViewModel("de"));
        var bubble = vm.Columns[0].GetOrAdd("u1");
        bubble.Text = "Das Bauteil passt nicht.";
        bubble.SourceText = "零件不合适。";
        bubble.IsPartial = false;

        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Settle(window);
        var blocks = window.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        return (window, blocks);
    }

    [AvaloniaFact]
    public void TheTranslationAndTheSourceLineAreSelectable()
    {
        var (window, blocks) = Shown();

        Assert.Contains(blocks, b => b.Text == "Das Bauteil passt nicht.");
        Assert.Contains(blocks, b => b.Text == "零件不合适。");
        window.Close();
    }

    [AvaloniaFact]
    public void EveryLineCarriesTheSharedMenuWithTheCopyItems()
    {
        var (window, blocks) = Shown();
        Assert.Equal(2, blocks.Count);

        foreach (var block in blocks)
        {
            var flyout = Assert.IsType<MenuFlyout>(block.ContextFlyout);
            var items = flyout.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["CopyLine", "CopyWithSpeaker"], items.Select(i => i.Name));
            Assert.Equal(
                [Localizer.Instance["transcript.copy"], Localizer.Instance["transcript.copy.speaker"]],
                items.Select(i => i.Header as string));
        }

        Assert.Same(blocks[0].ContextFlyout, blocks[1].ContextFlyout);
        window.Close();
    }

    [AvaloniaFact]
    public void SelectingInALineReachesTheUtteranceViewModel()
    {
        var (window, blocks) = Shown();
        var translation = blocks.Single(b => b.Text == "Das Bauteil passt nicht.");
        var source = blocks.Single(b => b.Text == "零件不合适。");
        var bubble = Assert.IsType<BubbleViewModel>(translation.DataContext);

        translation.SelectionStart = 4;
        translation.SelectionEnd = 11;
        source.SelectionStart = 0;
        source.SelectionEnd = 2;
        Settle(window);

        Assert.Equal("Bauteil", bubble.SelectedText);
        Assert.Equal("零件", bubble.SelectedSourceText);
        window.Close();
    }

    [AvaloniaFact]
    public void CtrlASelectsTheWholeFocusedLine()
    {
        var (window, blocks) = Shown();
        var translation = blocks.Single(b => b.Text == "Das Bauteil passt nicht.");

        translation.Focus();
        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        Settle(window);

        Assert.Equal("Das Bauteil passt nicht.", translation.SelectedText);
        window.Close();
    }
}
