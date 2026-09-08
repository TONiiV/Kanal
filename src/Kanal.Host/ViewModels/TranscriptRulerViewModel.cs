using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kanal.Core.Models;

namespace Kanal.Host.ViewModels;

public enum RulerTickKind
{
    Speaker,
    // Nothing produces Topic until the semantic layer (#103) lands; it reconciles into this
    // same tick list rather than a second strip.
    Topic,
}

public partial class RulerTickViewModel : ViewModelBase
{
    public RulerTickKind Kind { get; init; } = RulerTickKind.Speaker;

    [ObservableProperty]
    private string _anchorUtteranceId = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private string _speakerName = "";

    [ObservableProperty]
    private string _speakerColor = "#4C5C68";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private string _preview = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private string _timeLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoldBadge))]
    private int _turns = 1;

    [ObservableProperty]
    private int _utterances;

    [ObservableProperty]
    private bool _isCurrent;

    public string FoldBadge => Turns > 1 ? $"×{Turns}" : "";

    public string AccessibleName => $"{TimeLabel} · {SpeakerName} · {Preview}";
}

public sealed partial class TranscriptRulerViewModel : ViewModelBase
{
    public const int Slots = 72;
    public const double Pitch = 9;
    public const double CardHeight = 96;

    private const int PreviewLimit = 88;

    private readonly Func<string, (string Tag, string Name, string Color)> _resolve;
    private readonly List<Segment> _segments = new();
    private readonly Dictionary<string, Segment> _anchors = new();
    private readonly HashSet<string> _seen = new();
    private long? _originMs;

    public TranscriptRulerViewModel(Func<string, (string Tag, string Name, string Color)> resolve) =>
        _resolve = resolve;

    public ObservableCollection<RulerTickViewModel> Ticks { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewOpen))]
    private RulerTickViewModel? _hoveredTick;

    [ObservableProperty]
    private double _previewTop;

    public bool IsPreviewOpen => HoveredTick is not null;

    public event Action<string>? JumpRequested;

    public void Observe(Utterance u)
    {
        _originMs ??= u.TStartMs;

        if (_anchors.TryGetValue(u.Id, out var anchored))
        {
            anchored.Preview = Shorten(u.SrcText);
            anchored.StartMs = u.TStartMs;
            if (anchored.Utterances == 1)
                anchored.SpeakerTag = u.SpeakerTag;
            Sync();
            return;
        }

        // A translation upserts an utterance the room moved on from turns ago; only a first
        // sighting may open a turn, or every late arrival forks the ruler.
        if (!_seen.Add(u.Id))
        {
            Sync();
            return;
        }

        var canonical = _resolve(u.SpeakerTag).Tag;
        var last = _segments.Count > 0 ? _segments[^1] : null;
        if (last is not null && _resolve(last.SpeakerTag).Tag == canonical)
        {
            last.Utterances++;
        }
        else
        {
            var segment = new Segment
            {
                SpeakerTag = u.SpeakerTag,
                AnchorId = u.Id,
                StartMs = u.TStartMs,
                Preview = Shorten(u.SrcText),
                Utterances = 1,
            };
            _segments.Add(segment);
            _anchors[u.Id] = segment;
        }

        Sync();
    }

    public void Reresolve() => Sync();

    public void Hover(RulerTickViewModel? tick)
    {
        HoveredTick = tick;
        if (tick is null)
            return;

        var centre = Ticks.IndexOf(tick) * Pitch + Pitch / 2 - CardHeight / 2;
        PreviewTop = Math.Clamp(centre, 0, Slots * Pitch - CardHeight);
    }

    public void Jump(RulerTickViewModel tick) => JumpRequested?.Invoke(tick.AnchorUtteranceId);

    public void Clear()
    {
        _segments.Clear();
        _anchors.Clear();
        _seen.Clear();
        _originMs = null;
        HoveredTick = null;
        Ticks.Clear();
    }

    private void Sync()
    {
        var turns = Coalesce();
        var stride = Stride(turns.Count);

        var index = 0;
        for (var start = 0; start < turns.Count; start += stride, index++)
        {
            var group = turns.GetRange(start, Math.Min(stride, turns.Count - start));
            var tick = index < Ticks.Count ? Ticks[index] : Add();
            Write(tick, group);
            tick.IsCurrent = start + stride >= turns.Count;
        }

        while (Ticks.Count > index)
        {
            if (ReferenceEquals(HoveredTick, Ticks[^1]))
                HoveredTick = null;
            Ticks.RemoveAt(Ticks.Count - 1);
        }
    }

    private RulerTickViewModel Add()
    {
        var tick = new RulerTickViewModel { Kind = RulerTickKind.Speaker };
        Ticks.Add(tick);
        return tick;
    }

    private void Write(RulerTickViewModel tick, List<Turn> group)
    {
        var head = group[0];
        var speakers = group.Select(t => t.Tag).Distinct().ToList();

        tick.AnchorUtteranceId = head.AnchorId;
        tick.Preview = head.Preview;
        tick.TimeLabel = Offset(head.StartMs - (_originMs ?? 0));
        tick.Turns = group.Count;
        tick.Utterances = group.Sum(t => t.Utterances);
        tick.SpeakerName = string.Join(", ", speakers.Take(3).Select(t => _resolve(t).Name)) +
            (speakers.Count > 3 ? $", +{speakers.Count - 3}" : "");
        // A mark standing for several people's turns identifies nobody, so it spends no hue.
        tick.SpeakerColor = speakers.Count == 1 ? _resolve(speakers[0]).Color : "#7C8A93";
    }

    private List<Turn> Coalesce()
    {
        var turns = new List<Turn>(_segments.Count);
        foreach (var segment in _segments)
        {
            var tag = _resolve(segment.SpeakerTag).Tag;
            // Merges are non-destructive, so both tags survive in history; without this the
            // ruler shows one person handing over to themselves.
            if (turns.Count > 0 && turns[^1].Tag == tag)
            {
                turns[^1].Utterances += segment.Utterances;
                continue;
            }

            turns.Add(new Turn
            {
                Tag = tag,
                AnchorId = segment.AnchorId,
                StartMs = segment.StartMs,
                Preview = segment.Preview,
                Utterances = segment.Utterances,
            });
        }

        return turns;
    }

    private static int Stride(int turns)
    {
        var stride = 1;
        while ((turns + stride - 1) / stride > Slots)
            stride *= 2;
        return stride;
    }

    private static string Offset(long ms)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private static string Shorten(string text)
    {
        var line = text.Trim();
        return line.Length <= PreviewLimit ? line : line[..PreviewLimit].TrimEnd() + "…";
    }

    private sealed class Segment
    {
        public required string SpeakerTag { get; set; }
        public required string AnchorId { get; init; }
        public required long StartMs { get; set; }
        public required string Preview { get; set; }
        public required int Utterances { get; set; }
    }

    private sealed class Turn
    {
        public required string Tag { get; init; }
        public required string AnchorId { get; init; }
        public required long StartMs { get; init; }
        public required string Preview { get; init; }
        public required int Utterances { get; set; }
    }
}
