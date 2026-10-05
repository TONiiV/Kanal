using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Kanal.Host.Controls;

// A right-aligned StackPanel still starts at x=0 once it overflows and so clips its inner end;
// this one stays pinned to its right edge and loses its outer (left) end instead.
public sealed class EndAlignedStack : StackPanel
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Orientation != Orientation.Horizontal)
            return base.ArrangeOverride(finalSize);

        var shown = Children.Where(child => child.IsVisible).ToList();
        var total = shown.Sum(child => child.DesiredSize.Width) + Spacing * System.Math.Max(0, shown.Count - 1);
        var x = finalSize.Width - total;

        foreach (var child in shown)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }

        return finalSize;
    }
}
