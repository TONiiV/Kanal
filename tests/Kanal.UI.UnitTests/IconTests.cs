using Avalonia;
using Avalonia.Headless.XUnit;
using Kanal.Host.Controls;
using Kanal.Host.Services;

namespace Kanal.UI.UnitTests;

public class IconTests
{
    [AvaloniaFact]
    public void EveryIconOnDiskLoads()
    {
        Assert.NotEmpty(Icons.Names);
        Assert.All(Icons.Names, name => Assert.NotNull(Icons.Of(name)));
    }

    /// <summary>
    /// Avalonia's <c>Stretch="Uniform"</c> aligns the scaled geometry to the top-left of the
    /// control's box rather than centring it, so a glyph whose ink is narrower than its box sits
    /// off to one side. Padding every icon's bounds out to its own view box removes the slack:
    /// the box maps onto the view box exactly, and the ink lands where the SVG drew it.
    /// </summary>
    [AvaloniaFact]
    public void EveryIconMeasuresAsItsWholeViewBoxSoNothingDriftsOffCentre()
    {
        Assert.All(Icons.Names, name =>
            Assert.Equal(new Rect(0, 0, 16, 16), Icons.Of(name).Bounds));
    }

    [AvaloniaFact]
    public void EveryModeHasAMarkOfItsOwn()
    {
        var marks = Enum.GetValues<PipelineModeId>().Select(Icons.Mode).ToList();

        Assert.All(marks, Assert.NotNull);
        Assert.Equal(marks.Count, marks.Distinct().Count());
    }

    [AvaloniaFact]
    public void AnUnknownIconNameFailsLoudly() =>
        Assert.Throws<ArgumentException>(() => Icons.Of("no-such-icon"));
}
