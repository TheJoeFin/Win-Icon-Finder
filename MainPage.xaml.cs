using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinIconFinder.Models;
using WinIconFinder.Services;
using WinIconFinder.ViewModels;
using WinRT.Interop;

namespace WinIconFinder;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; } = new();

    // Debounce timer: fires 500 ms after the last pointer movement
    private readonly DispatcherTimer _debounceTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // Stroke storage for Win2D drawing
    private readonly List<List<Point>> _allStrokes = [];
    private List<Point>? _currentStroke;
    private bool _isDrawing;

    // Pen width in canvas-logical pixels
    private float _penWidth = 36f;

    // Tracks the current square canvas side length for stroke rescaling on resize
    private double _canvasSize;

    // ── Similarity map state ──────────────────────────────────────────────────
    private float _mapScale = 1f;
    private float _mapPanX = 0f;
    private float _mapPanY = 0f;
    private bool _mapIsDragging = false;
    private Point _mapDragStartPointer;
    private float _mapPanXAtDragStart = 0f;
    private float _mapPanYAtDragStart = 0f;
    private bool _mapDragHasMoved = false;
    private int _mapHoveredIndex = -1;   // index into LayoutService.Positions
    private int _mapPivotIconIdx = -1;   // index into AllIcons / GlyphVectors
    private float[]? _mapSimilarities;   // [iconIdx] → cosine similarity to pivot
    private const float MapCellSize = 26f; // logical pixels per grid cell at scale=1

    // Multi-touch pinch-to-zoom state
    private readonly Dictionary<uint, Point> _mapActivePointers = [];
    private float _mapPinchStartScale;
    private float _mapPinchStartPanX, _mapPinchStartPanY;
    private double _mapPinchStartDist;
    private Point _mapPinchStartMid;
    private bool _mapPinchOccurred = false; // suppresses tap-to-pivot after any pinch gesture

    public MainPage()
    {
        InitializeComponent();
        NavView.SelectedItem = SearchModeNavItem;

        _debounceTimer.Tick += async (_, _) =>
        {
            _debounceTimer.Stop();
            await ViewModel.SearchByInkAsync(
                _allStrokes.Select(s => (IReadOnlyList<Point>)s).ToList(),
                DrawingCanvas.RenderSize);
        };

        ViewModel.RequestClearCanvas += () =>
        {
            _allStrokes.Clear();
            _currentStroke = null;
            _isDrawing = false;
            DrawingCanvas.Invalidate();
            EmptyStateText.Visibility = Visibility.Visible;
        };

        ViewModel.RequestResearch += async () =>
        {
            if (_allStrokes.Count == 0) return;
            await ViewModel.SearchByInkAsync(
                _allStrokes.Select(s => (IReadOnlyList<Point>)s).ToList(),
                DrawingCanvas.RenderSize);
        };

        ViewModel.TopMatchesFound += matches =>
        {
            if (matches.Count > 0)
                IconListView.ScrollIntoView(matches[0]);
        };

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedIcon))
                DrawingCanvas.Invalidate();
            // Refresh map as soon as loading finishes (avoids "not ready" guard hit)
            if (e.PropertyName == nameof(ViewModel.IsBusy) && !ViewModel.IsBusy)
                MapCanvas.Invalidate();
            if (e.PropertyName == nameof(ViewModel.SelectedCollection))
                CollectionIconsListView.SelectedItems.Clear();
        };

        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Default pen is medium (36px)
        PenMediumButton.IsChecked = true;

        LoadingOverlay.Visibility = Visibility.Visible;
        await ViewModel.InitializeAsync();
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    // -------------------------------------------------------------------------
    // Square canvas constraint
    // -------------------------------------------------------------------------

    private void CanvasHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double margin = 48.0; // breathing room so the legend fits below
        double side = Math.Min(e.NewSize.Width, e.NewSize.Height) - margin;
        side = Math.Max(side, 120);

        // Rescale all stored stroke points so ink stays proportionally correct
        if (_canvasSize > 0 && side != _canvasSize)
        {
            double scale = side / _canvasSize;
            foreach (List<Point> stroke in _allStrokes)
                for (int i = 0; i < stroke.Count; i++)
                    stroke[i] = new Point(stroke[i].X * scale, stroke[i].Y * scale);

            if (_currentStroke is not null)
                for (int i = 0; i < _currentStroke.Count; i++)
                    _currentStroke[i] = new Point(_currentStroke[i].X * scale, _currentStroke[i].Y * scale);
        }

        _canvasSize = side;
        DrawingCanvas.Width = side;
        DrawingCanvas.Height = side;
        DrawingCanvas.Invalidate();
    }

    // -------------------------------------------------------------------------
    // Win2D CanvasControl — Draw event
    // -------------------------------------------------------------------------

    private void DrawingCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;

        ds.Clear(ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(255, 30, 30, 30)
            : Color.FromArgb(255, 250, 250, 250));

        DrawGuideLines(ds, (float)sender.ActualWidth, (float)sender.ActualHeight);

        if (ViewModel.SelectedIcon is { } overlayIcon)
            DrawIconOverlay(ds, sender, overlayIcon);

        foreach (List<Point> stroke in _allStrokes)
            DrawStroke(ds, stroke);

        if (_currentStroke is { Count: > 0 })
            DrawStroke(ds, _currentStroke);
    }

    private void DrawIconOverlay(CanvasDrawingSession ds, CanvasControl sender, FluentIcon icon)
    {
        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;
        float side = Math.Min(w, h);

        Color overlayColor = ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(50, 255, 255, 255)
            : Color.FromArgb(50, 0, 0, 0);

        // Mirror exactly what the matching algorithm does: BaseFontSize inside a GlyphSize×GlyphSize
        // canvas, centered — so the overlay shows the icon precisely as the algorithm sees it.
        float fontSize = side * (IconMatchingService.BaseFontSize / IconMatchingService.GlyphSize);

        using CanvasTextFormat textFormat = new()
        {
            FontFamily = IconMatchingService.FontUri,
            FontSize = fontSize,
            HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
            WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
        };

        ds.DrawText(icon.GlyphString, new Rect(0, 0, w, h), overlayColor, textFormat);
    }

    /// <summary>
    /// Draws two overlaid guides:
    ///   • a thin gray border showing the full canvas extent
    ///   • a dashed blue rectangle at the icon safe-area inset (~10 % per side,
    ///     matching the padding Fluent icons use within their bounding box)
    /// </summary>
    private void DrawGuideLines(CanvasDrawingSession ds, float w, float h)
    {
        // Outer canvas boundary
        Color borderColor = Color.FromArgb(55, 140, 140, 140);
        ds.DrawRectangle(0.5f, 0.5f, w - 1f, h - 1f, borderColor, 1f);

        // Corner ticks (small L marks at each corner to reinforce the boundary)
        float tick = Math.Max(8f, w * 0.04f);
        ds.DrawLine(0, 0, tick, 0, borderColor, 1f);
        ds.DrawLine(0, 0, 0, tick, borderColor, 1f);
        ds.DrawLine(w, 0, w - tick, 0, borderColor, 1f);
        ds.DrawLine(w, 0, w, tick, borderColor, 1f);
        ds.DrawLine(0, h, tick, h, borderColor, 1f);
        ds.DrawLine(0, h, 0, h - tick, borderColor, 1f);
        ds.DrawLine(w, h, w - tick, h, borderColor, 1f);
        ds.DrawLine(w, h, w, h - tick, borderColor, 1f);

        // Safe-area dashed rectangle (~10 % inset, matching Fluent icon padding)
        float inset = w * 0.10f;
        Rect safeRect = new(inset, inset, w - inset * 2, h - inset * 2);
        CanvasStrokeStyle dashStyle = new()
        {
            DashStyle = Microsoft.Graphics.Canvas.Geometry.CanvasDashStyle.Dash
        };
        ds.DrawRoundedRectangle(
            (float)safeRect.X, (float)safeRect.Y,
            (float)safeRect.Width, (float)safeRect.Height,
            3f, 3f,
            Color.FromArgb(160, 0, 120, 212),
            1.5f,
            dashStyle);
    }

    private void DrawStroke(CanvasDrawingSession ds, List<Point> pts)
    {
        if (pts.Count == 0) return;

        Color inkColor = ActualTheme == ElementTheme.Dark
            ? Colors.White
            : Colors.Black;

        if (pts.Count == 1)
        {
            ds.FillCircle((float)pts[0].X, (float)pts[0].Y, _penWidth / 2, inkColor);
            return;
        }

        CanvasStrokeStyle strokeStyle = new()
        {
            StartCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
            EndCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
            LineJoin = Microsoft.Graphics.Canvas.Geometry.CanvasLineJoin.Round
        };

        for (int i = 0; i < pts.Count - 1; i++)
        {
            ds.DrawLine(
                (float)pts[i].X, (float)pts[i].Y,
                (float)pts[i + 1].X, (float)pts[i + 1].Y,
                inkColor, _penWidth, strokeStyle);
        }
    }

    // -------------------------------------------------------------------------
    // Win2D CanvasControl — Pointer events
    // -------------------------------------------------------------------------

    private void DrawingCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        DrawingCanvas.CapturePointer(e.Pointer);
        _isDrawing = true;
        _currentStroke = [e.GetCurrentPoint(DrawingCanvas).Position];
        EmptyStateText.Visibility = Visibility.Collapsed;
        DrawingCanvas.Invalidate();
        e.Handled = true;
    }

    private void DrawingCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawing || _currentStroke is null) return;

        foreach (PointerPoint? pt in e.GetIntermediatePoints(DrawingCanvas))
            _currentStroke.Add(pt.Position);

        DrawingCanvas.Invalidate();

        _debounceTimer.Stop();
        _debounceTimer.Start();

        e.Handled = true;
    }

    private void DrawingCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinalizeStroke(e);
        e.Handled = true;
    }

    private void DrawingCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => FinalizeStroke(e);

    private void FinalizeStroke(PointerRoutedEventArgs e)
    {
        if (!_isDrawing) return;
        _isDrawing = false;

        if (_currentStroke is { Count: > 0 })
        {
            _currentStroke.Add(e.GetCurrentPoint(DrawingCanvas).Position);
            _allStrokes.Add(_currentStroke);
        }

        _currentStroke = null;
        DrawingCanvas.Invalidate();

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    // -------------------------------------------------------------------------
    // Pen-width buttons
    // -------------------------------------------------------------------------

    private void UndoStroke_Click(object sender, RoutedEventArgs e)
    {
        if (_allStrokes.Count == 0) return;

        _allStrokes.RemoveAt(_allStrokes.Count - 1);
        DrawingCanvas.Invalidate();

        if (_allStrokes.Count == 0)
            EmptyStateText.Visibility = Visibility.Visible;

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void RotateInk_Click(object sender, RoutedEventArgs e)
    {
        if (_allStrokes.Count == 0) return;

        double s = _canvasSize;
        foreach (List<Point> stroke in _allStrokes)
            for (int i = 0; i < stroke.Count; i++)
            {
                double x = stroke[i].X, y = stroke[i].Y;
                // 90° clockwise rotation around canvas centre (s/2, s/2): (x,y) → (y, s−x)
                stroke[i] = new Point(y, s - x);
            }

        DrawingCanvas.Invalidate();
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void PenSmall_Click(object sender, RoutedEventArgs e) => SetActivePen(PenSmallButton, 20f);
    private void PenMedium_Click(object sender, RoutedEventArgs e) => SetActivePen(PenMediumButton, 36f);
    private void PenLarge_Click(object sender, RoutedEventArgs e) => SetActivePen(PenLargeButton, 56f);

    private void SetActivePen(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton active, float width)
    {
        _penWidth = width;
        PenSmallButton.IsChecked = ReferenceEquals(active, PenSmallButton);
        PenMediumButton.IsChecked = ReferenceEquals(active, PenMediumButton);
        PenLargeButton.IsChecked = ReferenceEquals(active, PenLargeButton);
    }

    // -------------------------------------------------------------------------
    // Per-item context menu (RightTapped on DataTemplate Grid)
    // -------------------------------------------------------------------------

    private void IconItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not FluentIcon icon) return;

        ShowItemContextMenu(fe, e.GetPosition(fe), icon);
        e.Handled = true;
    }

    private MenuFlyout BuildItemContextMenu(FluentIcon icon)
    {
        MenuFlyout flyout = new();

        ToggleMenuFlyoutItem favorite = new()
        {
            Text = "Favorite in Default",
            IsChecked = icon.IsFavorite
        };
        favorite.Click += async (_, _) => await ViewModel.ToggleFavoriteAsync(icon);

        MenuFlyoutSubItem collections = BuildCollectionsSubMenu(icon);

        MenuFlyoutItem copyGlyph = new()
        {
            Text = "Copy glyph code",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        AutomationProperties.SetAutomationId(copyGlyph, "CtxCopyGlyph");
        copyGlyph.Click += CopyGlyph_Click;

        MenuFlyoutItem copyXaml = new()
        {
            Text = "Copy as XAML FontIcon",
            Icon = new FontIcon { Glyph = "\uE943" }
        };
        AutomationProperties.SetAutomationId(copyXaml, "CtxCopyXaml");
        copyXaml.Click += CopyXaml_Click;

        MenuFlyoutItem copyPng = new()
        {
            Text = "Copy as PNG",
            Icon = new FontIcon { Glyph = "\uEB9F" }
        };
        AutomationProperties.SetAutomationId(copyPng, "CtxCopyPng");
        copyPng.Click += CopyPng_Click;

        MenuFlyoutItem copySvg = new()
        {
            Text = "Copy as SVG",
            Icon = new FontIcon { Glyph = "\uE71B" }
        };
        AutomationProperties.SetAutomationId(copySvg, "CtxCopySvg");
        copySvg.Click += CopySvg_Click;

        flyout.Items.Add(favorite);
        flyout.Items.Add(collections);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(copyGlyph);
        flyout.Items.Add(copyXaml);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(copyPng);
        flyout.Items.Add(copySvg);
        return flyout;
    }

    private MenuFlyoutSubItem BuildCollectionsSubMenu(FluentIcon icon)
    {
        MenuFlyoutSubItem collections = new()
        {
            Text = "Collections"
        };

        foreach (IconCollection collection in ViewModel.Collections)
        {
            ToggleMenuFlyoutItem item = new()
            {
                Text = collection.Name,
                IsChecked = ViewModel.IsIconInCollection(icon, collection.Name)
            };
            item.Click += async (_, _) =>
                await ViewModel.SetIconCollectionMembershipAsync(icon, collection.Name, item.IsChecked);

            collections.Items.Add(item);
        }

        collections.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem newCollection = new()
        {
            Text = "New collection…"
        };
        newCollection.Click += async (_, _) => await CreateCollectionFromSelectionAsync([icon]);
        collections.Items.Add(newCollection);

        return collections;
    }

    private async void CopyGlyph_Click(object sender, RoutedEventArgs e) =>
        await CopyGlyphWithFormatAsync(sender);

    private async Task CopyGlyphWithFormatAsync(object sender)
    {
        if (!TrySelectActionIcon(sender))
        {
            return;
        }

        ContentDialog dialog = new()
        {
            Title = "Glyph code format",
            Content = "Choose the format for the glyph code.",
            PrimaryButtonText = "C# (\\uXXXX)",
            SecondaryButtonText = "XAML (&#xXXXX;)",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        ViewModel.CopyAsGlyphCommand.Execute(result == ContentDialogResult.Secondary);
    }

    private void CopyXaml_Click(object sender, RoutedEventArgs e) =>
        ExecuteCopyAction(sender, () => ViewModel.CopyAsXamlCommand.Execute(null));

    private async void CopyPng_Click(object sender, RoutedEventArgs e) =>
        await CopyPngWithColorChoiceAsync(sender);

    private async Task CopyPngWithColorChoiceAsync(object sender)
    {
        if (!TrySelectActionIcon(sender))
        {
            return;
        }

        bool? useBlack = await PromptForPngColorChoiceAsync();
        if (!useBlack.HasValue) return;

        await ViewModel.CopyAsPngCommand.ExecuteAsync(useBlack.Value);
    }

    private void CopySvg_Click(object sender, RoutedEventArgs e) =>
        ExecuteCopyAction(sender, () => ViewModel.CopyAsSvgCommand.Execute(null));

    // -------------------------------------------------------------------------
    // Mode switch
    // -------------------------------------------------------------------------

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.SelectedItem, SimilarityMapNavItem))
        {
            SearchModePanel.Visibility = Visibility.Collapsed;
            SimilarityMapPanel.Visibility = Visibility.Visible;
            CollectionsPanel.Visibility = Visibility.Collapsed;
            ViewModel.IsMapMode = true;
            MapHintText.Visibility = _mapPivotIconIdx >= 0 ? Visibility.Collapsed : Visibility.Visible;
            if (_mapPivotIconIdx < 0)
                FitGridToCanvas();
            MapCanvas.Invalidate();
        }
        else if (ReferenceEquals(e.SelectedItem, CollectionsNavItem))
        {
            SearchModePanel.Visibility = Visibility.Collapsed;
            SimilarityMapPanel.Visibility = Visibility.Collapsed;
            CollectionsPanel.Visibility = Visibility.Visible;
            ViewModel.IsMapMode = false;
        }
        else
        {
            SearchModePanel.Visibility = Visibility.Visible;
            SimilarityMapPanel.Visibility = Visibility.Collapsed;
            CollectionsPanel.Visibility = Visibility.Collapsed;
            ViewModel.IsMapMode = false;
        }
    }

    private async void FavoriteIcon_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySelectActionIcon(sender, out FluentIcon icon))
        {
            return;
        }

        await ViewModel.ToggleFavoriteAsync(icon);
    }

    private void IconRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetHoverActionsVisible(sender, isVisible: true);
    }

    private void IconRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetHoverActionsVisible(sender, isVisible: false);
    }

    private void ManageCollections_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || !TrySelectActionIcon(sender, out FluentIcon icon))
        {
            return;
        }

        MenuFlyout flyout = new();
        foreach (IconCollection collection in ViewModel.Collections)
        {
            ToggleMenuFlyoutItem item = new()
            {
                Text = collection.Name,
                IsChecked = ViewModel.IsIconInCollection(icon, collection.Name)
            };
            item.Click += async (_, _) =>
                await ViewModel.SetIconCollectionMembershipAsync(icon, collection.Name, item.IsChecked);

            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem newCollection = new()
        {
            Text = "New collection…"
        };
        newCollection.Click += async (_, _) => await CreateCollectionFromSelectionAsync([icon]);
        flyout.Items.Add(newCollection);

        flyout.ShowAt(element);
    }

    private void MapCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!ViewModel.LayoutService.IsReady)
        {
            return;
        }

        float width = (float)MapCanvas.ActualWidth;
        float height = (float)MapCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Point point = e.GetPosition(MapCanvas);
        int hitIndex = MapHitTest((float)point.X, (float)point.Y, width, height);
        if (hitIndex < 0 || hitIndex >= ViewModel.LayoutService.Positions.Count)
        {
            return;
        }

        _mapHoveredIndex = hitIndex;
        MapCanvas.Invalidate();

        ShowItemContextMenu(MapCanvas, point, ViewModel.LayoutService.Positions[hitIndex].Icon);
        e.Handled = true;
    }

    private static void SetHoverActionsVisible(object sender, bool isVisible)
    {
        if (sender is FrameworkElement row && row.FindName("HoverActionsPanel") is UIElement actions)
        {
            actions.Opacity = isVisible ? 1.0 : 0.0;
            actions.IsHitTestVisible = isVisible;
        }
    }

    private void ShowItemContextMenu(FrameworkElement target, Point position, FluentIcon icon)
    {
        ViewModel.SelectedIcon = icon;
        BuildItemContextMenu(icon).ShowAt(target, position);
    }

    private void ExecuteCopyAction(object sender, Action action)
    {
        if (!TrySelectActionIcon(sender))
        {
            return;
        }

        action();
    }

    private bool TrySelectActionIcon(object sender) =>
        TrySelectActionIcon(sender, out _);

    private bool TrySelectActionIcon(object sender, out FluentIcon icon)
    {
        FluentIcon? resolvedIcon = GetActionIcon(sender);
        if (resolvedIcon is null)
        {
            icon = null!;
            return false;
        }

        icon = resolvedIcon;
        ViewModel.SelectedIcon = icon;
        return true;
    }

    private FluentIcon? GetActionIcon(object sender)
    {
        if (sender is FrameworkElement { DataContext: FluentIcon dataContextIcon })
        {
            return dataContextIcon;
        }

        if (sender is FrameworkElement { Tag: FluentIcon tagIcon })
        {
            return tagIcon;
        }

        return ViewModel.SelectedIcon;
    }

    private void CollectionIconsListView_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelectedCollectionIcons(CollectionIconsListView.SelectedItems.OfType<FluentIcon>());

    private async void CreateCollection_Click(object sender, RoutedEventArgs e) =>
        await CreateCollectionFromSelectionAsync([]);

    private async void SaveSelectionToNewCollection_Click(object sender, RoutedEventArgs e) =>
        await CreateCollectionFromSelectionAsync(ViewModel.SelectedCollectionIcons);

    private void AddSelectionToCollection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || ViewModel.SelectedCollectionIcons.Count == 0)
        {
            return;
        }

        MenuFlyout flyout = BuildBulkCollectionFlyout(ViewModel.SelectedCollectionIcons);
        flyout.ShowAt(element);
    }

    private async void RemoveSelectionFromCollection_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RemoveSelectedIconsFromCurrentCollectionAsync();

    private void ExportSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || ViewModel.SelectedCollectionIcons.Count == 0)
        {
            return;
        }

        MenuFlyout flyout = BuildExportSelectionFlyout();
        flyout.ShowAt(element);
    }

    private MenuFlyout BuildBulkCollectionFlyout(IReadOnlyList<FluentIcon> icons)
    {
        MenuFlyout flyout = new();

        foreach (IconCollection collection in ViewModel.Collections)
        {
            MenuFlyoutItem item = new()
            {
                Text = collection.Name
            };
            item.Click += async (_, _) => await ViewModel.AddIconsToCollectionAsync(collection.Name, icons);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem newCollection = new()
        {
            Text = "New collection…"
        };
        newCollection.Click += async (_, _) => await CreateCollectionFromSelectionAsync(icons);
        flyout.Items.Add(newCollection);

        return flyout;
    }

    private MenuFlyout BuildExportSelectionFlyout()
    {
        MenuFlyout flyout = new();

        MenuFlyoutItem copyGlyphs = new()
        {
            Text = "Copy glyph codes"
        };
        copyGlyphs.Click += (_, _) => ViewModel.CopySelectedCollectionGlyphs(false);

        MenuFlyoutItem copyGlyphsForXaml = new()
        {
            Text = "Copy glyph codes for XAML"
        };
        copyGlyphsForXaml.Click += (_, _) => ViewModel.CopySelectedCollectionGlyphs(true);

        MenuFlyoutItem copyXaml = new()
        {
            Text = "Copy XAML FontIcons"
        };
        copyXaml.Click += (_, _) => ViewModel.CopySelectedCollectionXaml();

        MenuFlyoutItem exportPng = new()
        {
            Text = "Export PNG files…"
        };
        exportPng.Click += async (_, _) => await ExportSelectedPngsAsync();

        MenuFlyoutItem exportSvg = new()
        {
            Text = "Export SVG files…"
        };
        exportSvg.Click += async (_, _) => await ExportSelectedSvgsAsync();

        flyout.Items.Add(copyGlyphs);
        flyout.Items.Add(copyGlyphsForXaml);
        flyout.Items.Add(copyXaml);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(exportPng);
        flyout.Items.Add(exportSvg);

        return flyout;
    }

    private async Task CreateCollectionFromSelectionAsync(IEnumerable<FluentIcon> icons)
    {
        string? collectionName = await PromptForCollectionNameAsync();
        if (collectionName is null)
        {
            return;
        }

        try
        {
            await ViewModel.CreateCollectionAsync(collectionName, icons);
        }
        catch (ArgumentException ex)
        {
            await ShowMessageDialogAsync("Collection name required", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            await ShowMessageDialogAsync("Unable to create collection", ex.Message);
        }
    }

    private async Task ExportSelectedPngsAsync()
    {
        string? folderPath = await PickFolderPathAsync();
        if (folderPath is null)
        {
            return;
        }

        bool? useBlack = await PromptForPngColorChoiceAsync();
        if (!useBlack.HasValue)
        {
            return;
        }

        await ViewModel.ExportSelectedCollectionPngsAsync(folderPath, useBlack.Value);
    }

    private async Task ExportSelectedSvgsAsync()
    {
        string? folderPath = await PickFolderPathAsync();
        if (folderPath is null)
        {
            return;
        }

        await ViewModel.ExportSelectedCollectionSvgsAsync(folderPath);
    }

    private async Task<bool?> PromptForPngColorChoiceAsync()
    {
        ContentDialog dialog = new()
        {
            Title = "PNG icon color",
            Content = "Choose the icon color (on a transparent background).",
            PrimaryButtonText = "Black",
            SecondaryButtonText = "White",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
        {
            return null;
        }

        return result == ContentDialogResult.Primary;
    }

    private async Task<string?> PromptForCollectionNameAsync()
    {
        TextBox input = new()
        {
            AcceptsReturn = false,
            PlaceholderText = "Collection name"
        };

        ContentDialog dialog = new()
        {
            Title = "New collection",
            Content = input,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return input.Text.Trim();
    }

    private async Task<string?> PickFolderPathAsync()
    {
        FolderPicker picker = new();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, App.WindowHandle);

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task ShowMessageDialogAsync(string title, string message)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private void ExploreInMap_Click(object sender, RoutedEventArgs e)
    {
        FluentIcon? icon = GetActionIcon(sender) ?? ViewModel.SelectedIcon;
        if (icon == null || !ViewModel.LayoutService.IsReady) return;

        ViewModel.SelectedIcon = icon;

        // Find this icon's position in the current layout
        IReadOnlyList<LayoutPosition> positions = ViewModel.LayoutService.Positions;
        int posIdx = -1;
        for (int i = 0; i < positions.Count; i++)
        {
            if (ReferenceEquals(positions[i].Icon, icon)) { posIdx = i; break; }
        }
        if (posIdx < 0) return;

        // Switch to map mode and set pivot
        NavView.SelectedItem = SimilarityMapNavItem;

        SetMapPivot(posIdx);
    }

    // -------------------------------------------------------------------------
    // Map canvas — Draw
    // -------------------------------------------------------------------------

    private void MapCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;
        float W = (float)sender.ActualWidth;
        float H = (float)sender.ActualHeight;
        bool isDark = ActualTheme == ElementTheme.Dark;

        ds.Clear(isDark
            ? Windows.UI.Color.FromArgb(255, 20, 20, 20)
            : Windows.UI.Color.FromArgb(255, 248, 248, 248));

        // Show loading message if vectors aren't ready yet
        if (!ViewModel.LayoutService.IsReady)
        {
            using CanvasTextFormat loadFmt = new()
            {
                FontSize = 16,
                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
                VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center
            };
            ds.DrawText("Loading icons…", W / 2f, H / 2f,
                isDark ? Colors.White : Colors.Black, loadFmt);
            return;
        }

        IReadOnlyList<LayoutPosition> positions = ViewModel.LayoutService.Positions;
        if (positions.Count == 0) return;

        bool hasPivot = _mapPivotIconIdx >= 0 && _mapSimilarities != null;
        float cellPx = MapCellSize * _mapScale;    // full cell size in screen pixels
        float fontSize = cellPx * 0.70f;

        Color baseColor = isDark
            ? Windows.UI.Color.FromArgb(200, 220, 220, 220)
            : Windows.UI.Color.FromArgb(200, 40, 40, 40);
        byte accentR = isDark ? (byte)100 : (byte)0;
        byte accentG = isDark ? (byte)180 : (byte)120;
        byte accentB = isDark ? (byte)255 : (byte)212;

        // Clamp font so icons don't become invisible when zoomed out
        float drawFontSize = Math.Max(fontSize, 4f);

        using CanvasTextFormat tf = new()
        {
            FontFamily = IconMatchingService.FontUri,
            FontSize = drawFontSize,
            HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
            WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap
        };

        using CanvasTextFormat tfPivot = new()
        {
            FontFamily = IconMatchingService.FontUri,
            FontSize = Math.Max(drawFontSize * 1.35f, 6f),
            HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
            WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap
        };

        float halfCell = cellPx * 0.5f;

        for (int i = 0; i < positions.Count; i++)
        {
            LayoutPosition pos = positions[i];
            (float sx, float sy) = MapToScreen(pos.GX, pos.GY, W, H);

            // Cull icons outside visible area
            if (sx < -halfCell * 2 || sx > W + halfCell * 2 ||
                sy < -halfCell * 2 || sy > H + halfCell * 2)
                continue;

            bool isPivot = pos.GX == 0 && pos.GY == 0 && hasPivot;
            float similarity = hasPivot ? _mapSimilarities![pos.Index] : 1f;

            byte alpha = hasPivot
                ? (byte)Math.Clamp((int)(25 + 230 * similarity), 25, 255)
                : (byte)200;

            if (isPivot)
            {
                // Accent halo for pivot
                ds.FillCircle(sx, sy, halfCell * 0.95f,
                    Windows.UI.Color.FromArgb(90, accentR, accentG, accentB));
                ds.DrawCircle(sx, sy, halfCell * 0.95f,
                    Windows.UI.Color.FromArgb(210, accentR, accentG, accentB), 1.5f);
                ds.DrawText(pos.Icon.GlyphString, sx, sy,
                    Windows.UI.Color.FromArgb(235, accentR, accentG, accentB), tfPivot);
            }
            else
            {
                bool isHovered = i == _mapHoveredIndex;
                if (isHovered)
                    ds.FillRoundedRectangle(sx - halfCell * 0.9f, sy - halfCell * 0.9f,
                        halfCell * 1.8f, halfCell * 1.8f, 4, 4,
                        Windows.UI.Color.FromArgb(55, 128, 128, 128));

                // Blend base → accent colour for high-similarity icons
                byte r, g, b;
                if (hasPivot && similarity > 0.25f)
                {
                    float t = Math.Clamp((similarity - 0.25f) / 0.75f, 0f, 1f);
                    r = (byte)(baseColor.R + t * (accentR - baseColor.R));
                    g = (byte)(baseColor.G + t * (accentG - baseColor.G));
                    b = (byte)(baseColor.B + t * (accentB - baseColor.B));
                }
                else
                {
                    r = baseColor.R; g = baseColor.G; b = baseColor.B;
                }

                ds.DrawText(pos.Icon.GlyphString, sx, sy,
                    Windows.UI.Color.FromArgb(alpha, r, g, b), tf);
            }
        }

        // Hover tooltip label
        if (_mapHoveredIndex >= 0 && _mapHoveredIndex < positions.Count)
        {
            LayoutPosition hovPos = positions[_mapHoveredIndex];
            (float hx, float hy) = MapToScreen(hovPos.GX, hovPos.GY, W, H);
            string label = hovPos.Icon.DisplayName;
            if (hasPivot && _mapSimilarities != null)
                label += $"  {_mapSimilarities[hovPos.Index]:P0}";

            using CanvasTextFormat labelFmt = new()
            {
                FontSize = 11,
                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
                VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Top
            };

            float textWidth = label.Length * labelFmt.FontSize * 0.62f;
            float boxWidth = Math.Clamp(textWidth + 16f, 120f, Math.Max(120f, W - 16f));
            float halfBox = boxWidth / 2f;
            float lx = Math.Clamp(hx, halfBox + 4f, W - halfBox - 4f);
            float ly = Math.Min(hy + halfCell + 3f, H - 18);
            ds.FillRoundedRectangle(lx - halfBox, ly - 1, boxWidth, 16, 3, 3,
                Windows.UI.Color.FromArgb(160, 20, 20, 20));
            ds.DrawText(label, lx, ly,
                Windows.UI.Color.FromArgb(230, 240, 240, 240), labelFmt);
        }
    }

    // -------------------------------------------------------------------------
    // Map coordinate helpers (grid ↔ screen)
    // -------------------------------------------------------------------------

    /// <summary>Grid cell (gx, gy) → canvas pixels. Pivot (0,0) = canvas centre + pan.</summary>
    private (float sx, float sy) MapToScreen(int gx, int gy, float W, float H) =>
        (W / 2f + gx * MapCellSize * _mapScale + _mapPanX,
         H / 2f + gy * MapCellSize * _mapScale + _mapPanY);

    /// <summary>Canvas pixels → nearest grid cell (integer coords).</summary>
    private (int gx, int gy) ScreenToCell(float mx, float my, float W, float H) =>
        ((int)Math.Round((mx - W / 2f - _mapPanX) / (MapCellSize * _mapScale)),
         (int)Math.Round((my - H / 2f - _mapPanY) / (MapCellSize * _mapScale)));

    /// <summary>Returns index into Positions[] for the cell under (mx, my), or -1.</summary>
    private int MapHitTest(float mx, float my, float W, float H)
    {
        if (!ViewModel.LayoutService.IsReady) return -1;
        (int gx, int gy) = ScreenToCell(mx, my, W, H);
        return ViewModel.LayoutService.CellIndex.TryGetValue((gx, gy), out int idx) ? idx : -1;
    }

    /// <summary>Scales the view so all icons fit in the current canvas.</summary>
    private void FitGridToCanvas()
    {
        if (!ViewModel.LayoutService.IsReady) return;
        int maxExt = 0;
        foreach (LayoutPosition p in ViewModel.LayoutService.Positions)
        {
            int e = Math.Max(Math.Abs(p.GX), Math.Abs(p.GY));
            if (e > maxExt) maxExt = e;
        }
        float W = (float)MapCanvas.ActualWidth;
        float H = (float)MapCanvas.ActualHeight;
        if (W <= 0 || H <= 0) { _mapScale = 1f; return; }
        float span = (maxExt * 2 + 3) * MapCellSize;
        _mapScale = Math.Min(W / span, H / span);
        _mapPanX = 0f;
        _mapPanY = 0f;
    }

    // -------------------------------------------------------------------------
    // Map canvas — Pointer events
    // -------------------------------------------------------------------------

    private void MapCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(MapCanvas);
        if (point.Properties.IsRightButtonPressed)
        {
            float width = (float)MapCanvas.ActualWidth;
            float height = (float)MapCanvas.ActualHeight;
            if (width > 0 && height > 0)
            {
                int hovered = MapHitTest((float)point.Position.X, (float)point.Position.Y, width, height);
                if (hovered != _mapHoveredIndex)
                {
                    _mapHoveredIndex = hovered;
                    MapCanvas.Invalidate();
                }
            }

            _mapIsDragging = false;
            _mapDragHasMoved = true;
            return;
        }

        MapCanvas.CapturePointer(e.Pointer);
        Point pt = point.Position;
        _mapActivePointers[e.Pointer.PointerId] = pt;

        if (_mapActivePointers.Count >= 2)
        {
            // Second finger down — enter pinch mode, record start state
            Point[] pts = _mapActivePointers.Values.ToArray();
            _mapPinchStartDist = MapPtrDistance(pts[0], pts[1]);
            _mapPinchStartMid = MapPtrMidpoint(pts[0], pts[1]);
            _mapPinchStartScale = _mapScale;
            _mapPinchStartPanX = _mapPanX;
            _mapPinchStartPanY = _mapPanY;
            _mapIsDragging = false;
            _mapDragHasMoved = true; // prevent tap-to-pivot on lift
            _mapPinchOccurred = true; // remember pinch for when last finger lifts
        }
        else
        {
            _mapIsDragging = true;
            _mapDragHasMoved = false;
            _mapDragStartPointer = pt;
            _mapPanXAtDragStart = _mapPanX;
            _mapPanYAtDragStart = _mapPanY;
        }
        e.Handled = true;
    }

    private void MapCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.LayoutService.IsReady) return;
        float W = (float)MapCanvas.ActualWidth;
        float H = (float)MapCanvas.ActualHeight;
        Point pt = e.GetCurrentPoint(MapCanvas).Position;

        if (_mapActivePointers.ContainsKey(e.Pointer.PointerId))
            _mapActivePointers[e.Pointer.PointerId] = pt;

        if (_mapActivePointers.Count >= 2)
        {
            // Two-finger pinch: zoom around midpoint + allow midpoint translation
            if (_mapPinchStartDist > 0)
            {
                Point[] pts = _mapActivePointers.Values.ToArray();
                double dist = MapPtrDistance(pts[0], pts[1]);
                Point mid = MapPtrMidpoint(pts[0], pts[1]);

                float factor = (float)(dist / _mapPinchStartDist);
                float newScale = Math.Clamp(_mapPinchStartScale * factor, 0.08f, 20f);
                float ratio = newScale / _mapPinchStartScale;

                float curMx = (float)mid.X, curMy = (float)mid.Y;
                float startMx = (float)_mapPinchStartMid.X, startMy = (float)_mapPinchStartMid.Y;

                // Fix the world point under the start midpoint, then shift by midpoint translation
                _mapPanX = curMx - W / 2f - (startMx - W / 2f - _mapPinchStartPanX) * ratio;
                _mapPanY = curMy - H / 2f - (startMy - H / 2f - _mapPinchStartPanY) * ratio;
                _mapScale = newScale;
                MapCanvas.Invalidate();
            }
            _mapDragHasMoved = true;
        }
        else
        {
            float mx = (float)pt.X, my = (float)pt.Y;
            int hovered = MapHitTest(mx, my, W, H);
            if (hovered != _mapHoveredIndex)
            {
                _mapHoveredIndex = hovered;
                MapCanvas.Invalidate();
            }

            if (_mapIsDragging)
            {
                float dx = (float)(pt.X - _mapDragStartPointer.X);
                float dy = (float)(pt.Y - _mapDragStartPointer.Y);
                if (MathF.Abs(dx) + MathF.Abs(dy) > 3) _mapDragHasMoved = true;
                _mapPanX = _mapPanXAtDragStart + dx;
                _mapPanY = _mapPanYAtDragStart + dy;
                MapCanvas.Invalidate();
            }
        }

        e.Handled = true;
    }

    private void MapCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Read wasDrag before ReleasePointerCapture, which fires PointerCaptureLost
        // synchronously and would reset _mapDragHasMoved before we can check it.
        bool wasDrag = _mapDragHasMoved;
        _mapActivePointers.Remove(e.Pointer.PointerId);
        MapCanvas.ReleasePointerCapture(e.Pointer);

        if (_mapActivePointers.Count == 1)
        {
            // One finger remains after pinch — re-arm single-finger drag from its current position
            Point remainingPt = _mapActivePointers.Values.First();
            _mapIsDragging = true;
            _mapDragHasMoved = false;
            _mapDragStartPointer = remainingPt;
            _mapPanXAtDragStart = _mapPanX;
            _mapPanYAtDragStart = _mapPanY;
            wasDrag = true; // don't trigger tap-to-pivot
        }
        else if (_mapActivePointers.Count == 0)
        {
            _mapIsDragging = false;
            _mapDragHasMoved = false;
            // Suppress tap-to-pivot for the last finger lifted after a pinch
            if (_mapPinchOccurred)
            {
                wasDrag = true;
                _mapPinchOccurred = false;
            }
        }

        if (!wasDrag && _mapHoveredIndex >= 0)
            SetMapPivot(_mapHoveredIndex);

        e.Handled = true;
    }

    private void MapCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _mapActivePointers.Remove(e.Pointer.PointerId);
        if (_mapActivePointers.Count == 0)
        {
            _mapIsDragging = false;
            _mapDragHasMoved = false;
            _mapPinchOccurred = false;
        }
    }

    private static double MapPtrDistance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Point MapPtrMidpoint(Point a, Point b) =>
        new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private void MapCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint pt = e.GetCurrentPoint(MapCanvas);
        float mx = (float)pt.Position.X;
        float my = (float)pt.Position.Y;
        float factor = pt.Properties.MouseWheelDelta > 0 ? 1.12f : 1f / 1.12f;
        float newScale = Math.Clamp(_mapScale * factor, 0.08f, 20f);
        float ratio = newScale / _mapScale;

        float W = (float)MapCanvas.ActualWidth;
        float H = (float)MapCanvas.ActualHeight;

        // Keep the world point under the cursor fixed.
        // MapToScreen: sx = W/2 + gx*cell*scale + panX  →  newPanX = (mx - W/2)*(1 - ratio) + panX*ratio
        _mapPanX = (mx - W / 2f) * (1f - ratio) + _mapPanX * ratio;
        _mapPanY = (my - H / 2f) * (1f - ratio) + _mapPanY * ratio;
        _mapScale = newScale;

        MapCanvas.Invalidate();
        e.Handled = true;
    }

    // -------------------------------------------------------------------------
    // Map pivot selection
    // -------------------------------------------------------------------------

    private void SetMapPivot(int positionIndex)
    {
        if (!ViewModel.LayoutService.IsReady) return;
        IReadOnlyList<LayoutPosition> positions = ViewModel.LayoutService.Positions;
        if (positionIndex < 0 || positionIndex >= positions.Count) return;

        LayoutPosition pos = positions[positionIndex];
        int iconIdx = pos.Index;

        // Compute cosine similarities for this pivot
        _mapSimilarities = ViewModel.LayoutService.ComputeSimilarities(
            ViewModel.GlyphVectors, iconIdx);

        // Re-sort grid: pivot → (0,0), rest sorted by descending similarity
        ViewModel.LayoutService.SetPivotLayout(iconIdx, _mapSimilarities);

        _mapPivotIconIdx = iconIdx;
        _mapHoveredIndex = -1;
        // Keep zoom, reset pan so pivot is centred
        _mapPanX = 0f;
        _mapPanY = 0f;

        ViewModel.SetMapPivotCommand.Execute(pos.Icon);
        MapHintText.Visibility = Visibility.Collapsed;
        MapCanvas.Invalidate();
    }

    // -------------------------------------------------------------------------
    // Static helper functions for x:Bind
    // -------------------------------------------------------------------------

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility HasSelectedIcon(FluentIcon? icon) =>
        icon != null ? Visibility.Visible : Visibility.Collapsed;

    public static double MatchOpacity(bool isMatch) => isMatch ? 0.18 : 0.0;

    public static string FormatScore(double score) =>
        score > 0.001 ? $"{score:P0}" : "";

    public static string FormatCount(int count) =>
        count == 1 ? "1 icon" : $"{count:N0} icons";

    public static string FormatCollectionCount(int count) =>
        count == 1 ? "1 collection" : $"{count:N0} collections";

    public static bool HasSelection(int count) => count > 0;

    public static Visibility ZeroToVisibility(int count) =>
        count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatLoading(string phase, int progress) =>
        progress < 100 ? $"{phase} {progress}%" : "Almost done…";
}
