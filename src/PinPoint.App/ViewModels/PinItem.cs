using Microsoft.UI.Xaml.Media;
using PinPoint.Core.Windows;
using PinPoint.Services;
using PinPoint.Views;

namespace PinPoint.ViewModels;

/// <summary>One row in the Pins list. Subclass per pin kind.</summary>
public abstract class PinItem : ObservableObject
{
    public abstract string Title { get; }

    public abstract string Subtitle { get; }

    public abstract string Glyph { get; }

    public virtual ImageSource? Thumbnail => null;

    public bool IsGlyphVisible => Thumbnail is null;

    public abstract bool SupportsOpacity { get; }

    /// <summary>10–100, bound to the row's slider.</summary>
    public abstract double OpacityPercent { get; set; }

    public abstract string RemoveLabel { get; }

    public abstract void Remove();

    /// <summary>True when this pin has recognised text that can be copied.</summary>
    public virtual bool CanCopyText => false;

    public virtual void CopyText()
    {
    }

    /// <summary>Called when the pin changes (title, opacity, text found) so the row stays current.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(OpacityPercent));
        OnPropertyChanged(nameof(CanCopyText));
    }
}

public sealed class WindowPinItem(PinnedWindow pin, PinManager manager) : PinItem
{
    public PinnedWindow Pin { get; } = pin;

    public override string Title => Pin.Title;

    public override string Subtitle => string.IsNullOrEmpty(Pin.ProcessName) ? "Window" : $"Window · {Pin.ProcessName}";

    public override string Glyph => ""; // app window

    public override bool SupportsOpacity => Pin.SupportsOpacity;

    public override double OpacityPercent
    {
        get => Math.Round(Pin.Opacity * 100);
        set
        {
            if (Math.Abs(value - OpacityPercent) < 0.5) return;
            manager.SetOpacity(Pin, value / 100);
            OnPropertyChanged();
        }
    }

    public override string RemoveLabel => "Unpin";

    public override void Remove() => manager.Unpin(Pin);
}

public sealed class SnipPinItem : PinItem
{
    public SnipPinItem(ImagePinWindow window)
    {
        Window = window;
        window.Changed += (_, _) => Refresh();
    }

    public ImagePinWindow Window { get; }

    public override string Title => Window.SavedPath is { } path ? Path.GetFileNameWithoutExtension(path) : "Snip";

    public override string Subtitle => $"Snip · {Window.Frame.Width} × {Window.Frame.Height}";

    public override string Glyph => ""; // crop

    public override ImageSource? Thumbnail => Window.Bitmap;

    public override bool SupportsOpacity => true;

    public override double OpacityPercent
    {
        get => Math.Round(Window.PinOpacity * 100);
        set
        {
            if (Math.Abs(value - OpacityPercent) < 0.5) return;
            Window.PinOpacity = value / 100;
            OnPropertyChanged();
        }
    }

    public override string RemoveLabel => "Close";

    public override void Remove() => Window.Close();

    public override bool CanCopyText => Window.HasText;

    public override void CopyText() => Window.CopyAllText();
}
