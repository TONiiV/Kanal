using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Kanal.Host;

public sealed record AccentVariant(
    string Brand, string Hover, string Pressed, string Wash, string OnBrand,
    string RampA, string RampB, string RampC)
{
    public Color BrandColour => Color.Parse(Brand);
}

public sealed record AccentTheme(string Id, AccentVariant Light, AccentVariant Dark)
{
    public AccentVariant Of(bool dark) => dark ? Dark : Light;
}

public static class AccentThemes
{
    public static readonly AccentTheme Kanal = new("kanal",
        new("#414B83", "#35406F", "#293359", "#EBEDF6", "#FFFFFF", "#535E96", "#6B75A8", "#252B3B"),
        new("#B9C2FF", "#CDD3FF", "#E0E4FF", "#303B5B", "#181C28", "#8D99E6", "#6F7BC4", "#545FA3"));

    public static readonly IReadOnlyList<AccentTheme> All =
    [
        Kanal,
        new("grove",
            new("#2F6B4F", "#265840", "#1C4430", "#E8F2EC", "#FFFFFF", "#447A61", "#63907B", "#252B3B"),
            new("#8FD3AE", "#A8E0C1", "#C0EBD2", "#1F3B30", "#14241B", "#71A58C", "#5F8A78", "#4E6E64")),
        new("ocean",
            new("#1F5F8F", "#184E78", "#123D5F", "#E6F0F7", "#FFFFFF", "#356F9A", "#5787AB", "#252B3B"),
            new("#8CC8F2", "#A6D6F6", "#C1E3FA", "#1E364D", "#0F2230", "#6F9DC0", "#5E83A1", "#4C6983")),
        new("iris",
            new("#6A3FA0", "#583385", "#45276A", "#F1EBF8", "#FFFFFF", "#7952AA", "#8F6FB8", "#252B3B"),
            new("#C9A9F5", "#D8BFF8", "#E5D3FB", "#3A2D55", "#1E1A30", "#9D86C2", "#8271A3", "#685B84")),
        new("graphite",
            new("#4A4F57", "#3B4047", "#2D3136", "#ECEDEF", "#FFFFFF", "#5C6168", "#777B81", "#252B3B"),
            new("#C4C8D0", "#D6D9DF", "#E6E8EC", "#343843", "#1B1B1D", "#999DA6", "#7F838D", "#656974")),
    ];

    public static AccentTheme Find(string? id) => All.FirstOrDefault(theme => theme.Id == id) ?? Kanal;
}
