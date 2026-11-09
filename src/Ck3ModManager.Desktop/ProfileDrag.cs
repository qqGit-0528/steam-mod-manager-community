using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Ck3ModManager.Desktop;

public partial class MainWindow
{
    private const double DragThreshold = 5;
    private const double ScrollEdge = 28;
    private readonly ListBox _profileList;
    private readonly DispatcherTimer _profileDragScroll = new() { Interval = TimeSpan.FromMilliseconds(55) };
    private (IPointer Pointer, MainWindowViewModel Workspace, ProfileModRow Row, Point Start)? _profileDrag;
    private ScrollViewer? _dragScrollViewer;
    private ListBoxItem? _dropTarget;
    private bool _dropAfter;
    private bool _dragStarted;
    private Point _dragPosition;

    private void InitializeProfileDrag()
    {
        _profileList.PointerMoved += MoveProfileDrag;
        _profileList.PointerReleased += ReleaseProfileDrag;
        _profileList.PointerCaptureLost += (_, _) => CancelProfileDrag();
        _profileDragScroll.Tick += (_, _) => ScrollProfileDrag();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || _profileDrag is null) return;
            CancelProfileDrag();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => CancelProfileDrag();
        Closed += (_, _) => CancelProfileDrag();
    }

    private void ProfileHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_profileDrag is not null || !e.GetCurrentPoint(_profileList).Properties.IsLeftButtonPressed ||
            sender is not Control { DataContext: ProfileModRow row } ||
            DataContext is not MainWindowViewModel workspace || !workspace.CanDragProfileRow(row)) return;

        _profileList.Focus();
        _profileDrag = (e.Pointer, workspace, row, e.GetPosition(_profileList));
        _dragScrollViewer = _profileList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        e.Pointer.Capture(_profileList);
        e.Handled = true;
    }

    private void MoveProfileDrag(object? sender, PointerEventArgs e)
    {
        if (_profileDrag is not { } drag || e.Pointer != drag.Pointer) return;
        if (!drag.Workspace.CanDragProfileRow(drag.Row))
        {
            CancelProfileDrag();
            return;
        }

        _dragPosition = e.GetPosition(_profileList);
        if (!_dragStarted)
        {
            var movement = _dragPosition - drag.Start;
            if (Math.Max(Math.Abs(movement.X), Math.Abs(movement.Y)) < DragThreshold) return;
            _dragStarted = true;
            _profileDragScroll.Start();
        }
        UpdateProfileDropTarget();
        e.Handled = true;
    }

    private void ReleaseProfileDrag(object? sender, PointerReleasedEventArgs e)
    {
        if (_profileDrag is not { } drag || e.Pointer != drag.Pointer) return;

        _dragPosition = e.GetPosition(_profileList);
        if (_dragStarted) UpdateProfileDropTarget();
        var target = _dropTarget?.DataContext as ProfileModRow;
        var after = _dropAfter;
        CancelProfileDrag();
        if (target is not null) drag.Workspace.DropProfileRow(drag.Row, target, after);
        e.Handled = true;
    }

    private bool IsOverProfileList =>
        new Rect(_profileList.Bounds.Size).Contains(_dragPosition);

    private void ScrollProfileDrag()
    {
        if (_profileDrag is not { } drag || !drag.Workspace.CanDragProfileRow(drag.Row))
        {
            CancelProfileDrag();
            return;
        }
        if (_dragScrollViewer is not { } scroll || !IsOverProfileList) return;

        var direction = _dragPosition.Y < ScrollEdge ? -1 :
            _dragPosition.Y > _profileList.Bounds.Height - ScrollEdge ? 1 : 0;
        if (direction == 0) return;

        scroll.Offset += new Vector(0, direction * 18);
        _profileList.UpdateLayout();
        UpdateProfileDropTarget();
    }

    private void UpdateProfileDropTarget()
    {
        var target = IsOverProfileList && _profileList.InputHitTest(_dragPosition) is Visual hit
            ? hit.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() : null;
        if (target?.DataContext is not ProfileModRow row || ReferenceEquals(row, _profileDrag?.Row))
            target = null;

        var after = target?.TranslatePoint(default, _profileList) is { } origin &&
            _dragPosition.Y >= origin.Y + target.Bounds.Height / 2;
        if (ReferenceEquals(target, _dropTarget) && after == _dropAfter) return;

        ClearProfileDropTarget();
        _dropTarget = target;
        _dropAfter = after;
        target?.Classes.Add(after ? "insertAfter" : "insertBefore");
    }

    private void ClearProfileDropTarget()
    {
        _dropTarget?.Classes.Remove(_dropAfter ? "insertAfter" : "insertBefore");
        _dropTarget = null;
    }

    private void CancelProfileDrag()
    {
        var pointer = _profileDrag?.Pointer;
        _profileDrag = null;
        _dragStarted = false;
        _dragScrollViewer = null;
        _profileDragScroll.Stop();
        ClearProfileDropTarget();
        if (pointer?.Captured == _profileList) pointer.Capture(null);
    }
}
