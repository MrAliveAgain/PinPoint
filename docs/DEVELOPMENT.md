# Developing PinPoint

## Requirements

* Windows 11
* .NET 10 SDK
* Visual Studio 2026 with the WinUI workload is optional; the command line works fine.

## Build, test, run

```bash
dotnet build src/PinPoint.App
dotnet test tests/PinPoint.Core.Tests
```

The app lands in `src/PinPoint.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/PinPoint.exe`. Quit any running copy from the tray first, because Windows locks the exe while it runs.

Release build (self-contained; nothing needs installing on the target PC):

```bash
dotnet publish src/PinPoint.App -c Release -r win-x64 --self-contained -o publish/PinPoint
```

## Layout

```
src/PinPoint.Core     UI-free, unit-tested: Win32 interop, hotkeys, window pinning,
                      screen capture, tray icon, settings
src/PinPoint.App      WinUI 3 (Windows App SDK 2.x): windows, pages, services
tests/                xUnit v3 tests for Core
tools/make-icon.ps1   regenerates the app icon
```

## How it fits together

* **`AppHost`** is the composition root. It creates every service and owns the tray icon, hotkeys and the main window.
* **Commands** (`Commands/AppCommand.cs`) are the main extension point. Register an `AppCommand` in `AppHost.RegisterCommands` and it automatically gets a rebindable global shortcut on the Settings page and, if you want, a tray-menu entry.
* **`PinManager`** is the single source of truth for pins: other apps' windows plus PinPoint's own snip windows. It also pushes pins back on top when a topmost game grabs focus.
* **`SnipService`** runs the snip flow: capture → `SnipOverlayWindow` → `ImagePinWindow` → save/copy. Capture sits behind `IScreenCapture`, so a Windows.Graphics.Capture backend can be swapped in.
* **Settings** are plain JSON (`AppSettings`) in `%LocalAppData%\PinPoint`. A new setting is just a property with a default. Unknown or missing fields are tolerated, and a corrupt file is backed up rather than lost.
* Package versions live in one place: `Directory.Packages.props`.

## Ideas

* Bring snips back after a restart
* Magnifier and colour picker in the snip overlay
* Per-pin shortcuts
* Windows.Graphics.Capture backend
* winget / MSIX packaging
