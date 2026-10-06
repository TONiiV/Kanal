using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class TrafficLightLayoutTests
{
    private const double NativeWidth = 14;
    private const double NativeHeight = 16;

    private static LightFrame Frame(int index, double bar = WorkspaceShellViewModel.HeaderHeight) =>
        TrafficLightLayout.Frame(index, bar, NativeWidth, NativeHeight);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EachLightIsCentredVerticallyInTheHeader(int index)
    {
        var frame = Frame(index);

        Assert.Equal(WorkspaceShellViewModel.HeaderHeight / 2, frame.Y + frame.Height / 2, precision: 3);
    }

    [Fact]
    public void TheLightsAreLargerThanTheNativeOnes()
    {
        var frame = Frame(0);

        Assert.True(frame.Width > NativeWidth);
        Assert.True(frame.Height > NativeHeight);
    }

    [Fact]
    public void TheLightsKeepOneEvenPitchFromLeftToRight()
    {
        var centres = new[] { 0, 1, 2 }.Select(i => Frame(i).X + Frame(i).Width / 2).ToArray();

        Assert.Equal(TrafficLightLayout.Pitch, centres[1] - centres[0], precision: 3);
        Assert.Equal(TrafficLightLayout.Pitch, centres[2] - centres[1], precision: 3);
    }

    [Fact]
    public void TheLightsStayInsideTheRoomTheHeadersReserve()
    {
        var last = Frame(2);

        Assert.True(Frame(0).X >= 0);
        Assert.True(last.X + last.Width <= WindowControlInsets.MacOs.Start);
    }
}
