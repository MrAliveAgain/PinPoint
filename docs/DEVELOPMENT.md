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
* **Boards** have their own section below.
* **Notes** are stored by `NoteStore` (Core, unit-tested): one `.txt` per note plus `layout.json` for position, size and opacity. Closing a note with text moves it to `notes\closed`.
* **`SnipService`** runs the snip flow: capture → `SnipOverlayWindow` → `ImagePinWindow` → save/copy. Capture sits behind `IScreenCapture`, so a Windows.Graphics.Capture backend can be swapped in.
* **Settings** are plain JSON (`AppSettings`) in `%LocalAppData%\PinPoint`. A new setting is just a property with a default. Unknown or missing fields are tolerated, and a corrupt file is backed up rather than lost.
* Package versions live in one place: `Directory.Packages.props`.

## Boards

* **Documents.** `BoardService` treats a board like a document. The open board autosaves every 2 s if its snapshot changed (snips' bounds, zoom, opacity, lock and strokes; notes' text and bounds), writing each snip's PNG once, off the UI thread. `BoardStore` (Core, tested) keeps one folder per board id with `board.json` plus `{snipId}.png`. While a board is open, `PinManager.BoardMode` makes new notes belong to the board instead of the notes folder. Pinned windows are never part of a board.
* **Loose snips** (no board open) are autosaved by the same code into a hidden session board (`AppPaths.SessionDirectory`), so they come back after a restart; notes keep their own `NoteStore`. Opening or saving a board clears the session.
* **Deleting** sends the folder to the Recycle Bin (`BoardStore(useRecycleBin: true)`; tests use the default, a real delete).
* **Tray.** `AppHost.BuildBoardsMenu` lists recent boards. Opening one from there never shows a dialog: pins that aren't in a board yet are saved as a new board first.
* **Pictures** (`BoardService.Images.cs`, `CollageRenderer`) are drawn with Win2D, off the UI thread:
  * `CollageLayout` (Core, tested) crops the on-screen layout; `CollagePacker` (Core, tested) packs pins into rows instead ("Compact").
  * Snips, their strokes and notes are drawn the way their windows look. Exporting what's on screen can add snapshots of pinned windows (`WindowCapture`, PrintWindow), in the image only.
  * Backgrounds (`CollageBackground`): theme grey, solid colour, dot grid, transparent, or the user's picture, covering the image or tiled at one picture pixel per screen pixel.
  * Thumbnails are a cache (`thumbnail-v2.png` in the board folder), redrawn when older than `board.json`.
* **Choosing pins.** Each pin is also drawn on its own (`CollageRenderer.RenderItemPngAsync`) for `PinPicker`, which the board preview popup (`BoardPreviewFlyout`) and the export dialog (`BoardExport`) share: click to tick, hover for a full-size `PinPeekWindow`, right-click to copy or save one pin. The export dialog redraws the exact picture (debounced) as the choice changes.

## Gotchas

* **Clipboard images** must go in as a native `InMemoryRandomAccessStream`, followed by `Clipboard.Flush()` (see `SnipStorage.CopyPngToClipboardAsync`). A .NET `MemoryStream` wrapper looks fine but leaves the clipboard empty.
* **OCR** (`OcrEngine`) aborts a `RecognizeAsync` while another is running, so `OcrService` queues them. Opening a board starts several at once.
* **The hover peek** is a layered window with `WS_EX_TRANSPARENT`, so the mouse stays on the pin underneath. Moving off that pin is what hides it. Don't make it hit-testable or it flickers.
* **Thumbnail look changed?** Bump `BoardStore.ThumbnailFile`, otherwise users keep the old cached ones.

## Text recognition (OCR)

`OcrService` wraps Windows' built-in `Windows.Media.Ocr` (offline, uses the user's installed languages). Each snip is read once in the background, enlarged 2× first because small UI text recognises much better that way. `RecognizedText` (in Core, unit-tested) turns the result into reading order and rebuilds table rows, so copied tables paste as tab-separated rows. The snip window's text mode lives in `ImagePinWindow.Text.cs`.

To check OCR still works in a trimmed release build without clicking through the UI:

```bash
PinPoint.exe --ocr-selftest some-image.png out.txt
```

It writes the recognised text to `out.txt` and exits with 0 (text found), 2 (no text) or 1 (error). It runs without UI, so it works even while another PinPoint is open.

Board pictures have the same kind of check. Add `thumbnail`, `packed`, `pin`, `dotgrid`, `transparent`, `#RRGGBB`, `image=path` or `tile=path` for those variants:

```bash
PinPoint.exe --board-selftest board-20261025-040115-888 out.png
```

It exits with 0 (drawn), 2 (no such board, or nothing on it) or 1 (error, details in `out.png.error.txt`).

## Ideas

* Magnifier and colour picker in the snip overlay
* Per-pin shortcuts
* Windows.Graphics.Capture backend
* winget / MSIX packaging
