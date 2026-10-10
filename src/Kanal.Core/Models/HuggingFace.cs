namespace Kanal.Core.Models;

public static class HuggingFace
{
    public static string FileUrl(string repo, string fileName) =>
        $"https://huggingface.co/{repo}/resolve/main/{fileName}";
}
