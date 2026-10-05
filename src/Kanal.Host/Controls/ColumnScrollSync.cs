using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace Kanal.Host.Controls;

public sealed class ColumnScrollSync
{
    private const double Epsilon = 1;

    private readonly List<ScrollViewer> _scrollers = [];
    private readonly Dictionary<ScrollViewer, double> _expected = [];

    public bool IsFollowing { get; private set; } = true;

    public bool HasUnseen { get; private set; }

    public event Action? Changed;

    public void Attach(ScrollViewer scroller)
    {
        if (_scrollers.Contains(scroller))
            return;

        _scrollers.Add(scroller);
        scroller.ScrollChanged += OnScrollChanged;
    }

    public void Detach(ScrollViewer scroller)
    {
        scroller.ScrollChanged -= OnScrollChanged;
        _scrollers.Remove(scroller);
        _expected.Remove(scroller);
    }

    public void ScrollToLatest()
    {
        IsFollowing = true;
        HasUnseen = false;
        foreach (var scroller in Visible())
            ToEnd(scroller);
        Changed?.Invoke();
    }

    public void StopFollowing() => IsFollowing = false;

    private IEnumerable<ScrollViewer> Visible() => _scrollers.Where(s => s.IsEffectivelyVisible);

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller)
            return;

        var ours = _expected.Remove(scroller, out var expected)
            && Math.Abs(scroller.Offset.Y - expected) < Epsilon;

        if (e.OffsetDelta.Y != 0 && !ours)
        {
            OnReaderScrolled(scroller);
            return;
        }

        if (e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0)
            return;

        if (IsFollowing)
        {
            if (!AtBottom(scroller))
                ToEnd(scroller);
        }
        else
        {
            if (e.ExtentDelta.Y > 0 && !HasUnseen)
            {
                HasUnseen = true;
                Changed?.Invoke();
            }

            // A column that was hidden comes back at whatever offset it left with.
            if (e.ViewportDelta.Y == scroller.Viewport.Height
                && Visible().FirstOrDefault(s => s != scroller) is { } reference
                && AnchorOf(reference) is { } anchor)
                Align(scroller, anchor);
        }
    }

    private void OnReaderScrolled(ScrollViewer source)
    {
        var atBottom = AtBottom(source);
        var changed = IsFollowing != atBottom || (atBottom && HasUnseen);
        IsFollowing = atBottom;
        if (atBottom)
            HasUnseen = false;

        // ponytail: finds the top line by scanning containers; binary search if a column ever holds thousands.
        var anchor = atBottom ? null : AnchorOf(source);
        foreach (var other in Visible().Where(s => s != source))
        {
            if (atBottom)
                ToEnd(other);
            else if (anchor is { } a)
                Align(other, a);
        }

        if (changed)
            Changed?.Invoke();
    }

    private static bool AtBottom(ScrollViewer s) =>
        s.Offset.Y >= s.Extent.Height - s.Viewport.Height - Epsilon;

    private void ToEnd(ScrollViewer s) => SetOffset(s, s.Extent.Height - s.Viewport.Height);

    private void SetOffset(ScrollViewer s, double y)
    {
        y = Math.Max(0, Math.Min(y, s.Extent.Height - s.Viewport.Height));
        if (Math.Abs(y - s.Offset.Y) < 0.5)
            return;

        _expected[s] = y;
        s.Offset = new Vector(s.Offset.X, y);
    }

    private static (int Index, double Fraction)? AnchorOf(ScrollViewer s)
    {
        if (s.Content is not ItemsControl items)
            return null;

        for (var i = 0; i < items.ItemCount; i++)
        {
            if (items.ContainerFromIndex(i) is not Control row || TopOf(row, items) is not { } top)
                continue;

            if (top + row.Bounds.Height > s.Offset.Y + 0.5)
                return (i, row.Bounds.Height <= 0 ? 0 : Math.Clamp((s.Offset.Y - top) / row.Bounds.Height, 0, 1));
        }

        return null;
    }

    private void Align(ScrollViewer s, (int Index, double Fraction) anchor)
    {
        if (s.Content is not ItemsControl items
            || items.ContainerFromIndex(anchor.Index) is not Control row
            || TopOf(row, items) is not { } top)
            return;

        SetOffset(s, top + anchor.Fraction * row.Bounds.Height);
    }

    private static double? TopOf(Control row, ItemsControl items) =>
        row.TranslatePoint(default, items)?.Y;
}
