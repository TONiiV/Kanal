using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class MainWindow : Window
{
    /// <summary>Marks a column drag as ours, so no other payload lights up a drop rule.</summary>
    private static readonly DataFormat<string> ColumnDragFormat =
        DataFormat.CreateStringApplicationFormat("kanal-column");

    /// <summary>Per-column: is this column still tracking the newest utterance?</summary>
    private readonly Dictionary<ScrollViewer, bool> _following = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.ChooseExportPath = ChooseExportPathAsync;
        };
    }

    /// <summary>The native device-hotplug listener must not outlive the window that shows the list.</summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// The view half of export: the view model builds the transcript and knows what to suggest,
    /// this opens the dialog. Returns null when the operator cancels.
    /// </summary>
    private async Task<string?> ChooseExportPathAsync(string folder, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Instance["export.dialog.title"],
            SuggestedFileName = suggestedName,
            DefaultExtension = "md",
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await SafeFolderAsync(folder),
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    /// <summary>A configured folder that has since been deleted must not take the dialog with it.</summary>
    private async Task<IStorageFolder?> SafeFolderAsync(string path)
    {
        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch
        {
            return null;
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow();
        await dialog.ShowDialog(this);
        (DataContext as MainViewModel)?.RefreshPipelineStatus();
    }

    private async void OnLanguagesClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new LanguagesWindow { DataContext = DataContext };
        await dialog.ShowDialog(this);
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

    // ---- moving a column ---------------------------------------------------------------
    //
    // The head is the grab handle: the transcript below it stays selectable and scrollable.
    // Dragging is the primary gesture; Alt+←/→ on the focused head is the same operation for a
    // trackpad mid-meeting, and the only route a headless test can drive end to end. Both land
    // on MainViewModel.MoveColumn, which is where the invariants live.

    /// <summary>
    /// Starts the drag from the press: <c>DoDragDropAsync</c> takes the pressed-event args, and
    /// the platform applies its own movement threshold — a click that never moves comes back as
    /// <see cref="DragDropEffects.None"/> and changes nothing.
    /// </summary>
    private async void OnColumnHeadPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control head || head.DataContext is not ColumnViewModel column ||
            DataContext is not MainViewModel vm ||
            !e.GetCurrentPoint(head).Properties.IsLeftButtonPressed)
            return;

        head.Focus(); // a click also arms the keyboard route
        vm.BeginColumnDrag(vm.Columns.IndexOf(column));

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(ColumnDragFormat, column.Language));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // no platform drag source (headless, some Linux sessions): Alt+←/→ still moves it
        }
        finally
        {
            // a completed drop has already committed and cleared; this covers every other ending
            vm.CancelColumnDrag();
        }
    }

    private void OnColumnDragOver(object? sender, DragEventArgs e)
    {
        if (!TryResolveDropTarget(sender, e, out var vm, out var index, out var before))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        vm.UpdateColumnDropTarget(index, before);
        e.Handled = true;
    }

    private void OnColumnDrop(object? sender, DragEventArgs e)
    {
        if (!TryResolveDropTarget(sender, e, out var vm, out var index, out var before))
            return;

        vm.DropColumn(index, before);
        e.Handled = true;
    }

    private void OnColumnDragLeave(object? sender, RoutedEventArgs e) =>
        (DataContext as MainViewModel)?.UpdateColumnDropTarget(-1, before: false);

    /// <summary>Which column the pointer is over, and which side of it — the drop lands there.</summary>
    private bool TryResolveDropTarget(
        object? sender, DragEventArgs e, out MainViewModel vm, out int index, out bool before)
    {
        vm = null!;
        index = -1;
        before = false;

        if (DataContext is not MainViewModel model || sender is not Control target ||
            target.DataContext is not ColumnViewModel column ||
            !e.DataTransfer.Contains(ColumnDragFormat))
            return false;

        vm = model;
        index = model.Columns.IndexOf(column);
        before = e.GetPosition(target).X < target.Bounds.Width / 2;
        return index >= 0;
    }

    private void OnColumnHeadKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control head || head.DataContext is not ColumnViewModel column ||
            DataContext is not MainViewModel vm || e.KeyModifiers != KeyModifiers.Alt)
            return;

        var from = vm.Columns.IndexOf(column);
        var to = e.Key switch
        {
            Key.Left => from - 1,
            Key.Right => from + 1,
            _ => from,
        };

        if (from < 0 || to == from || to < 0 || to >= vm.Columns.Count)
            return;

        vm.MoveColumn(from, to);
        e.Handled = true;
        // the head travelled with its column; keep the keyboard on it for the next press
        Dispatcher.UIThread.Post(() => FocusColumnHead(column));
    }

    private void FocusColumnHead(ColumnViewModel column) =>
        this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("colhead") && ReferenceEquals(b.DataContext, column))
            ?.Focus();
}
