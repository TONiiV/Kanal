using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class MainWindowCompositionTests
{
    [AvaloniaFact]
    public void MainWindowComposesTheFourNamedRegionsWithoutAWordmark()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.Single(window.GetLogicalDescendants().OfType<IconBarView>());
        Assert.Single(window.GetLogicalDescendants().OfType<MeetingRoomView>());
        Assert.Single(window.GetLogicalDescendants().OfType<SidePanelView>());
        Assert.Single(window.GetLogicalDescendants().OfType<StatusBarView>());

        var iconBar = window.GetLogicalDescendants().OfType<IconBarView>().Single();
        Assert.DoesNotContain(
            iconBar.GetLogicalDescendants().OfType<TextBlock>(),
            text => string.Equals(text.Text, "KANAL", StringComparison.Ordinal));

        var toolbarScroller = Assert.Single(
            iconBar.GetLogicalDescendants().OfType<ScrollViewer>(),
            scroller => scroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto);
        Assert.Equal(ScrollBarVisibility.Disabled, toolbarScroller.VerticalScrollBarVisibility);

        var buttons = iconBar.GetLogicalDescendants().OfType<Button>().ToList();
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.StartCommand));
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.PauseCommand));
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.StopCommand));
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.ExportMarkdownCommand));

        window.Close();
    }
}
