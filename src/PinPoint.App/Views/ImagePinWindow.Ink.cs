using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PinPoint.Core.Drawing;
using Windows.Foundation;
using Windows.UI;

namespace PinPoint.Views;

/// <summary>
/// Drawing on a snip to mark things up. In draw mode the left button draws red strokes and a right-drag
/// erases whole strokes it touches; a plain right-click still opens the menu and right-hold + scroll still
/// fades. Strokes are in snip pixels on a layer that scales with the image, so they stay on what they mark
/// and thicken as you zoom. Undo/redo/erase-all are buttons because snips usually lack keyboard focus.
/// </summary>
public sealed partial class ImagePinWindow
{
    /// <summary>Stroke width in snip pixels (so it scales with zoom).</summary>
    private const double InkThickness = 3;

    /// <summary>How close (snip pixels) a right-drag must pass to a stroke to erase it.</summary>
    private const double EraserRadius = 6;

    /// <summary>Screen pixels a right-press may move before it counts as an erase drag, not a click.</summary>
    private const double RightDragThreshold = 4;

    private static readonly Color InkColor = Color.FromArgb(255, 232, 17, 35);

    private readonly StrokeHistory<Polyline> _ink = new();
    private readonly List<Polyline> _erasing = [];
    private Polyline? _currentStroke;
    private bool _drawMode;
    private bool _rightErasing;
    private bool _rightDragged;
    private Point _rightStart;
    private Brush? _inkBrush;
    private Brush? _inkHitBackground;
    private InputCursor? _penCursor;

    private void InitInk()
    {
        InkCanvas.Width = Frame.Width;
        InkCanvas.Height = Frame.Height;
        _ink.Changed += (_, _) => SyncInk();
        UpdateInkButtons();
    }

    private void SetDrawMode(bool on)
    {
        on &= !_clickThrough;
        if (on) SetTextMode(false); // one mode at a time; its own badge is replaced below

        bool changed = on != _drawMode;
        _drawMode = on;
        DrawModeButton.IsChecked = on;
        DrawTools.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        InkCanvas.IsHitTestVisible = on;
        InkCanvas.Background = on ? _inkHitBackground ??= new SolidColorBrush(Colors.Transparent) : null;
        InkCanvas.Cursor = on ? _penCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Cross) : null;
        CancelInkGesture();

        if (changed) ShowBadge(on ? "Draw to mark it up · right-drag erases" : "Drag to move");
    }

    private void DrawModeButton_Click(object sender, RoutedEventArgs e) => SetDrawMode(DrawModeButton.IsChecked == true);

    private void DrawItem_Click(object sender, RoutedEventArgs e) => SetDrawMode(DrawItem.IsChecked);

    private void UndoInk_Click(object sender, RoutedEventArgs e) => _ink.Undo();

    private void RedoInk_Click(object sender, RoutedEventArgs e) => _ink.Redo();

    private void ClearInk_Click(object sender, RoutedEventArgs e)
    {
        if (_ink.Strokes.Count == 0) return;
        _ink.Clear();
        ShowBadge("Drawing cleared · undo brings it back");
    }

    // ---- Pointer -----------------------------------------------------------------------------

    private void InkCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(InkCanvas);

        if (point.Properties.IsRightButtonPressed)
        {
            // Not handled: the window still tracks right-hold for scroll-to-fade and click-for-menu.
            _rightErasing = true;
            _rightDragged = false;
            _rightStart = e.GetCurrentPoint(Root).Position;
            EraseAt(point.Position);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        _inkBrush ??= new SolidColorBrush(InkColor);
        _currentStroke = new Polyline
        {
            Stroke = _inkBrush,
            StrokeThickness = InkThickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        _currentStroke.Points.Add(point.Position);
        _currentStroke.Points.Add(new Point(point.Position.X + 0.01, point.Position.Y)); // a click leaves a dot
        InkCanvas.Children.Add(_currentStroke);
        InkCanvas.CapturePointer(e.Pointer);
        e.Handled = true; // in draw mode a left-drag never moves the snip
    }

    private void InkCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(InkCanvas).Position;

        if (_currentStroke is not null)
        {
            var last = _currentStroke.Points[^1];
            double dx = position.X - last.X, dy = position.Y - last.Y;
            if (dx * dx + dy * dy >= 0.5) _currentStroke.Points.Add(position); // skip sub-pixel jitter
            e.Handled = true;
            return;
        }

        if (_rightErasing)
        {
            var screen = e.GetCurrentPoint(Root).Position;
            if (Math.Abs(screen.X - _rightStart.X) > RightDragThreshold || Math.Abs(screen.Y - _rightStart.Y) > RightDragThreshold)
                _rightDragged = true;
            EraseAt(position);
        }
    }

    private void InkCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var kind = e.GetCurrentPoint(InkCanvas).Properties.PointerUpdateKind;

        if (kind == PointerUpdateKind.LeftButtonReleased && _currentStroke is not null)
        {
            var stroke = _currentStroke;
            _currentStroke = null;
            InkCanvas.ReleasePointerCapture(e.Pointer);
            _ink.Add(stroke);
            e.Handled = true;
            return;
        }

        if (kind == PointerUpdateKind.RightButtonReleased && _rightErasing)
        {
            // Not handled: the window decides about the menu (skipped if this was an erase drag).
            _rightErasing = false;
            if (_erasing.Count > 0) _ink.Remove(_erasing); // one undo step per drag
            _erasing.Clear();
        }
    }

    private void InkCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_currentStroke is null) return;
        var stroke = _currentStroke;
        _currentStroke = null;
        _ink.Add(stroke); // keep what was drawn even if the drag was interrupted
    }

    /// <summary>Double-clicking while drawing shouldn't also reset the snip's zoom.</summary>
    private void InkCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

    /// <summary>True if the last right-press erased or moved (so releasing it mustn't open the menu).</summary>
    private bool RightPressWasErase => _drawMode && _rightDragged;

    // ---- Helpers -----------------------------------------------------------------------------

    private void EraseAt(Point position)
    {
        foreach (var stroke in _ink.Strokes)
        {
            if (_erasing.Contains(stroke)) continue;
            var points = stroke.Points.Select(p => (p.X, p.Y)).ToList();
            if (!StrokeGeometry.IsNear(points, position.X, position.Y, EraserRadius + InkThickness / 2)) continue;

            _erasing.Add(stroke);
            stroke.Opacity = 0; // gone at once; committed as one undo step on release
            _rightDragged = true;
        }
    }

    private void CancelInkGesture()
    {
        if (_currentStroke is not null)
        {
            _ink.Add(_currentStroke);
            _currentStroke = null;
        }

        foreach (var stroke in _erasing) stroke.Opacity = 1;
        _erasing.Clear();
        _rightErasing = false;
    }

    /// <summary>Redraws the canvas from the history (strokes are few; rebuilding is simplest).</summary>
    private void SyncInk()
    {
        InkCanvas.Children.Clear();
        foreach (var stroke in _ink.Strokes)
        {
            stroke.Opacity = 1;
            InkCanvas.Children.Add(stroke);
        }

        if (_currentStroke is not null) InkCanvas.Children.Add(_currentStroke);
        UpdateInkButtons();
    }

    private void UpdateInkButtons()
    {
        UndoButton.IsEnabled = _ink.CanUndo;
        RedoButton.IsEnabled = _ink.CanRedo;
        ClearInkButton.IsEnabled = _ink.Strokes.Count > 0;
    }
}
