using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private string _speakerColor = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private string _preview = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private string _timeLabel = "";

    [ObservableProperty]
    private double _pitch = TranscriptRulerViewModel.MaxPitch;

    [ObservableProperty]
    private bool _isCurrent;

    public string AccessibleName => $"{TimeLabel} · {SpeakerName} · {Preview}";
}

public sealed partial class TranscriptRulerViewModel : ViewModelBase
{
    public const double MaxPitch = 9;
    public const double DefaultStripHeight = 648;
    public const double CardHeight = 96;

    private const int PreviewLimit = 88;

    private readonly Func<string, (string Tag, string Name, string Color)> _resolve;
    private readonly Dictionary<string, Entry> _entries = new();
    private long? _originMs;
    private int _reserved;
    private double _appliedPitch = double.NaN;

    public TranscriptRulerViewModel(Func<string, (string Tag, string Name, string Color)> resolve) =>
        _resolve = resolve;

    public ObservableCollection<RulerTickViewModel> Ticks { get; } = new();

    public double StripHeight { get; private set; } = DefaultStripHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewOpen))]
    private RulerTickViewModel? _hoveredTick;

    [ObservableProperty]
    private double _previewTop;

    public bool IsPreviewOpen => HoveredTick is not null;

    public event Action<string>? JumpRequested;

    public void Observe(Utterance u)
    {
        if (_entries.TryGetValue(u.Id, out var entry))
        {
            entry.SpeakerTag = u.SpeakerTag;
            entry.Tick.Preview = Shorten(u.SrcText);
            entry.Tick.TimeLabel = Offset(u.TStartMs - _originMs.GetValueOrDefault());
            Write(entry);
            return;
        }

        if (u.State != UtteranceState.Final)
            return;

        _originMs ??= u.TStartMs;
        var tick = new RulerTickViewModel
        {
            AnchorUtteranceId = u.Id,
            Preview = Shorten(u.SrcText),
            TimeLabel = Offset(u.TStartMs - _originMs.Value),
        };
        var added = new Entry { SpeakerTag = u.SpeakerTag, Tick = tick };
        _entries[u.Id] = added;
        Write(added);
        Ticks.Add(tick);
        var pitch = Pitch();
        if (Ticks.Count > 1 && pitch == _appliedPitch)
        {
            Ticks[^2].IsCurrent = false;
            tick.Pitch = pitch;
            tick.IsCurrent = true;
        }
        else
            Layout();
    }

    public void Reserve(int total) => _reserved = total;

    public void Release()
    {
        _reserved = 0;
        if (Ticks.Count > 0)
            Layout();
    }

    public void Reresolve()
    {
        foreach (var entry in _entries.Values)
            Write(entry);
    }

    public void SetStripHeight(double height)
    {
        StripHeight = Math.Max(0, height);
        if (Ticks.Count > 0)
            Layout();
    }

    public void Hover(RulerTickViewModel? tick)
    {
        HoveredTick = tick;
        if (tick is null)
            return;

        var centre = Ticks.IndexOf(tick) * tick.Pitch + tick.Pitch / 2 - CardHeight / 2;
        PreviewTop = Math.Clamp(centre, 0, Math.Max(0, StripHeight - CardHeight));
    }

    public void Jump(RulerTickViewModel tick) => JumpRequested?.Invoke(tick.AnchorUtteranceId);

    public void Clear()
    {
        _entries.Clear();
        _originMs = null;
        _reserved = 0;
        _appliedPitch = double.NaN;
        HoveredTick = null;
        Ticks.Clear();
    }

    private double Pitch() => Math.Min(MaxPitch, StripHeight / Math.Max(Ticks.Count, _reserved));

    private void Layout()
    {
        var pitch = Pitch();
        _appliedPitch = pitch;
        foreach (var tick in Ticks)
        {
            tick.Pitch = pitch;
            tick.IsCurrent = ReferenceEquals(tick, Ticks[^1]);
        }
    }

    private void Write(Entry entry)
    {
        var (_, name, color) = _resolve(entry.SpeakerTag);
        entry.Tick.SpeakerName = name;
        entry.Tick.SpeakerColor = color;
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

    private sealed class Entry
    {
        public required string SpeakerTag { get; set; }
        public required RulerTickViewModel Tick { get; init; }
    }
}
