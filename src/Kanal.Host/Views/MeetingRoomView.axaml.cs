using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class MeetingRoomView : UserControl
{
    private static readonly DataFormat<string> ColumnDragFormat =
        DataFormat.CreateStringApplicationFormat("kanal-column");

    private readonly Dictionary<ScrollViewer, bool> _following = new();

    public MeetingRoomView() => InitializeComponent();

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

    private void OnColumnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller)
            return;

        var atBottom = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 2;
        if (e.ExtentDelta.Y == 0)
        {
            _following[scroller] = atBottom;
            return;
        }

        if (_following.TryGetValue(scroller, out var following) && following && !atBottom)
            scroller.ScrollToEnd();
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

    private void FocusColumnHead(ColumnViewModel column) =>
        this.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("colhead") && ReferenceEquals(b.DataContext, column))
            ?.Focus();
}
