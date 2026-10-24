# PinPoint

Pin any window, or any snipped part of the screen, on top of everything. That includes games running in borderless windowed mode. It's built for a single big monitor: keep a guide, map or build order floating over your game without alt-tabbing.

## Using it

| Shortcut (default) | What it does |
|---|---|
| **Ctrl+Alt+S** | **Snip & pin.** The screen freezes. Drag a region, or click a window to grab all of it. The snip pins exactly where it was. |
| **Ctrl+Alt+P** | Pin or unpin the window you're in (it gets a coloured border while pinned). |
| **Ctrl+Alt+H** | Hide or show every pin. |
| **Ctrl+Alt+G** | Click-through: clicks pass through pins into the game. |
| **Ctrl+Alt+Down / Up** | Fade / unfade the pin under the mouse (every pin if the mouse isn't over one). Works on pinned windows too, e.g. Chrome. |

You can rebind every shortcut in **Settings**. A warning appears there if another app already owns a combination.

**On a snip:** drag to move · scroll to zoom · **Alt+scroll** or **hold right-click + scroll** to fade · double-click to reset size · middle-click or **Esc** to close · right-click for copy, save as, opacity, click-through.

The fade key (Alt/Ctrl/Shift) is a setting. Holding the right mouse button while scrolling always works. Games that run as administrator hide keyboard state from normal apps, so a held key can go unseen while they're focused.

Snips are saved to `Pictures\PinPoint` (you can change this) and copied to the clipboard. PinPoint lives in the system tray: left-click opens it, right-click shows the menu and **Exit**.

**Limits:** exclusive-fullscreen games can't be overlaid by any app; switch them to borderless. To pin a window that runs as administrator, PinPoint must also run as administrator.

## What it touches on your PC

* `%LocalAppData%\PinPoint\` holds settings.json and logs.
* `Pictures\PinPoint\` holds saved snips.
* Optional **Start with Windows** adds one per-user Run key (`HKCU\…\Run\PinPoint`). It's off by default.
* Pinned windows get topmost, border colour and opacity changes. All of these are undone when you unpin or exit, and also if PinPoint crashes.

It needs no installer, no MSIX, no admin rights and no runtime install (the Windows App SDK ships alongside the exe).

## Build & run

Requires the .NET 10 SDK and Windows 11.

```bash
dotnet build src/PinPoint.App
```

```bash
dotnet test tests/PinPoint.Core.Tests
```

Run `src/PinPoint.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/PinPoint.exe`, or open `PinPoint.sln` in Visual Studio and press F5.

To make a standalone folder you can copy anywhere:

```bash
dotnet publish src/PinPoint.App -c Release -r win-x64 --self-contained -o publish
```

## Architecture

```
src/PinPoint.Core     UI-free, unit-tested: Win32 interop, hotkeys, window pinning,
                      screen capture, tray icon, settings
src/PinPoint.App      WinUI 3 (Windows App SDK 2.x): windows, pages, services
tests/                xUnit v3 tests for Core
```

* **`AppHost`** is the composition root. It creates every service and owns the tray, hotkeys and main window.
* **Commands** (`Commands/AppCommand.cs`) are the extension point. When you register an `AppCommand` in `AppHost.RegisterCommands`, it automatically gets a rebindable global shortcut on the Settings page and a tray-menu entry.
* **`PinManager`** is the single source of truth for pins (external windows plus snip windows). It re-raises pins when a topmost game grabs focus.
* **`SnipService`** runs the snip flow: capture, then `SnipOverlayWindow`, then `ImagePinWindow`, then save/copy. Capture sits behind `IScreenCapture`, so a Windows.Graphics.Capture backend can drop in later.
* Settings are plain JSON (`AppSettings`). New properties just need a default. Unknown or missing fields are tolerated, and a corrupt file is backed up rather than lost.
* Package versions are pinned centrally in `Directory.Packages.props`. The icon is regenerated with `tools/make-icon.ps1`.

### Ideas for later

Restore snips after a restart · magnifier/colour picker in the snip overlay · per-pin hotkeys · Windows.Graphics.Capture backend · MSIX/winget packaging.
