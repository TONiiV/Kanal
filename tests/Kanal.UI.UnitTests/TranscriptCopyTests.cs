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
    public void CopyWritesTheTextTheMenuGivesIt()
    {
        var (bubble, clipboard) = Spoken();

        bubble.CopyCommand.Execute("Bauteil");

        Assert.Equal(["Bauteil"], clipboard.Written);
    }

    [AvaloniaFact]
    public void CopyWithSpeakerAndTimeWritesTheGivenTextBehindSpeakerAndTime()
    {
        var (bubble, clipboard) = Spoken();

        bubble.CopyWithSpeakerCommand.Execute("零件不合适。");

        Assert.Equal(["[01:02:03] Anna: 零件不合适。"], clipboard.Written);
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
        columns[1].Bubbles[0].CopyWithSpeakerCommand.Execute("Das Bauteil passt nicht.");

        Assert.Equal(["[00:01:05] s1: Das Bauteil passt nicht."], clipboard.Written);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static (Window Window, List<SelectableTextBlock> Blocks, FakeClipboard Clipboard) Shown()
    {
        var clipboard = new FakeClipboard();
        var vm = TestViewModels.Hermetic();
        vm.Columns.Add(new ColumnViewModel("de", clipboard.Write));
        var bubble = vm.Columns[0].GetOrAdd("u1");
        bubble.SpeakerName = "Anna";
        bubble.Text = "Das Bauteil passt nicht.";
        bubble.SourceText = "零件不合适。";
        bubble.IsPartial = false;

        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Settle(window);
        var blocks = window.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        return (window, blocks, clipboard);
    }

    [AvaloniaFact]
    public void TheTranslationAndTheSourceLineAreSelectable()
    {
        var (window, blocks, _) = Shown();

        Assert.Contains(blocks, b => b.Text == "Das Bauteil passt nicht.");
        Assert.Contains(blocks, b => b.Text == "零件不合适。");
        window.Close();
    }

    [AvaloniaFact]
    public void EveryLineCarriesTheSharedMenuWithTheCopyItems()
    {
        var (window, blocks, _) = Shown();
        Assert.Equal(2, blocks.Count);

        foreach (var block in blocks)
        {
            var flyout = Assert.IsType<MenuFlyout>(block.ContextFlyout);
            var items = flyout.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["CopyLine", "CopyWithSpeaker"], items.Take(2).Select(i => i.Name));
            Assert.Equal(
                [Localizer.Instance["transcript.copy"], Localizer.Instance["transcript.copy.speaker"]],
                items.Take(2).Select(i => i.Header as string));
        }

        Assert.Same(blocks[0].ContextFlyout, blocks[1].ContextFlyout);
        window.Close();
    }

    [AvaloniaFact]
    public void OpeningTheMenuOnALineBindsItsItemsToThatUtterance()
    {
        var (window, blocks, _) = Shown();
        var block = blocks[0];
        var bubble = Assert.IsType<BubbleViewModel>(block.DataContext);
        var flyout = Assert.IsType<MenuFlyout>(block.ContextFlyout);

        flyout.ShowAt(block);
        Settle(window);

        var items = flyout.Items.OfType<MenuItem>().ToList();
        Assert.Same(bubble.CopyCommand, items.Single(i => i.Name == "CopyLine").Command);
        Assert.Same(bubble.CopyWithSpeakerCommand, items.Single(i => i.Name == "CopyWithSpeaker").Command);
        flyout.Hide();
        window.Close();
    }

    private static List<MenuItem> OpenMenuOn(Window window, SelectableTextBlock line)
    {
        var flyout = Assert.IsType<MenuFlyout>(line.ContextFlyout);
        flyout.ShowAt(line);
        Settle(window);
        return flyout.Items.OfType<MenuItem>().ToList();
    }

    [AvaloniaFact]
    public void TheMenuGivesItsItemsTheSelectionOfTheLineItOpenedOn()
    {
        var (window, blocks, clipboard) = Shown();
        var translation = blocks.Single(b => b.Text == "Das Bauteil passt nicht.");
        translation.SelectionStart = 4;
        translation.SelectionEnd = 11;

        var items = OpenMenuOn(window, translation);
        var copy = items.Single(i => i.Name == "CopyLine");
        copy.Command!.Execute(copy.CommandParameter);

        Assert.Equal(["Bauteil"], clipboard.Written);
        window.Close();
    }

    [AvaloniaFact]
    public void TheMenuGivesItsItemsTheWholeTranslationLineWhenNothingIsSelected()
    {
        var (window, blocks, clipboard) = Shown();
        var translation = blocks.Single(b => b.Text == "Das Bauteil passt nicht.");

        var items = OpenMenuOn(window, translation);
        var copy = items.Single(i => i.Name == "CopyWithSpeaker");
        copy.Command!.Execute(copy.CommandParameter);

        Assert.Equal(["[00:00:00] Anna: Das Bauteil passt nicht."], clipboard.Written);
        window.Close();
    }

    [AvaloniaFact]
    public void TheMenuGivesItsItemsTheSourceLineWhenItOpenedOnTheSourceLine()
    {
        var (window, blocks, clipboard) = Shown();
        var source = blocks.Single(b => b.Text == "零件不合适。");

        var items = OpenMenuOn(window, source);
        var copy = items.Single(i => i.Name == "CopyLine");
        copy.Command!.Execute(copy.CommandParameter);

        Assert.Equal(["零件不合适。"], clipboard.Written);
        window.Close();
    }

    [AvaloniaFact]
    public void CtrlASelectsTheWholeFocusedLine()
    {
        var (window, blocks, _) = Shown();
        var translation = blocks.Single(b => b.Text == "Das Bauteil passt nicht.");

        translation.Focus();
        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        Settle(window);

        Assert.Equal("Das Bauteil passt nicht.", translation.SelectedText);
        window.Close();
    }
}
