using PinPoint.Core.Diagnostics;
using PinPoint.Core.Interop;

namespace PinPoint.Core.Shell;

public static class NativeTheme
{
    private const int AllowDark = 1;

    /// <summary>Lets Win32 popup menus (tray menu) follow the system dark/light theme.</summary>
    public static void EnableDarkMenus()
    {
        try
        {
            Win32.SetPreferredAppMode(AllowDark);
            Win32.FlushMenuThemes();
        }
        catch (Exception ex)
        {
            // Undocumented export; if a future Windows drops it we simply keep light menus.
            Log.Warn($"Dark menus unavailable: {ex.Message}");
        }
    }
}
