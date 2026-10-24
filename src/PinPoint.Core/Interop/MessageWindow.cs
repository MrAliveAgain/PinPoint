using System.ComponentModel;
using System.Runtime.InteropServices;
using PinPoint.Core.Diagnostics;

namespace PinPoint.Core.Interop;

/// <summary>Return true to mark the message handled and supply <paramref name="result"/>.</summary>
public delegate bool WndProcHook(uint msg, nint wParam, nint lParam, out nint result);

/// <summary>
/// A hidden top-level window that receives hotkey, tray and broadcast messages
/// (a true message-only window would miss the TaskbarCreated broadcast).
/// Must be created on the UI thread so its messages are pumped by the WinUI loop.
/// </summary>
public sealed unsafe class MessageWindow : IDisposable
{
    private const string ClassName = "PinPoint.MessageWindow";
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    private static readonly Dictionary<nint, MessageWindow> s_windows = [];
    private static bool s_classRegistered;

    private readonly List<WndProcHook> _hooks = [];

    public MessageWindow()
    {
        nint hInstance = Win32.GetModuleHandleW(null);
        EnsureClassRegistered(hInstance);

        Handle = Win32.CreateWindowExW((uint)Win32.WS_EX_TOOLWINDOW, ClassName, AppPaths.AppName, Win32.WS_POPUP,
            0, 0, 0, 0, 0, 0, hInstance, 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());

        s_windows[Handle] = this;
    }

    public nint Handle { get; private set; }

    public void AddHook(WndProcHook hook) => _hooks.Add(hook);

    public void RemoveHook(WndProcHook hook) => _hooks.Remove(hook);

    public void Dispose()
    {
        if (Handle == 0) return;
        s_windows.Remove(Handle);
        Win32.DestroyWindow(Handle);
        Handle = 0;
        _hooks.Clear();
    }

    private static void EnsureClassRegistered(nint hInstance)
    {
        if (s_classRegistered) return;

        fixed (char* name = ClassName)
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                hInstance = hInstance,
                lpszClassName = (nint)name,
            };

            if (Win32.RegisterClassExW(in wc) == 0 && Marshal.GetLastPInvokeError() != ERROR_CLASS_ALREADY_EXISTS)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (s_windows.TryGetValue(hwnd, out var window))
            {
                // Copy: a hook may add/remove hooks while running.
                foreach (var hook in window._hooks.ToArray())
                {
                    if (hook(msg, wParam, lParam, out nint result)) return result;
                }
            }
        }
        catch (Exception ex)
        {
            // Exceptions must never unwind into native code.
            Log.Error($"Unhandled exception in message 0x{msg:X4}", ex);
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
