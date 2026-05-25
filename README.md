# WinIconFinder

[![Build](https://github.com/<your-username>/win-icon-finder/actions/workflows/build.yml/badge.svg)](https://github.com/<your-username>/win-icon-finder/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A WinUI 3 desktop application for browsing, sketching, and exporting the full [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) library (~1,600 icons). Find the right icon by name **or** by drawing it freehand — then copy it straight into your project.

---

## Features

### Search & Draw

**Name search** — a live-filtered list narrows icons as you type. Matched icons float to the top; every other icon remains visible in alphabetical order.

**Sketch-to-icon matching** — draw a rough shape on the freehand canvas. As you draw, the app renders your strokes to a 64 × 64 grayscale bitmap, computes an L2-normalised pixel vector, and scores every icon using cosine similarity against a pre-rendered vector database. The top 10 matches are highlighted with an accent tint and a percentage-match badge, and the best match is scrolled into view automatically.

Drawing tools in the toolbar:

| Control | Action |
|---|---|
| Pen size (S / M / L) | Sets stroke width (6 px / 10 px / 16 px) |
| Undo (Ctrl+Z) | Removes the last drawn stroke |
| Clear (Ctrl+L) | Clears the canvas and resets scores |
| Rotate 90° | Rotates all strokes clockwise before re-matching |
| Try mirrors | Also tests horizontally and vertically flipped versions of the sketch, useful for asymmetric icons |

The canvas displays two overlaid guides: a solid border for the full drawing area and a dashed blue line for the icon safe area, so you can proportion your sketch accurately.

### Similarity Map

Switch to the **Similarity Map** tab to see all icons laid out in a scrollable 2-D spiral grid. Click any icon to make it the *pivot*. Icons are then re-sorted so visually similar icons occupy the innermost spiral cells, giving you an at-a-glance neighbourhood view that helps you discover variants and related glyphs.

Map navigation:

| Gesture | Action |
|---|---|
| Scroll wheel | Zoom in/out at pointer |
| Pinch | Zoom in/out (touch/trackpad) |
| Drag | Pan |
| Tap / click | Set pivot icon |

Hovering over an icon shows its name in a tooltip. After selecting a pivot, the toolbar shows the same copy buttons as the Search view.

You can also jump directly from a search result to its map neighbourhood with the **Explore in Map** button in the Search & Draw toolbar.

### Export formats

Once an icon is selected, four one-click copy buttons appear:

| Button | Clipboard content |
|---|---|
| 📋 Unicode | `\uXXXX` escape sequence |
| `</>` XAML | WinUI 3 `<FontIcon>` snippet (includes font family URI) |
| 🖼 PNG | 256 × 256 PNG bitmap (black glyph on transparent background) |
| ✦ SVG | Scalable vector SVG with real path data extracted from the font outline, using `fill="currentColor"` for theme-awareness |

Right-clicking any icon in the list also opens a context menu with the same export options.

---

## How it works

### Icon loading

On startup, `FluentIconsService` reads the bundled `Assets/icons.json` file — a flat dictionary mapping glyph names (e.g. `ic_fluent_arrow_circle_up_24_regular`) to decimal Unicode codepoints. Icons are grouped by their base name, the 24 px variant is preferred, and each name is converted to title-case for display (`Arrow Circle Up`).

### Sketch matching

`IconMatchingService` pre-renders every icon glyph at 64 × 64 using Win2D and the bundled `FluentSystemIcons-Regular.ttf` font. Each bitmap is converted to a grayscale pixel array and L2-normalised into a unit vector. The full vector database is written to a binary cache file (`icon_vectors.bin` in `LocalCacheFolder`) on first launch. Subsequent launches load from cache in milliseconds — the cache is automatically invalidated if the icon set or render parameters change (tracked via a fingerprint hash).

When you draw, the same 64 × 64 pipeline is applied to your strokes (scaled to fill the canvas), then a brute-force dot-product search across all ~1,600 vectors finds the best cosine matches.

SVG export uses `CanvasGeometry.CreateText` + `ICanvasPathReceiver` to extract real cubic Bézier outlines from the font — no pixel rasterisation.

### Similarity map layout

`SimilarityLayoutService` generates a square outward spiral of grid coordinates. The pivot icon occupies cell (0, 0); all other icons are placed at spiral positions ordered by their cosine similarity to the pivot. Sorting is O(n log n) and completes in under 5 ms.

---

## Architecture

```
WinIconFinder/
├── Models/
│   └── FluentIcon.cs          # Icon data model (name, codepoint, match state)
├── Services/
│   ├── FluentIconsService.cs  # Loads icons.json, deduplicates variants
│   ├── IconMatchingService.cs # Win2D rendering, cosine similarity search, cache, PNG/SVG export
│   ├── ClipboardExportService.cs  # Copies glyph code, XAML, PNG, SVG to clipboard
│   └── SimilarityLayoutService.cs # Spiral grid layout for the similarity map
├── ViewModels/
│   └── MainPageViewModel.cs   # MVVM glue (CommunityToolkit.Mvvm), commands
├── MainPage.xaml / .cs        # Main UI — search panel, drawing canvas, similarity map
├── MainWindow.xaml / .cs      # App window host
└── Assets/
    ├── icons.json                      # Fluent icon name → codepoint mapping
    └── FluentSystemIcons-Regular.ttf   # Bundled icon font
```

**Dependencies**

| Package | Version | Purpose |
|---|---|---|
| Microsoft.WindowsAppSDK | 2.1.3 | WinUI 3, packaged app runtime |
| Microsoft.Graphics.Win2D | 1.4.0 | Hardware-accelerated 2D rendering |
| CommunityToolkit.Mvvm | 8.4.2 | Source-generated MVVM helpers |

Target framework: `.NET 10` — `net10.0-windows10.0.26100.0`  
Minimum OS: Windows 10 version 1809 (build 17763)  
Architectures: x86, x64, ARM64

---

## Requirements

- Windows 10 version 1809 or later
- [Visual Studio 2022](https://visualstudio.microsoft.com/) with the **Windows application development** workload  
  *or* the [.NET 10 SDK](https://dotnet.microsoft.com/download) with the `Microsoft.WindowsAppSDK` workload
- **Developer Mode** enabled (`Settings → System → For developers → Developer Mode`)

---

## Building & running

### Option 1 — `BuildAndRun.ps1` (recommended)

```powershell
.\BuildAndRun.ps1                        # Auto-detect platform, build + run
.\BuildAndRun.ps1 -SkipRun               # Build only
.\BuildAndRun.ps1 /p:Configuration=Release  # Release build + run
.\BuildAndRun.ps1 --detach               # Launch app in background
```

The script:
1. Verifies Developer Mode is enabled
2. Auto-detects the current CPU architecture (x64 / ARM64)
3. Locates MSBuild via `vswhere`, falling back to `dotnet build`
4. Builds the project and launches the packaged app with `winapp run`

### Option 2 — Visual Studio

Open `WinIconFinder.slnx`, select your target platform (x64 / ARM64), and press **F5**.

### Option 3 — dotnet CLI

```powershell
dotnet build WinIconFinder.csproj -p:Platform=x64
winapp run bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\
```

> **Note:** `winapp` is the [Windows App SDK CLI](https://learn.microsoft.com/windows/apps/windows-app-sdk/deploy-packaged-apps) helper that registers the debug package identity required to run packaged WinUI apps outside of Visual Studio.

---

## First launch

On first launch, the app pre-renders all ~1,600 icons to 64 × 64 bitmaps and saves them to the cache. A loading overlay with a progress bar is shown during this phase. On subsequent launches the cache is loaded instantly and the overlay disappears in under a second.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build instructions, code style guidance, and how to submit a pull request.

## Third-party assets

The bundled font (`FluentSystemIcons-Regular.ttf`) and codepoint map (`icons.json`) are from Microsoft's [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) project, licensed under the **MIT License**.

## License

This project is licensed under the [MIT License](LICENSE).
