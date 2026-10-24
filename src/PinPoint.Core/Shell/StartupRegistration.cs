using Microsoft.Win32;
using PinPoint.Core.Diagnostics;

namespace PinPoint.Core.Shell;

/// <summary>
/// Optional "start with Windows" via the per-user Run key. Off unless the user turns it on;
/// nothing machine-wide is ever touched.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string BackgroundArgument = "--background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppPaths.AppName) is string;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
                key.SetValue(AppPaths.AppName, $"\"{Environment.ProcessPath}\" {BackgroundArgument}");
            else
                key.DeleteValue(AppPaths.AppName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not update startup registration", ex);
            return false;
        }
    }
}
