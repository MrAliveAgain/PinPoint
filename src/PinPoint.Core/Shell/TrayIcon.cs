using PinPoint.Core.Diagnostics;
using PinPoint.Core.Interop;

namespace PinPoint.Core.Shell;

public sealed record TrayMenuItem(string Text, Action? Execute = null, bool IsChecked = false, bool IsEnabled = true)
{
    public static TrayMenuItem Separator { get; } = new("-");

    public bool IsSeparator => ReferenceEquals(this, Separator);

    /// <summary>When set, this item opens a submenu of these instead of running <see cref="Execute"/>.</summary>
    public IReadOnlyList<TrayMenuItem>? Children { get; init; }
}

/// <summary>
/// Notification-area icon using Shell_NotifyIcon directly (no extra dependency).
/// Left click raises <see cref="Invoked"/>; right click shows a themed Win32 menu built on demand.
/// </summary>
public sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Win32.WM_USER + 0x50;
    private const uint IconId = 1;

    private readonly MessageWindow _window;
    private readonly uint _taskbarCreatedMessage;
    private readonly string _tooltip;
    private nint _icon;
    private bool _added;

    public TrayIcon(MessageWindow window, string iconPath, string tooltip)
    {
        _window = window;
        _tooltip = tooltip;
        _taskbarCreatedMessage = Win32.RegisterWindowMessageW("TaskbarCreated");

        int size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, Win32.GetDpiForSystem());
        _icon = Win32.LoadImageW(0, iconPath, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
        if (_icon == 0) Log.Warn($"Could not load tray icon from {iconPath}");

        _window.AddHook(OnMessage);
        Add();
    }

    public event Action? Invoked;

    /// <summary>Builds the context menu each time it opens, so labels/checks are always current.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuFactory { get; set; }

    public void Dispose()
    {
        _window.RemoveHook(OnMessage);
        if (_added)
        {
            var data = CreateData();
            Win32.Shell_NotifyIconW(Win32.NIM_DELETE, &data);
            _added = false;
        }

        if (_icon != 0)
        {
            Win32.DestroyIcon(_icon);
            _icon = 0;
        }
    }

    private void Add()
    {
        var data = CreateData();
        data.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        CopyString(_tooltip, data.szTip, 128);

        _added = Win32.Shell_NotifyIconW(Win32.NIM_ADD, &data);
        if (!_added)
        {
            Log.Warn("Shell_NotifyIcon(NIM_ADD) failed");
            return;
        }

        data.uVersion = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIconW(Win32.NIM_SETVERSION, &data);
    }

    private Win32.NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = (uint)sizeof(Win32.NOTIFYICONDATAW),
        hWnd = _window.Handle,
        uID = IconId,
    };

    private bool OnMessage(uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;

        if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted: our icon is gone, add it again.
            _added = false;
            Add();
            return true;
        }

        if (msg != CallbackMessage) return false;

        // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, wParam = anchor x/y.
        uint notification = (uint)(lParam & 0xFFFF);
        switch (notification)
        {
            case Win32.NIN_SELECT or Win32.NIN_KEYSELECT:
                Invoked?.Invoke();
                break;
            case Win32.WM_CONTEXTMENU:
                ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                break;
        }

        return true;
    }

    private void ShowMenu(int x, int y)
    {
        var items = MenuFactory?.Invoke();
        if (items is null || items.Count == 0) return;

        var commands = new List<TrayMenuItem>();
        nint menu = BuildMenu(items, commands);
        try
        {
            // Required so the menu closes when the user clicks elsewhere.
            Win32.SetForegroundWindow(_window.Handle);
            int command = Win32.TrackPopupMenuEx(menu,
                Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN,
                x, y, _window.Handle, 0);
            Win32.PostMessageW(_window.Handle, Win32.WM_NULL, 0, 0);

            if (command > 0) commands[command - 1].Execute?.Invoke();
        }
        finally
        {
            Win32.DestroyMenu(menu); // destroys its submenus too
        }
    }

    /// <summary>Builds a (sub)menu; clickable items get ids 1..n in <paramref name="commands"/>.</summary>
    private static nint BuildMenu(IReadOnlyList<TrayMenuItem> items, List<TrayMenuItem> commands)
    {
        nint menu = Win32.CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);
                continue;
            }

            uint flags = Win32.MF_STRING
                | (item.IsChecked ? Win32.MF_CHECKED : 0)
                | (item.IsEnabled ? 0 : Win32.MF_GRAYED);

            if (item.Children is { Count: > 0 } children)
            {
                Win32.AppendMenuW(menu, flags | Win32.MF_POPUP, (nuint)BuildMenu(children, commands), item.Text);
            }
            else
            {
                commands.Add(item);
                Win32.AppendMenuW(menu, flags, (nuint)commands.Count, item.Text);
            }
        }

        return menu;
    }

    private static void CopyString(string value, char* destination, int capacity)
    {
        int length = Math.Min(value.Length, capacity - 1);
        value.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }
}
