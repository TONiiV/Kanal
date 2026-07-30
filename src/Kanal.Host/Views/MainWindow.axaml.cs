using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class MainWindow : Window
{
    /// <summary>Per-column: is this column still tracking the newest utterance?</summary>
    private readonly Dictionary<ScrollViewer, bool> _following = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow();
        await dialog.ShowDialog(this);
        (DataContext as MainViewModel)?.RefreshKeyStatus();
    }

    private void OnColumnScrollLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scroller || _following.ContainsKey(scroller))
            return;

        _following[scroller] = true;
        scroller.ScrollChanged += OnColumnScrollChanged;
    }

    private void OnColumnScrollDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ScrollViewer scroller)
            return;

        scroller.ScrollChanged -= OnColumnScrollChanged;
        _following.Remove(scroller);
    }

    /// <summary>
    /// Follow the newest utterance: emphasising the live record is pointless if it scrolls out of
    /// sight. But an operator who scrolled back to re-read a part number must not be yanked
    /// forward, so scrolling away opts the column out until they return to the bottom.
    ///
    /// Driven off ScrollChanged rather than the collection: by the time this fires the new extent
    /// is already laid out, so "am I at the bottom" is asked of the real geometry.
    /// </summary>
    private void OnColumnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller)
            return;

        // 2 px of slack absorbs sub-pixel extent rounding at the true bottom
        var atBottom = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 2;

        if (e.ExtentDelta.Y == 0)
        {
            // nothing was added, so this offset change came from the operator — they decide
            _following[scroller] = atBottom;
            return;
        }

        if (_following.TryGetValue(scroller, out var following) && following && !atBottom)
            scroller.ScrollToEnd();
    }
}
