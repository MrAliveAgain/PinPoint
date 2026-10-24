using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PinPoint.Core.Capture;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace PinPoint.Views;

/// <summary>
/// Full-virtual-screen region picker over a frozen screenshot. Created once and reused;
/// hidden (and its bitmap released) between snips.
/// </summary>
public sealed partial class SnipOverlayWindow : Window
{
    private const int ClickTolerance = 4; // px; smaller drags count as a click

    private readonly nint _hwnd;
    private TaskCompletionSource<PixelRect?>? _completion;
    private PixelRect _bounds;
    private IReadOnlyList<PixelRect> _targets = [];
    private double _scale = 1;
    private bool _wasActivated;
    private bool _dragging;
    private Win32.POINT _dragStart;
    private PixelRect? _current;
    private PixelRect _hintMonitor;

    public SnipOverlayWindow()
    {
        InitializeComponent();
        _hwnd = this.GetHandle();

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(WindowHelpers.IconPath);

        HintPill.SizeChanged += (_, _) => PositionHint();
        Activated += OnActivated;
    }

    public nint Handle => _hwnd;

    public bool IsSelecting => _completion is not null;

    /// <summary>Shows the overlay and resolves with the chosen screen rectangle, or null if cancelled.</summary>
    public async Task<PixelRect?> SelectAsync(CapturedFrame frame, IReadOnlyList<PixelRect> targets)
    {
        if (_completion is not null) return null;

        var completion = new TaskCompletionSource<PixelRect?>();
        _completion = completion;
        _bounds = frame.Bounds;
        _targets = targets;
        _wasActivated = false;
        _current = null;

        Frozen.Source = frame.ToWriteableBitmap();

        // Render while cloaked so the first visible frame is already the frozen screenshot (no flash).
        WindowStyler.SetCloaked(_hwnd, true);
        AppWindow.MoveAndResize(new RectInt32(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height));
        AppWindow.Show(activateWindow: true);
        // A DPI change while moving can rescale the window; pin it to the exact virtual screen again.
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, Win32.SWP_NOACTIVATE);

        _scale = Root.XamlRoot?.RasterizationScale ?? WindowHelpers.GetScale(_hwnd);
        LayoutSurface();
        Win32.GetCursorPos(out var cursor);
        PlaceHintOnMonitorAt(cursor);
        UpdateHover(cursor);

        await WaitForFramesAsync(2);
        if (_completion != completion) return await completion.Task; // cancelled meanwhile

        WindowStyler.SetCloaked(_hwnd, false);
        Win32.SetForegroundWindow(_hwnd);
        Activate();
        FocusSink.Focus(FocusState.Programmatic);
        Shade.Opacity = 1;

        return await completion.Task;
    }

    public void Cancel() => Complete(null);

    // ---- Layout ------------------------------------------------------------------------------

    private void LayoutSurface()
    {
        double width = _bounds.Width / _scale;
        double height = _bounds.Height / _scale;
        Frozen.Width = width;
        Frozen.Height = height;
        Surface.Width = width;
        Surface.Height = height;
        ShadeFull.Rect = new Rect(0, 0, width, height);
        HintPill.Opacity = 1;
        UpdateSelectionVisuals();
    }

    private Rect ToDip(PixelRect r) =>
        new((r.X - _bounds.X) / _scale, (r.Y - _bounds.Y) / _scale, r.Width / _scale, r.Height / _scale);

    private void PlaceHintOnMonitorAt(Win32.POINT cursor)
    {
        _hintMonitor = WindowEnumerator.GetMonitorAt(cursor.X, cursor.Y).Bounds;
        PositionHint();
    }

    private void PositionHint()
    {
        var monitor = ToDip(_hintMonitor);
        Canvas.SetLeft(HintPill, monitor.X + (monitor.Width - HintPill.ActualWidth) / 2);
        Canvas.SetTop(HintPill, monitor.Y + 28);
    }

    private void UpdateSelectionVisuals()
    {
        if (_current is not { } current)
        {
            ShadeHole.Rect = default;
            SelectionOutline.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var r = ToDip(current);
        ShadeHole.Rect = r;

        Canvas.SetLeft(SelectionOutline, r.X - 1);
        Canvas.SetTop(SelectionOutline, r.Y - 1);
        SelectionOutline.Width = r.Width + 2;
        SelectionOutline.Height = r.Height + 2;
        SelectionOutline.Visibility = Visibility.Visible;

        SizeText.Text = $"{current.Width} × {current.Height}";
        SizeBadge.Visibility = Visibility.Visible;
        // Below the selection's bottom-left corner, or inside it when there's no room.
        double top = r.Bottom + 6;
        if (top + 28 > Surface.Height) top = r.Bottom - 32;
        Canvas.SetLeft(SizeBadge, Math.Max(0, r.X));
        Canvas.SetTop(SizeBadge, Math.Max(0, top));
    }

    // ---- Input -------------------------------------------------------------------------------

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(Root).Properties;
        if (properties.IsRightButtonPressed)
        {
            if (_dragging)
            {
                // Right-click mid-drag: abandon this rectangle, keep the overlay.
                _dragging = false;
                Root.ReleasePointerCaptures();
                Win32.GetCursorPos(out var cursor);
                UpdateHover(cursor);
            }
            else
            {
                Complete(null);
            }

            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed) return;

        Win32.GetCursorPos(out _dragStart);
        _dragging = Root.CapturePointer(e.Pointer);
        HintPill.Opacity = 0;
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Win32.GetCursorPos(out var cursor);
        if (!_dragging)
        {
            UpdateHover(cursor);
            return;
        }

        // Inclusive of the pixel under the cursor.
        _current = PixelRect.FromEdges(
            Math.Min(_dragStart.X, cursor.X), Math.Min(_dragStart.Y, cursor.Y),
            Math.Max(_dragStart.X, cursor.X) + 1, Math.Max(_dragStart.Y, cursor.Y) + 1).Intersect(_bounds);
        UpdateSelectionVisuals();
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);

        Win32.GetCursorPos(out var cursor);
        bool isClick = Math.Abs(cursor.X - _dragStart.X) <= ClickTolerance && Math.Abs(cursor.Y - _dragStart.Y) <= ClickTolerance;
        if (isClick)
        {
            UpdateHover(cursor); // click = take the window/monitor under the cursor
        }

        Complete(_current);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Complete(null);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                Complete(_current);
                e.Handled = true;
                break;
        }
    }

    private void UpdateHover(Win32.POINT cursor)
    {
        PixelRect? hovered = null;
        foreach (var target in _targets)
        {
            if (target.Contains(cursor.X, cursor.Y))
            {
                hovered = target.Intersect(_bounds);
                break;
            }
        }

        if (hovered == _current) return;
        _current = hovered;
        UpdateSelectionVisuals();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_completion is null) return;

        if (args.WindowActivationState != WindowActivationState.Deactivated)
            _wasActivated = true;
        else if (_wasActivated)
            Complete(null); // focus went elsewhere (Alt+Tab, notification…): never leave a frozen screen up
    }

    // ---- Finish ------------------------------------------------------------------------------

    private void Complete(PixelRect? result)
    {
        var completion = _completion;
        if (completion is null) return;
        _completion = null;

        _dragging = false;
        Root.ReleasePointerCaptures();
        AppWindow.Hide();
        WindowStyler.SetCloaked(_hwnd, false);

        // Release the full-screen bitmap right away; the overlay idles at near-zero memory.
        Frozen.Source = null;
        Shade.Opacity = 0;
        _current = null;
        _targets = [];
        UpdateSelectionVisuals();

        completion.TrySetResult(result is { IsEmpty: false } r ? r : null);
    }

    private static async Task WaitForFramesAsync(int frames)
    {
        var rendered = new TaskCompletionSource();
        int count = 0;

        void OnRendering(object? sender, object e)
        {
            if (++count < frames) return;
            CompositionTarget.Rendering -= OnRendering;
            rendered.TrySetResult();
        }

        CompositionTarget.Rendering += OnRendering;
        // Safety net: never hang the snip if no frame is produced.
        if (await Task.WhenAny(rendered.Task, Task.Delay(250)) != rendered.Task)
            CompositionTarget.Rendering -= OnRendering;
    }
}
