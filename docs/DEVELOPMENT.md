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
* **`PinManager`** is the single source of truth for pins: other apps' windows plus PinPoint's own windows. It also pushes pins back on top when a topmost game grabs focus.
* **`IPinSurface`** is what a PinPoint-owned pin window implements (today `ImagePinWindow` for snips and `NotePinWindow` for notes). PinManager drives all surfaces the same way (re-raising, hide all, click-through, fading), so a new pin kind is one window class plus a `PinItem` for the Pins list. `WindowDragger` handles drag-to-move for borderless windows.
* **Drawing** on snips lives in `ImagePinWindow.Ink.cs`; the undo/redo history (`StrokeHistory`) and stroke hit-testing (`StrokeGeometry`) are in Core and unit-tested. Strokes are in snip pixels on a Viewbox-scaled canvas, so they follow zoom for free. Drawings are an overlay: saved and copied snips are the original image.
* **Boards** (`BoardService`) behave like documents. The open board autosaves every 2 s if its snapshot changed (snips' bounds, zoom, opacity and strokes; notes' text and bounds), writing each snip's PNG once off the UI thread. `BoardStore` (Core, tested) keeps one folder per board id with `board.json` plus `{snipId}.png`. While a board is open, `PinManager.BoardMode` makes new notes belong to the board instead of the notes folder. Pinned windows are never part of a board.
* **Loose snips** (no board open) are autosaved by the same code into a hidden session board (`AppPaths.SessionDirectory`), so they come back after a restart; notes keep their own `NoteStore`. Opening or saving a board clears the session. Deleted boards go to the Recycle Bin (`BoardStore(useRecycleBin: true)`).
* **Board images** (export and the Boards page thumbnails) are drawn by `CollageRenderer` with Win2D, off the UI thread: the on-screen layout cropped by `CollageLayout` (Core, tested), with snips, their strokes and notes drawn the way their windows look. Exporting what's on screen can add a snapshot of pinned windows (`WindowCapture`, PrintWindow), into the image only. Thumbnails are a cache (`thumbnail-v2.png` in the board folder), redrawn when older than `board.json`. "Pack together" (`CollagePacker`, Core, tested) puts pins in tidy rows instead. `BoardPreviewWindow` shows each pin separately (`CollageRenderer.RenderItemPngAsync`) so it can enlarge one on hover and copy or save it alone.
* **Notes** are stored by `NoteStore` (Core, unit-tested): one `.txt` per note plus `layout.json` for position, size and opacity. Closing a note with text moves it to `notes\closed`.
* **`SnipService`** runs the snip flow: capture → `SnipOverlayWindow` → `ImagePinWindow` → save/copy. Capture sits behind `IScreenCapture`, so a Windows.Graphics.Capture backend can be swapped in.
* **Settings** are plain JSON (`AppSettings`) in `%LocalAppData%\PinPoint`. A new setting is just a property with a default. Unknown or missing fields are tolerated, and a corrupt file is backed up rather than lost.
* Package versions live in one place: `Directory.Packages.props`.

## Text recognition (OCR)

`OcrService` wraps Windows' built-in `Windows.Media.Ocr` (offline, uses the user's installed languages). Each snip is read once in the background, enlarged 2× first because small UI text recognises much better that way. `RecognizedText` (in Core, unit-tested) turns the result into reading order and rebuilds table rows, so copied tables paste as tab-separated rows. The snip window's text mode lives in `ImagePinWindow.Text.cs`.

To check OCR still works in a trimmed release build without clicking through the UI:

```bash
PinPoint.exe --ocr-selftest some-image.png out.txt
```

It writes the recognised text to `out.txt` and exits with 0 (text found), 2 (no text) or 1 (error). It runs without UI, so it works even while another PinPoint is open.

Board images have the same kind of check (add `thumbnail`, `packed` or `pin` for those variants):

```bash
PinPoint.exe --board-selftest board-20261025-040115-888 out.png
```

It exits with 0 (drawn), 2 (no such board, or nothing on it) or 1 (error, details in `out.png.error.txt`).

## Ideas

* Magnifier and colour picker in the snip overlay
* Per-pin shortcuts
* Windows.Graphics.Capture backend
* winget / MSIX packaging
