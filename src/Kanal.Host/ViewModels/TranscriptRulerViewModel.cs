using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Models;

namespace Kanal.Host.ViewModels;

public enum RulerTickKind
{
    Speaker,
    // Nothing produces Topic until the semantic layer (#103) lands; it reconciles into this
    // same tick list rather than a second strip.
    Topic,
}

public enum PageDirection
{
    Up,
    Down,
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
    private bool _isInView;

    [ObservableProperty]
    private double _reach = TranscriptRulerViewModel.RestReach;

    public string AccessibleName => $"{TimeLabel} · {SpeakerName} · {Preview}";
}

public sealed partial class TranscriptRulerViewModel : ViewModelBase
{
    public const double MaxPitch = 9;
    public const double DefaultStripHeight = 648;
    public const double CardHeight = 96;
    public const double RestReach = 10;
    public const double InViewReach = 16;

    private static readonly double[] HoverReach = [28, 21, 15];

    private const int PreviewLimit = 88;

    private readonly Func<string, (string Tag, string Name, string Color)> _resolve;
    private readonly Dictionary<string, Entry> _entries = new();
    private long? _originMs;
    private HashSet<string> _visible = new();
    private int _firstInView = -1;
    private int _lastInView = -1;

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

    public event Action<string, PageDirection>? PageRequested;

    public bool CanPageUp => _firstInView > 0;

    public bool CanPageDown => _lastInView >= 0 && _lastInView < Ticks.Count - 1;

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
        Layout();
        MarkInView();
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

    public void SetVisible(IEnumerable<string> utteranceIds)
    {
        _visible = new HashSet<string>(utteranceIds);
        MarkInView();
    }

    public void Hover(RulerTickViewModel? tick)
    {
        HoveredTick = tick;
        Magnify();
        if (tick is null)
            return;

        var centre = Ticks.IndexOf(tick) * tick.Pitch + tick.Pitch / 2 - CardHeight / 2;
        PreviewTop = Math.Clamp(centre, 0, Math.Max(0, StripHeight - CardHeight));
    }

    public void Jump(RulerTickViewModel tick) => JumpRequested?.Invoke(tick.AnchorUtteranceId);

    [RelayCommand(CanExecute = nameof(CanPageUp))]
    private void PageUp()
    {
        if (CanPageUp)
            PageRequested?.Invoke(Ticks[_firstInView - 1].AnchorUtteranceId, PageDirection.Up);
    }

    [RelayCommand(CanExecute = nameof(CanPageDown))]
    private void PageDown()
    {
        if (CanPageDown)
            PageRequested?.Invoke(Ticks[_lastInView + 1].AnchorUtteranceId, PageDirection.Down);
    }

    public void Clear()
    {
        _entries.Clear();
        _originMs = null;
        _visible = new HashSet<string>();
        HoveredTick = null;
        Ticks.Clear();
        MarkInView();
    }

    private void Layout()
    {
        var pitch = Math.Min(MaxPitch, StripHeight / Ticks.Count);
        foreach (var tick in Ticks)
            tick.Pitch = pitch;
    }

    private void MarkInView()
    {
        _firstInView = _lastInView = -1;
        for (var i = 0; i < Ticks.Count; i++)
        {
            var inView = _visible.Contains(Ticks[i].AnchorUtteranceId);
            Ticks[i].IsInView = inView;
            if (!inView)
                continue;

            if (_firstInView < 0)
                _firstInView = i;
            _lastInView = i;
        }

        Magnify();
        OnPropertyChanged(nameof(CanPageUp));
        OnPropertyChanged(nameof(CanPageDown));
        PageUpCommand.NotifyCanExecuteChanged();
        PageDownCommand.NotifyCanExecuteChanged();
    }

    private void Magnify()
    {
        var hovered = HoveredTick is null ? -1 : Ticks.IndexOf(HoveredTick);
        for (var i = 0; i < Ticks.Count; i++)
        {
            var reach = Ticks[i].IsInView ? InViewReach : RestReach;
            var distance = hovered < 0 ? int.MaxValue : Math.Abs(i - hovered);
            if (distance < HoverReach.Length)
                reach = Math.Max(reach, HoverReach[distance]);
            Ticks[i].Reach = reach;
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
