using System.Buffers.Binary;

namespace Kanal.Core.UnitTests;

public class WindowsInstallerBitmapTests
{
    [Theory]
    [InlineData("Dialog.bmp", 493, 312)]
    [InlineData("Banner.bmp", 493, 58)]
    public void BitmapHasTheSizeWixUiDrawsItAt(string file, int width, int height)
    {
        var bytes = File.ReadAllBytes(Path.Combine(InstallerLayoutTests.FindRepoRoot(), "installers", "windows", file));

        Assert.Equal("BM", System.Text.Encoding.ASCII.GetString(bytes, 0, 2));
        Assert.Equal(width, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18)));
        Assert.Equal(height, Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22))));
    }
}
