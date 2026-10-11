using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

public class StorePathsTests
{
    private static readonly string MeetingFolder = Path.Combine(Path.GetTempPath(), "kanal-paths", "meetings", "abc");

    [Theory]
    [InlineData("0a1b-2c_3D", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("zażółć", false)]
    public void A_folder_name_is_ascii_letters_digits_dash_and_underscore(string? id, bool expected) =>
        Assert.Equal(expected, StorePaths.IsFolderName(id));

    [Theory]
    [InlineData(null, true)]
    [InlineData("transcript.jsonl", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b.md", false)]
    [InlineData("a\\b.md", false)]
    public void A_file_name_is_absent_or_one_plain_name(string? name, bool expected) =>
        Assert.Equal(expected, StorePaths.IsFileName(name));

    [Fact]
    public void An_artifact_path_names_a_file_directly_inside_the_meeting_folder()
    {
        Assert.True(StorePaths.IsArtifactPath(null, MeetingFolder));
        Assert.True(StorePaths.IsArtifactPath(Path.Combine(MeetingFolder, "audio.wav"), MeetingFolder));
        Assert.False(StorePaths.IsArtifactPath("audio.wav", MeetingFolder));
        Assert.False(StorePaths.IsArtifactPath(Path.Combine(MeetingFolder, "sub", "audio.wav"), MeetingFolder));
        Assert.False(StorePaths.IsArtifactPath(Path.Combine(MeetingFolder, "..", "audio.wav"), MeetingFolder));
    }

    [Fact]
    public void A_canonical_path_drops_the_trailing_separator()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kanal-paths-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(StorePaths.Canonical(folder), StorePaths.Canonical(folder + Path.DirectorySeparatorChar));
    }
}
