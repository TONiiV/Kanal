using Avalonia.Media;
using Kanal.Host;

namespace Kanal.UI.UnitTests;

public class FontStackTests
{
    private static readonly FontFamily Declared =
        new("Helvetica Neue, Helvetica, Segoe UI, Arial, Microsoft YaHei, PingFang SC");

    [Fact]
    public void FamiliesThatAreNotInstalledAreDroppedAndTheOrderStays()
    {
        var installed = new HashSet<string> { "Segoe UI", "Arial", "Microsoft YaHei" };

        var resolved = FontStack.Resolve(Declared, installed.Contains);

        Assert.Equal(["Segoe UI", "Arial", "Microsoft YaHei"], resolved.FamilyNames);
    }

    [Fact]
    public void AStackWhereEverythingIsInstalledIsLeftAlone()
    {
        var resolved = FontStack.Resolve(Declared, _ => true);

        Assert.Same(Declared, resolved);
    }

    [Fact]
    public void AStackWhereNothingIsInstalledFallsBackToTheDefaultFont()
    {
        var resolved = FontStack.Resolve(Declared, _ => false);

        Assert.Equal(FontFamily.Default, resolved);
    }
}
