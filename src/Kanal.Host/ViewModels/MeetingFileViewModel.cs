using System.Globalization;
using Avalonia;

namespace Kanal.Host.ViewModels;

public sealed class MeetingFileViewModel
{
    private const long Kilo = 1024;
    private const long Mega = Kilo * 1024;
    private const long Giga = Mega * 1024;

    private MeetingFileViewModel(string name, int depth, bool isFolder, long length)
    {
        Name = name;
        Depth = depth;
        IsFolder = isFolder;
        Length = length;
    }

    public static MeetingFileViewModel Folder(string name, int depth) =>
        new(name, depth, isFolder: true, length: -1);

    public static MeetingFileViewModel File(string name, int depth, long length) =>
        new(name, depth, isFolder: false, length);

    public string Name { get; }

    public int Depth { get; }

    public bool IsFolder { get; }

    public long Length { get; }

    public string Label => IsFolder ? Name + "/" : Name;

    public Thickness Nesting => new(Depth * 15, 0, 0, 0);

    public string SizeLabel => IsFolder || Length < 0 ? "" : Size(Length);

    private static string Size(long bytes) => bytes switch
    {
        < Kilo => Written(bytes, 1, "B"),
        < Mega => Written(bytes, Kilo, "kB"),
        < Giga => Written(bytes, Mega, "MB"),
        _ => Written(bytes, Giga, "GB"),
    };

    private static string Written(long bytes, long unit, string suffix) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)unit:0.#} {suffix}");
}
