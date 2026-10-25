using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PinPoint.Core.Text;
using PinPoint.Services;
using Windows.UI;

namespace PinPoint.Views;

/// <summary>
/// Text selection on a snip. Text is recognised once in the background; the hover "T" button
/// switches into text mode, where dragging over words selects them (instead of moving the snip)
/// and the selection is copied to the clipboard on release.
/// </summary>
public sealed partial class ImagePinWindow
{
    /// <summary>Snip pixels around a word that still count as "on" it.</summary>
    private const double WordHitSlop = 4;

    private readonly List<Rectangle> _wordBoxes = [];
    private RecognizedText _text = RecognizedText.Empty;
    private bool _textMode;
    private bool _selecting;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private Brush? _wordBrush;
    private Brush? _selectedBrush;
    private InputCursor? _textCursor;
    private InputCursor? _arrowCursor;

    /// <summary>First-time hints shown before PinPoint stops pointing out the T button.</summary>
    private const int TextHintLimit = 3;

    private bool _showingTextHint;

    /// <summary>True once text has been found in this snip.</summary>
    public bool HasText => !_text.IsEmpty;

    /// <summary>Copies all of the snip's text (tables as tab-separated rows). False if there is none.</summary>
    public bool CopyAllText()
    {
        if (!HasText) return false;
        SnipStorage.CopyTextToClipboard(_text.AllText);
        ShowBadge("Text copied");
        return true;
    }

    private async Task DetectTextAsync()
    {
        if (!_ocr.IsAvailable) return;

        var text = await _ocr.RecognizeAsync(Frame);
        if (_isClosed || text.IsEmpty) return;

        _text = text;
        BuildWordBoxes();
        TextModeButton.Visibility = Visibility.Visible;
        Changed?.Invoke(this, EventArgs.Empty); // lets the Pins list offer "Copy text"
        ShowTextHintIfNeeded();
    }

    /// <summary>For the first few snips with text, briefly show the T button and say what it's for.</summary>
    private void ShowTextHintIfNeeded()
    {
        if (_clickThrough || _settings.Current.TextHintsShown >= TextHintLimit) return;

        _settings.Update(s => s.TextHintsShown++);
        ShowBadge("Text found · click T to copy it", seconds: 4);
        _showingTextHint = true;
        SetHoverChrome(true);
    }

    private void EndTextHint()
    {
        if (!_showingTextHint) return;
        _showingTextHint = false;
        if (!_pointerOver && !_drag.IsDragging) SetHoverChrome(false);
    }

    private void BuildWordBoxes()
    {
        _wordBrush ??= new SolidColorBrush(Color.FromArgb(46, 76, 194, 255));
        _selectedBrush ??= new SolidColorBrush(Color.FromArgb(125, 0, 120, 215));

        TextCanvas.Width = Frame.Width;
        TextCanvas.Height = Frame.Height;
        TextCanvas.Children.Clear();
        _wordBoxes.Clear();

        foreach (var word in _text.Words)
        {
            var box = new Rectangle
            {
                Width = word.Bounds.Width + 2,
                Height = word.Bounds.Height + 2,
                RadiusX = 2,
                RadiusY = 2,
                Fill = _wordBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, word.Bounds.X - 1);
            Canvas.SetTop(box, word.Bounds.Y - 1);
            TextCanvas.Children.Add(box);
            _wordBoxes.Add(box);
        }
    }

    private void SetTextMode(bool on)
    {
        on &= HasText && !_clickThrough;
        bool changed = on != _textMode;
        _textMode = on;
        TextModeButton.IsChecked = on;
        TextLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ClearSelection();

        if (changed) ShowBadge(on ? "Drag over text to copy it" : "Drag to move");
    }

    private void TextModeButton_Click(object sender, RoutedEventArgs e) => SetTextMode(TextModeButton.IsChecked == true);

    private void SelectText_Click(object sender, RoutedEventArgs e) => SetTextMode(SelectTextItem.IsChecked);

    private void CopyText_Click(object sender, RoutedEventArgs e) => CopyAllText();

    // ---- Selection ---------------------------------------------------------------------------

    private void TextCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TextCanvas);
        if (!point.Properties.IsLeftButtonPressed) return; // right/middle/wheel keep their usual jobs

        ClearSelection();
        int index = _text.HitTest(point.Position.X, point.Position.Y, WordHitSlop);
        if (index >= 0)
        {
            _selectionStart = _selectionEnd = index;
            _selecting = TextCanvas.CapturePointer(e.Pointer);
            UpdateSelection();
        }

        e.Handled = true; // in text mode a left-drag never moves the snip
    }

    private void TextCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(TextCanvas).Position;
        if (_selecting)
        {
            // While dragging, follow the pointer to the nearest word anywhere on the snip.
            int index = _text.HitTest(position.X, position.Y, double.PositiveInfinity);
            if (index != _selectionEnd)
            {
                _selectionEnd = index;
                UpdateSelection();
            }

            e.Handled = true;
        }

        bool overText = _selecting || _text.HitTest(position.X, position.Y, WordHitSlop) >= 0;
        TextCanvas.Cursor = overText
            ? _textCursor ??= InputSystemCursor.Create(InputSystemCursorShape.IBeam)
            : _arrowCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private void TextCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        TextCanvas.ReleasePointerCapture(e.Pointer);

        // Copy straight away: snips usually don't have keyboard focus, so Ctrl+C wouldn't reach them.
        string selected = _text.GetText(_selectionStart, _selectionEnd);
        SnipStorage.CopyTextToClipboard(selected);
        int words = Math.Abs(_selectionEnd - _selectionStart) + 1;
        ShowBadge(words == 1 ? "Copied" : $"Copied {words} words");
        e.Handled = true;
    }

    private void TextCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _selecting = false;

    /// <summary>Double-clicking text shouldn't also reset the snip's zoom.</summary>
    private void TextCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

    private void ClearSelection()
    {
        _selecting = false;
        _selectionStart = _selectionEnd = -1;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        int from = Math.Min(_selectionStart, _selectionEnd);
        int to = Math.Max(_selectionStart, _selectionEnd);
        for (int i = 0; i < _wordBoxes.Count; i++)
            _wordBoxes[i].Fill = from >= 0 && i >= from && i <= to ? _selectedBrush : _wordBrush;
    }
}
