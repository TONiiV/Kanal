using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.Controls;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class MeetingRoomView : UserControl
{
    private static readonly DataFormat<string> ColumnDragFormat =
        DataFormat.CreateStringApplicationFormat("kanal-column");

    private readonly ColumnScrollSync _scroll = new();
    private MainViewModel? _bound;

    public MeetingRoomView()
    {
        InitializeComponent();
        _scroll.Changed += () => JumpToLatest.IsVisible = _scroll.HasUnseen;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_bound is not null)
        {
            _bound.Ruler.JumpRequested -= ScrollTo;
            _bound.BrowsedRuler.JumpRequested -= ScrollTo;
            _bound.Sidebar.PropertyChanged -= OnSelectedMeetingChanged;
        }

        _bound = DataContext as MainViewModel;
        if (_bound is not null)
        {
            _bound.Ruler.JumpRequested += ScrollTo;
            _bound.BrowsedRuler.JumpRequested += ScrollTo;
            _bound.Sidebar.PropertyChanged += OnSelectedMeetingChanged;
        }

        base.OnDataContextChanged(e);
    }

    private void OnSelectedMeetingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceSidebarViewModel.SelectedMeeting))
            _scroll.ScrollToLatest();
    }

    private void OnJumpToLatestClick(object? sender, RoutedEventArgs e) => _scroll.ScrollToLatest();

    private void OnTickEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control row && row.DataContext is RulerTickViewModel tick &&
            DataContext is MainViewModel vm)
            vm.ShownRuler.Hover(tick);
    }

    private void OnTickExited(object? sender, PointerEventArgs e) =>
        (DataContext as MainViewModel)?.ShownRuler.Hover(null);

    private void OnTickPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control row || row.DataContext is not RulerTickViewModel tick ||
            DataContext is not MainViewModel vm ||
            !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            return;

        vm.ShownRuler.Jump(tick);
        e.Handled = true;
    }

    private void ScrollTo(string utteranceId)
    {
        _scroll.StopFollowing();
        foreach (var scroller in this.GetVisualDescendants().OfType<ScrollViewer>().ToList())
        {
            if (scroller.Content is not ItemsControl items)
                continue;

            var bubble = items.ItemsSource?.OfType<BubbleViewModel>()
                .FirstOrDefault(b => b.UtteranceId == utteranceId);
            if (bubble is not null && items.ContainerFromItem(bubble) is Control container)
                container.BringIntoView();
        }
    }

    private async void OnLanguagesClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await new LanguagesWindow { DataContext = DataContext }.ShowDialog(owner);
    }

    private void OnColumnScrollLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer scroller)
            _scroll.Attach(scroller);
    }

    private void OnColumnScrollDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is ScrollViewer scroller)
            _scroll.Detach(scroller);
    }

    private void OnColumnFocusClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ColumnViewModel column } && DataContext is MainViewModel vm)
            vm.ToggleColumnFocus(column);
    }

    private async void OnColumnHeadPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control head || head.DataContext is not ColumnViewModel column ||
            DataContext is not MainViewModel vm ||
            !e.GetCurrentPoint(head).Properties.IsLeftButtonPressed)
            return;

        head.Focus();
        vm.BeginColumnDrag(vm.Columns.IndexOf(column));

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(ColumnDragFormat, column.Language));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // Some headless and Linux sessions have no platform drag source; the keyboard route remains.
        }
        finally
        {
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
        Dispatcher.UIThread.Post(() => FocusColumnHead(column));
    }

    private void OnTitlePressed(object? sender, PointerPressedEventArgs e) => BeginRename();

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        BeginRename();
        e.Handled = true;
    }

    private void BeginRename()
    {
        if (DataContext is not MainViewModel vm) return;

        vm.BeginRenameTitleCommand.Execute(null);
        Dispatcher.UIThread.Post(() =>
        {
            MeetingTitleEditor.Focus();
            MeetingTitleEditor.CaretIndex = 0;
            MeetingTitleEditor.SelectAll();
        });
    }

    private void OnTitleEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (e.Key == Key.Enter)
            vm.CommitRenameTitleCommand.Execute(null);
        else if (e.Key == Key.Escape)
            vm.CancelRenameTitleCommand.Execute(null);
        else
            return;

        e.Handled = true;
    }

    // Clicking away commits rather than discards: the operator typed the name they wanted, and
    // losing it to a stray click is worse than committing one they can retype.
    private void OnTitleEditorLostFocus(object? sender, RoutedEventArgs e) =>
        (DataContext as MainViewModel)?.CommitRenameTitleCommand.Execute(null);

    private void FocusColumnHead(ColumnViewModel column) =>
        this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("colhead") && ReferenceEquals(b.DataContext, column))
            ?.Focus();
}
