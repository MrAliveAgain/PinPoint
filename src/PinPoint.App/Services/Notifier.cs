using PinPoint.Core.Diagnostics;
using PinPoint.Views;

namespace PinPoint.Services;

/// <summary>Brief on-screen confirmations. Created lazily so idle memory stays low.</summary>
public sealed class Notifier(SettingsService settings)
{
    private OsdWindow? _window;

    public void Show(string glyph, string title, string? detail = null, bool force = false)
    {
        if (!force && !settings.Current.ShowNotifications) return;

        try
        {
            _window ??= new OsdWindow();
            _window.Show(glyph, title, detail);
        }
        catch (Exception ex)
        {
            Log.Error("Notification failed", ex);
        }
    }

    public void Close()
    {
        _window?.Close();
        _window = null;
    }
}
