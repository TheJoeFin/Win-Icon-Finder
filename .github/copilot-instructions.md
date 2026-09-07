# Copilot Instructions for Draw Symbol Finder

## Build & Run

```powershell
# Build + run (recommended)
.\BuildAndRun.ps1

# Build only
.\BuildAndRun.ps1 -SkipRun

# Release build only
.\BuildAndRun.ps1 /p:Configuration=Release -SkipRun

# Direct dotnet build
dotnet build DrawSymbolFinder.csproj -p:Platform=x64
```

**Prerequisite:** Developer Mode must be enabled (`Settings → System → For developers → Developer Mode`). The `BuildAndRun.ps1` script checks this automatically.

There are no automated tests in this project.

## Architecture

Single-project WinUI 3 app (`net10.0-windows10.0.26100.0`) targeting Windows 10 1809+. Entry point is `App.xaml.cs` → `MainWindow` (thin shell with Mica backdrop) → `MainPage` (all UI logic).

```
Models/         FluentIcon.cs             — icon data model (name, codepoint, match state)
Services/       FluentIconsService.cs     — loads Assets/icons.json, deduplicates size variants
                IconMatchingService.cs    — Win2D rendering, cosine similarity search, binary cache, PNG/SVG export
                ClipboardExportService.cs — copies glyph code, XAML snippet, PNG, SVG to clipboard
                SimilarityLayoutService.cs — spiral grid layout for the similarity map
ViewModels/     MainPageViewModel.cs      — CommunityToolkit.Mvvm source-generated ViewModel
MainPage.xaml/.cs                         — all UI: search panel, Win2D drawing canvas, similarity map
Assets/         icons.json               — flat dict: glyph name → decimal codepoint
                FluentSystemIcons-Regular.ttf — bundled icon font
```

### Key data flow

1. **Startup:** `MainViewModel.InitializeAsync()` → `FluentIconsService.LoadAsync()` reads `icons.json`, groups by base name, picks the 24 px variant per icon → `IconMatchingService.InitializeAsync()` pre-renders every icon to a 64×64 grayscale bitmap with Win2D, L2-normalises each into a float vector, and saves to `LocalCacheFolder/icon_vectors.bin`. Cache is invalidated via a fingerprint hash if the icon set changes.

2. **Sketch search:** User draws on the Win2D `CanvasControl` in `MainPage`; strokes are debounced (500 ms) → `MainViewModel.SearchByInkAsync()` → `IconMatchingService.RenderInkToBitmap()` renders strokes to 64×64 → brute-force dot-product cosine similarity against all cached vectors → top 10 results update `FluentIcon.IsMatch`/`MatchScore` → `ApplyFilter()` re-sorts `FilteredIcons`.

3. **Similarity map:** `SimilarityLayoutService` generates a square outward spiral. Clicking a pivot icon sorts all icons by cosine similarity to the pivot; `_mapSimilarities` array drives per-cell tinting in the `CanvasControl` draw event.

4. **Export:** `ClipboardExportService` handles Unicode escape, `<FontIcon>` XAML, PNG (256×256 via Win2D), and SVG (outlines extracted via `CanvasGeometry.CreateText` + `ICanvasPathReceiver`).

## Key Conventions

- **MVVM:** Business logic lives in `ViewModels/` and `Services/`. `MainPage` code-behind is limited to Win2D canvas events, gesture handling, pointer input, and UI-only wiring. Do not add data logic to code-behind.

- **CommunityToolkit.Mvvm source generation:** Properties use `[ObservableProperty]` on `partial` properties (not fields). Commands use `[RelayCommand]`. The ViewModel class must be `partial`.

- **ViewModel ↔ code-behind communication:** The ViewModel exposes events (`RequestClearCanvas`, `RequestResearch`, `TopMatchesFound`, `MapModeChanged`) that code-behind subscribes to, rather than the ViewModel holding UI references.

- **x:Bind (compiled bindings):** All XAML bindings use `x:Bind` with `Mode=OneWay` or `Mode=TwoWay` as appropriate. Avoid `{Binding}` (runtime reflection).

- **Win2D rendering:** All 2D drawing (canvas, icon pre-rendering, PNG export, SVG path extraction) uses `Microsoft.Graphics.Win2D`. The shared `CanvasDevice` is captured on the UI thread before any `Task.Run` offload.

- **Nullable reference types:** Enabled project-wide. All code must be null-safe; use `?` annotations and null checks rather than suppressing warnings with `!`.

- **Font URI:** The bundled TTF is referenced as `ms-appx:///Assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular` throughout the services.

- **Cache invalidation:** `IconMatchingService.CacheFormatVersion` (const int) must be bumped whenever `GlyphSize`, `BaseFontSize`, or rendering logic changes to invalidate stale user caches automatically.

- **Icon variant selection:** `FluentIconsService` only loads `_regular` variants and picks the size closest to 24 px per base name. Only codepoints in the BMP (`<= 0xFFFF`) are included.
