using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Platform;
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

        Assert.Contains("kanal", texts);
        Assert.Contains("One room. Every language.", texts);
        Assert.NotNull(window.Icon);

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("kanal.ico")]
    [InlineData("kanal-dark.ico")]
    public void BothWindowIconsLoadAsAppResources(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Kanal.Host/Assets/{name}"));

        Assert.NotNull(new WindowIcon(stream));
    }

    [AvaloniaTheory]
    [InlineData("kanal-splash-mark.png")]
    [InlineData("kanal-splash-mark-dark.png")]
    public void BothSplashMarksLoadAsAppResources(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Kanal.Host/Assets/{name}"));

        var header = new byte[24];
        stream.ReadExactly(header);

        Assert.Equal(512, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)));
        Assert.Equal(512, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
    }
}
