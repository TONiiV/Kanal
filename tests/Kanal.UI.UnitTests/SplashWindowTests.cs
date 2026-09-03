using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class SplashWindowTests
{
    [AvaloniaFact]
    public void SplashUsesTheReadmeTaglineAndLoadsTheAppIcon()
    {
        var window = new SplashWindow();
        window.Show();

        var texts = window.GetLogicalDescendants().OfType<TextBlock>()
            .Select(text => text.Text)
            .ToArray();

        Assert.Contains("Kanal", texts);
        Assert.Contains("One room. Every language.", texts);
        Assert.NotNull(window.Icon);

        window.Close();
    }
}
