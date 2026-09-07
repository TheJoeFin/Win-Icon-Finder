using Microsoft.Graphics.Canvas;
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
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using WinIconFinder.Controls;
using WinIconFinder.Models;
using WinIconFinder.Services;
using WinIconFinder.ViewModels;
using WinRT.Interop;

namespace WinIconFinder;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; } = new();

    // One-time tips (e.g. "ship the font with your app" after the first FontIcon copy)
    private readonly UserTipsService _tips = new();

    // Debounce timer: fires 500 ms after the most recently collected ink stroke.
    private readonly DispatcherTimer _debounceTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // Pen width in canvas-logical pixels.
    private float _penWidth = 36f;

    // Tracks the current square canvas side length for stroke rescaling on resize.
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
    private bool _isInitialMapScaleApplied;
    private const float MapCellSize = 26f; // logical pixels per grid cell at scale=1
    private const float MapGlyphVerticalOffsetRatio = -0.05f;
    private const float InitialMapZoomFactor = 3f;

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
        ApplyTheme(AppSettingsService.ToElementTheme(ViewModel.ThemePreference));
        NavView.SelectedItem = SearchModeNavItem;

        ConfigureInkCanvas();
        ActualThemeChanged += (_, _) =>
        {
            UpdateInkDrawingAttributes();
            MapCanvas.Invalidate();
        };

        ViewModel.RequestThemeChange += ApplyTheme;

        _debounceTimer.Tick += async (_, _) =>
        {
            _debounceTimer.Stop();
            await SearchCollectedInkAsync();
        };

        ViewModel.RequestClearCanvas += () =>
        {
            DrawingCanvas.InkPresenter.StrokeContainer.Clear();
            EmptyStateText.Visibility = Visibility.Visible;
        };

        ViewModel.RequestResearch += async () => await SearchCollectedInkAsync();

        ViewModel.TopMatchesFound += matches =>
        {
            if (matches.Count > 0)
                IconListView.ScrollIntoView(matches[0]);
        };

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedIcon))
            {
                MatchingIconOverlay.Text = ViewModel.SelectedIcon?.GlyphString ?? string.Empty;
                MatchingIconOverlay.Visibility = ViewModel.SelectedIcon is null
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            // Refresh map as soon as loading finishes (avoids "not ready" guard hit)
            if (e.PropertyName == nameof(ViewModel.IsBusy) && !ViewModel.IsBusy)
            {
                ApplyInitialMapScale();
                MapCanvas.Invalidate();
            }
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
    // InkCanvas input and layout
    // -------------------------------------------------------------------------

    private void ConfigureInkCanvas()
    {
        DrawingCanvas.InkPresenter.InputDeviceTypes =
            CoreInputDeviceTypes.Mouse | CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Touch;
        DrawingCanvas.InkPresenter.StrokesCollected += DrawingCanvas_StrokesCollected;
        UpdateInkDrawingAttributes();
    }

    private void CanvasHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double margin = 48.0;
        double side = Math.Max(Math.Min(e.NewSize.Width, e.NewSize.Height) - margin, 120);

        if (_canvasSize > 0 && side != _canvasSize)
        {
            System.Numerics.Matrix3x2 scale = System.Numerics.Matrix3x2.CreateScale((float)(side / _canvasSize));
            foreach (Windows.UI.Input.Inking.InkStroke stroke in DrawingCanvas.InkPresenter.StrokeContainer.GetStrokes())
                stroke.PointTransform = stroke.PointTransform * scale;
        }

        _canvasSize = side;
        CanvasGuide.Width = side;
        CanvasGuide.Height = side;
        DrawingCanvas.Width = side;
        DrawingCanvas.Height = side;
        MatchingIconOverlay.FontSize = side * (IconMatchingService.BaseFontSize / IconMatchingService.GlyphSize);
    }

    private void DrawingCanvas_StrokesCollected(
        Microsoft.UI.Xaml.Controls.InkPresenter sender,
        Microsoft.UI.Xaml.Controls.InkStrokesCollectedEventArgs args)
    {
        EmptyStateText.Visibility = Visibility.Collapsed;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private async Task SearchCollectedInkAsync()
    {
        List<InkStrokeData> strokes = GetInkStrokeData();
        if (strokes.Count == 0)
        {
            return;
        }

        await ViewModel.SearchByInkAsync(strokes, DrawingCanvas.RenderSize);
    }

    private List<InkStrokeData> GetInkStrokeData() =>
        [.. DrawingCanvas.InkPresenter.StrokeContainer.GetStrokes().Select(GetInkStrokeData)];

    private static InkStrokeData GetInkStrokeData(Windows.UI.Input.Inking.InkStroke stroke)
    {
        System.Numerics.Matrix3x2 transform = stroke.PointTransform;
        IReadOnlyList<Point> points = [.. stroke.GetInkPoints().Select(point =>
        {
            System.Numerics.Vector2 transformed = System.Numerics.Vector2.Transform(
                new System.Numerics.Vector2((float)point.Position.X, (float)point.Position.Y),
                transform);
            return new Point(transformed.X, transformed.Y);
        })];
        return new InkStrokeData(points, (float)stroke.DrawingAttributes.Size.Width);
    }

    private async void ExportInk_Click(object sender, RoutedEventArgs e)
    {
        if (DrawingCanvas.InkPresenter.StrokeContainer.GetStrokes().Count == 0)
        {
            return;
        }

        FileSavePicker picker = new();
        picker.FileTypeChoices.Add("Ink stroke fixture", [".isf"]);
        picker.SuggestedFileName = "icon-sketch";
        InitializeWithWindow.Initialize(picker, App.WindowHandle);

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        await DrawingCanvas.InkPresenter.StrokeContainer.SaveAsync(stream);
        ViewModel.StatusText = $"Saved sketch fixture: {file.Name}";
    }

    // -------------------------------------------------------------------------
    // Pen-width buttons
    // -------------------------------------------------------------------------

    private void UndoStroke_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<Windows.UI.Input.Inking.InkStroke> strokes = DrawingCanvas.InkPresenter.StrokeContainer.GetStrokes();
        if (strokes.Count == 0)
        {
            return;
        }

        foreach (Windows.UI.Input.Inking.InkStroke stroke in strokes)
            stroke.Selected = false;
        strokes[^1].Selected = true;
        DrawingCanvas.InkPresenter.StrokeContainer.DeleteSelected();

        if (strokes.Count == 1)
            EmptyStateText.Visibility = Visibility.Visible;

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void RotateInk_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<Windows.UI.Input.Inking.InkStroke> strokes = DrawingCanvas.InkPresenter.StrokeContainer.GetStrokes();
        if (strokes.Count == 0)
        {
            return;
        }

        System.Numerics.Matrix3x2 rotation = System.Numerics.Matrix3x2.CreateRotation(
            MathF.PI / 2,
            new System.Numerics.Vector2((float)_canvasSize / 2, (float)_canvasSize / 2));
        foreach (Windows.UI.Input.Inking.InkStroke stroke in strokes)
            stroke.PointTransform = stroke.PointTransform * rotation;

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
        UpdateInkDrawingAttributes();
    }

    private void UpdateInkDrawingAttributes()
    {
        Windows.UI.Input.Inking.InkDrawingAttributes attributes = new()
        {
            Color = ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black,
            FitToCurve = true,
            IgnorePressure = true,
            Size = new Size(_penWidth, _penWidth)
        };
        DrawingCanvas.InkPresenter.UpdateDefaultDrawingAttributes(attributes);
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

        MenuFlyoutSubItem copyGlyph = BuildGlyphCopySubMenu();
        copyGlyph.Icon = new FontIcon { Glyph = "\uE8C8" };
        AutomationProperties.SetAutomationId(copyGlyph, "CtxCopyGlyph");

        MenuFlyoutItem copyXaml = new()
        {
            Text = "Copy as XAML FontIcon",
            Icon = new FontIcon { Glyph = "\uE943" }
        };
        AutomationProperties.SetAutomationId(copyXaml, "CtxCopyXaml");
        copyXaml.Click += CopyXaml_Click;

        MenuFlyoutItem copyPathIcon = new()
        {
            Text = "Copy as XAML PathIcon",
            Icon = new VectorPathIcon()
        };
        AutomationProperties.SetAutomationId(copyPathIcon, "CtxCopyPathIcon");
        copyPathIcon.Click += CopyPathIcon_Click;

        MenuFlyoutSubItem copyPng = BuildPngCopySubMenu();
        copyPng.Icon = new FontIcon { Glyph = "\uEB9F" };
        AutomationProperties.SetAutomationId(copyPng, "CtxCopyPng");

        MenuFlyoutSubItem copySvg = BuildSvgCopySubMenu();
        copySvg.Icon = new FontIcon { Glyph = "\uE71B" };
        AutomationProperties.SetAutomationId(copySvg, "CtxCopySvg");

        flyout.Items.Add(favorite);
        flyout.Items.Add(collections);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(copyGlyph);
        flyout.Items.Add(copyXaml);
        flyout.Items.Add(copyPathIcon);
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

    private void CopyGlyph_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && TrySelectActionIcon(sender))
        {
            BuildGlyphCopyFlyout().ShowAt(element);
        }
    }

    private MenuFlyout BuildGlyphCopyFlyout()
    {
        MenuFlyout flyout = new();
        flyout.Items.Add(CreateGlyphCopyItem("C# (\\uXXXX)", useXaml: false));
        flyout.Items.Add(CreateGlyphCopyItem("XAML (&#xXXXX;)", useXaml: true));
        return flyout;
    }

    private MenuFlyoutSubItem BuildGlyphCopySubMenu()
    {
        MenuFlyoutSubItem subMenu = new() { Text = "Copy glyph code" };
        subMenu.Items.Add(CreateGlyphCopyItem("C# (\\uXXXX)", useXaml: false));
        subMenu.Items.Add(CreateGlyphCopyItem("XAML (&#xXXXX;)", useXaml: true));
        return subMenu;
    }

    private MenuFlyoutItem CreateGlyphCopyItem(string text, bool useXaml)
    {
        MenuFlyoutItem item = new() { Text = text };
        item.Click += (_, _) => ViewModel.CopyAsGlyphCommand.Execute(useXaml);
        return item;
    }

    private async void CopyXaml_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySelectActionIcon(sender))
        {
            return;
        }

        ViewModel.CopyAsXamlCommand.Execute(null);
        await ShowFontIconTipIfFirstTimeAsync();
    }

    private async void CopyPathIcon_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySelectActionIcon(sender))
        {
            return;
        }

        ViewModel.CopyAsPathIconCommand.Execute(null);
        await ShowPathIconTipIfFirstTimeAsync();
    }

    private void CopyPng_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && TrySelectActionIcon(sender))
        {
            BuildPngCopyFlyout().ShowAt(element);
        }
    }

    private MenuFlyout BuildPngCopyFlyout()
    {
        MenuFlyout flyout = new();
        flyout.Items.Add(CreatePngCopyItem("Black", useBlack: true));
        flyout.Items.Add(CreatePngCopyItem("White", useBlack: false));
        return flyout;
    }

    private MenuFlyoutSubItem BuildPngCopySubMenu()
    {
        MenuFlyoutSubItem subMenu = new() { Text = "Copy as PNG" };
        subMenu.Items.Add(CreatePngCopyItem("Black", useBlack: true));
        subMenu.Items.Add(CreatePngCopyItem("White", useBlack: false));
        return subMenu;
    }

    private MenuFlyoutItem CreatePngCopyItem(string text, bool useBlack)
    {
        MenuFlyoutItem item = new() { Text = text };
        item.Click += async (_, _) => await ViewModel.CopyAsPngCommand.ExecuteAsync(useBlack);
        return item;
    }

    private void CopySvg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && TrySelectActionIcon(sender))
        {
            BuildSvgCopyFlyout().ShowAt(element);
        }
    }

    private MenuFlyout BuildSvgCopyFlyout()
    {
        MenuFlyout flyout = new();
        flyout.Items.Add(CreateSvgCopyItem("Black", useBlack: true));
        flyout.Items.Add(CreateSvgCopyItem("White", useBlack: false));
        return flyout;
    }

    private MenuFlyoutSubItem BuildSvgCopySubMenu()
    {
        MenuFlyoutSubItem subMenu = new() { Text = "Copy as SVG" };
        subMenu.Items.Add(CreateSvgCopyItem("Black", useBlack: true));
        subMenu.Items.Add(CreateSvgCopyItem("White", useBlack: false));
        return subMenu;
    }

    private MenuFlyoutItem CreateSvgCopyItem(string text, bool useBlack)
    {
        MenuFlyoutItem item = new() { Text = text };
        item.Click += (_, _) => ViewModel.CopyAsSvgCommand.Execute(useBlack);
        return item;
    }

    // -------------------------------------------------------------------------
    // Mode switch
    // -------------------------------------------------------------------------

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.SelectedItem, SettingsNavItem))
        {
            SearchModePanel.Visibility = Visibility.Collapsed;
            SimilarityMapPanel.Visibility = Visibility.Collapsed;
            CollectionsPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
            ViewModel.IsMapMode = false;
        }
        else if (ReferenceEquals(e.SelectedItem, SimilarityMapNavItem))
        {
            SearchModePanel.Visibility = Visibility.Collapsed;
            SimilarityMapPanel.Visibility = Visibility.Visible;
            CollectionsPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            ViewModel.IsMapMode = true;

            ApplyInitialMapScale();

            if (ViewModel.SelectedIcon is FluentIcon selectedIcon &&
                TryGetLayoutPositionIndex(selectedIcon, out int positionIndex))
            {
                SetMapPivot(positionIndex);
            }
            else
            {
                MapHintText.Visibility = _mapPivotIconIdx >= 0 ? Visibility.Collapsed : Visibility.Visible;
                MapCanvas.Invalidate();
            }
        }
        else if (ReferenceEquals(e.SelectedItem, CollectionsNavItem))
        {
            SearchModePanel.Visibility = Visibility.Collapsed;
            SimilarityMapPanel.Visibility = Visibility.Collapsed;
            CollectionsPanel.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
            ViewModel.IsMapMode = false;
        }
        else
        {
            SearchModePanel.Visibility = Visibility.Visible;
            SimilarityMapPanel.Visibility = Visibility.Collapsed;
            CollectionsPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            ViewModel.IsMapMode = false;
        }
    }

    private void ApplyTheme(ElementTheme theme)
    {
        RequestedTheme = theme;

        if (App.Window is MainWindow mainWindow)
        {
            mainWindow.ApplyTheme(theme);
        }

        UpdateInkDrawingAttributes();
        MapCanvas.Invalidate();
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
        copyXaml.Click += async (_, _) =>
        {
            if (ViewModel.SelectedCollectionIconCount == 0)
            {
                return;
            }

            ViewModel.CopySelectedCollectionXaml();
            await ShowFontIconTipIfFirstTimeAsync();
        };

        MenuFlyoutItem copyPathIcons = new()
        {
            Text = "Copy XAML PathIcons"
        };
        copyPathIcons.Click += async (_, _) =>
        {
            if (ViewModel.SelectedCollectionIconCount == 0)
            {
                return;
            }

            ViewModel.CopySelectedCollectionPathIcons();
            await ShowPathIconTipIfFirstTimeAsync();
        };

        MenuFlyoutSubItem exportPng = new()
        {
            Text = "Export PNG files…"
        };
        exportPng.Items.Add(CreatePngExportItem("Black", useBlack: true));
        exportPng.Items.Add(CreatePngExportItem("White", useBlack: false));

        MenuFlyoutSubItem exportSvg = new()
        {
            Text = "Export SVG files…"
        };
        exportSvg.Items.Add(CreateSvgExportItem("Black", useBlack: true));
        exportSvg.Items.Add(CreateSvgExportItem("White", useBlack: false));

        flyout.Items.Add(copyGlyphs);
        flyout.Items.Add(copyGlyphsForXaml);
        flyout.Items.Add(copyXaml);
        flyout.Items.Add(copyPathIcons);
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

    private MenuFlyoutItem CreatePngExportItem(string text, bool useBlack)
    {
        MenuFlyoutItem item = new() { Text = text };
        item.Click += async (_, _) => await ExportSelectedPngsAsync(useBlack);
        return item;
    }

    private async Task ExportSelectedPngsAsync(bool useBlack)
    {
        string? folderPath = await PickFolderPathAsync();
        if (folderPath is null)
        {
            return;
        }

        await ViewModel.ExportSelectedCollectionPngsAsync(folderPath, useBlack);
    }

    private MenuFlyoutItem CreateSvgExportItem(string text, bool useBlack)
    {
        MenuFlyoutItem item = new() { Text = text };
        item.Click += async (_, _) => await ExportSelectedSvgsAsync(useBlack);
        return item;
    }

    private async Task ExportSelectedSvgsAsync(bool useBlack)
    {
        string? folderPath = await PickFolderPathAsync();
        if (folderPath is null)
        {
            return;
        }

        await ViewModel.ExportSelectedCollectionSvgsAsync(folderPath, useBlack);
    }

    /// <summary>
    /// The copied FontIcon snippet points at an ms-appx font URI, which only
    /// resolves if the consuming app ships the TTF itself. Explain that once,
    /// the first time the user copies such a snippet.
    /// </summary>
    private async Task ShowFontIconTipIfFirstTimeAsync()
    {
        if (!_tips.ShouldShowFontIconTip())
        {
            return;
        }

        await ShowFontIconShippingTipAsync();
    }

    private async Task ShowFontIconShippingTipAsync()
    {
        StackPanel panel = new() { Spacing = 12, MaxWidth = 460 };

        panel.Children.Add(new TextBlock
        {
            Text = "The snippet references the Fluent System Icons font by package URI:",
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = IconMatchingService.FontUri,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "This font is not installed with Windows, so you have to ship it with your "
                 + "app — otherwise the glyph renders as an empty box on other machines.",
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Where to put it",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Copy FluentSystemIcons-Regular.ttf into your project's Assets folder and "
                 + "include it in your .csproj so it lands in the package:",
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = """
                   <Content Include="Assets\FluentSystemIcons-Regular.ttf">
                     <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                   </Content>
                   """,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "If you put the font somewhere else, adjust the path in the FontFamily URI to match.",
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Where to get it",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Download it from Microsoft's Fluent System Icons repo (MIT licensed), "
                 + "under fonts/FluentSystemIcons-Regular.ttf:",
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new HyperlinkButton
        {
            Content = "github.com/microsoft/fluentui-system-icons",
            NavigateUri = new Uri("https://github.com/microsoft/fluentui-system-icons/tree/main/fonts"),
            Padding = new Thickness(0)
        });

        ContentDialog dialog = new()
        {
            Title = "Copied — remember to ship the font",
            Content = new ScrollViewer
            {
                Content = panel,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 460
            },
            CloseButtonText = "Got it",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// A PathIcon carries its own outline, so unlike the FontIcon snippet it needs
    /// no font shipped alongside it — but the geometry does not scale with the
    /// control. Explain both points once, the first time the user copies one.
    /// </summary>
    private async Task ShowPathIconTipIfFirstTimeAsync()
    {
        if (!_tips.ShouldShowPathIconTip())
        {
            return;
        }

        await ShowPathIconSizingTipAsync();
    }

    private async Task ShowPathIconSizingTipAsync()
    {
        StackPanel panel = new() { Spacing = 12, MaxWidth = 460 };

        panel.Children.Add(new TextBlock
        {
            Text = "The snippet holds the glyph outline as vector path data, so it renders "
                 + "anywhere XAML does — no icon font to ship, no missing-glyph boxes.",
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Where it goes",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Paste it into any IconElement slot — AppBarButton.Icon, "
                 + "NavigationViewItem.Icon, MenuFlyoutItem.Icon, and so on:",
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = """
                   <AppBarButton Label="Save">
                     <AppBarButton.Icon>
                       <PathIcon Data="F1 M…" />
                     </AppBarButton.Icon>
                   </AppBarButton>
                   """,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Sizing",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "PathIcon draws its geometry at the coordinates you give it, so the data is "
                 + "scaled to a 16×16 box — the standard icon size. To render it larger, wrap "
                 + "it in a Viewbox rather than editing the numbers:",
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = """
                   <Viewbox Width="32" Height="32">
                     <PathIcon Data="F1 M…" />
                   </Viewbox>
                   """,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "The icon picks up the surrounding Foreground, so it follows your theme "
                 + "the same way a FontIcon does.",
            TextWrapping = TextWrapping.Wrap
        });

        ContentDialog dialog = new()
        {
            Title = "Copied — self-contained vector icon",
            Content = new ScrollViewer
            {
                Content = panel,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 460
            },
            CloseButtonText = "Got it",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
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
        if (icon == null || !TryGetLayoutPositionIndex(icon, out int positionIndex)) return;

        ViewModel.SelectedIcon = icon;

        // Switch to map mode and set pivot.
        NavView.SelectedItem = SimilarityMapNavItem;
        SetMapPivot(positionIndex);
    }

    private bool TryGetLayoutPositionIndex(FluentIcon icon, out int positionIndex)
    {
        positionIndex = -1;
        if (!ViewModel.LayoutService.IsReady) return false;

        IReadOnlyList<LayoutPosition> positions = ViewModel.LayoutService.Positions;
        for (int i = 0; i < positions.Count; i++)
        {
            if (ReferenceEquals(positions[i].Icon, icon))
            {
                positionIndex = i;
                return true;
            }
        }

        return false;
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
            ? Color.FromArgb(255, 20, 20, 20)
            : Color.FromArgb(255, 248, 248, 248));

        // Show loading message if vectors aren't ready yet
        if (!ViewModel.LayoutService.IsReady)
        {
            using CanvasTextFormat loadFmt = new()
            {
                FontSize = 16,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
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
            ? Color.FromArgb(200, 220, 220, 220)
            : Color.FromArgb(200, 40, 40, 40);
        byte accentR = isDark ? (byte)100 : (byte)0;
        byte accentG = isDark ? (byte)180 : (byte)120;
        byte accentB = isDark ? (byte)255 : (byte)212;

        // Clamp font so icons don't become invisible when zoomed out
        float drawFontSize = Math.Max(fontSize, 4f);
        float pivotFontSize = Math.Max(drawFontSize * 1.35f, 6f);
        float glyphVerticalOffset = drawFontSize * MapGlyphVerticalOffsetRatio;
        float pivotGlyphVerticalOffset = pivotFontSize * MapGlyphVerticalOffsetRatio;

        using CanvasTextFormat tf = new()
        {
            FontFamily = IconMatchingService.FontUri,
            FontSize = drawFontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap
        };

        using CanvasTextFormat tfPivot = new()
        {
            FontFamily = IconMatchingService.FontUri,
            FontSize = pivotFontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap
        };

        float halfCell = cellPx * 0.5f;

        for (int i = 0; i < positions.Count; i++)
        {
            LayoutPosition pos = positions[i];
            (float sx, float sy) = MapToScreen(pos.GX, pos.GY, W, H);

            // Cull icons outside visible area
            if (sx < -halfCell * 2 || sx > W + (halfCell * 2) ||
                sy < -halfCell * 2 || sy > H + (halfCell * 2))
                continue;

            bool isPivot = pos.GX == 0 && pos.GY == 0 && hasPivot;
            float similarity = hasPivot ? _mapSimilarities![pos.Index] : 1f;

            byte alpha = hasPivot
                ? (byte)Math.Clamp((int)(25 + (230 * similarity)), 25, 255)
                : (byte)200;

            if (isPivot)
            {
                // Accent halo for pivot
                ds.FillCircle(sx, sy, halfCell * 0.95f,
                    Color.FromArgb(90, accentR, accentG, accentB));
                ds.DrawCircle(sx, sy, halfCell * 0.95f,
                    Color.FromArgb(210, accentR, accentG, accentB), 1.5f);
                ds.DrawText(pos.Icon.GlyphString, sx, sy + pivotGlyphVerticalOffset,
                    Color.FromArgb(235, accentR, accentG, accentB), tfPivot);
            }
            else
            {
                bool isHovered = i == _mapHoveredIndex;
                if (isHovered)
                    ds.FillRoundedRectangle(sx - (halfCell * 0.9f), sy - (halfCell * 0.9f),
                        halfCell * 1.8f, halfCell * 1.8f, 4, 4,
                        Color.FromArgb(55, 128, 128, 128));

                // Blend base → accent colour for high-similarity icons
                byte r, g, b;
                if (hasPivot && similarity > 0.25f)
                {
                    float t = Math.Clamp((similarity - 0.25f) / 0.75f, 0f, 1f);
                    r = (byte)(baseColor.R + (t * (accentR - baseColor.R)));
                    g = (byte)(baseColor.G + (t * (accentG - baseColor.G)));
                    b = (byte)(baseColor.B + (t * (accentB - baseColor.B)));
                }
                else
                {
                    r = baseColor.R; g = baseColor.G; b = baseColor.B;
                }

                ds.DrawText(pos.Icon.GlyphString, sx, sy + glyphVerticalOffset,
                    Color.FromArgb(alpha, r, g, b), tf);
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
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Top
            };

            float lx = Math.Clamp(hx, 60, W - 60);
            float ly = Math.Min(hy + halfCell + 3f, H - 18);
            ds.FillRoundedRectangle(lx - 60, ly - 1, 120, 16, 3, 3,
                Color.FromArgb(160, 20, 20, 20));
            ds.DrawText(label, lx, ly,
                Color.FromArgb(230, 240, 240, 240), labelFmt);
        }
    }

    // -------------------------------------------------------------------------
    // Map coordinate helpers (grid ↔ screen)
    // -------------------------------------------------------------------------

    /// <summary>Grid cell (gx, gy) → canvas pixels. Pivot (0,0) = canvas centre + pan.</summary>
    private (float sx, float sy) MapToScreen(int gx, int gy, float W, float H) =>
        ((W / 2f) + (gx * MapCellSize * _mapScale) + _mapPanX,
         (H / 2f) + (gy * MapCellSize * _mapScale) + _mapPanY);

    /// <summary>Canvas pixels → nearest grid cell (integer coords).</summary>
    private (int gx, int gy) ScreenToCell(float mx, float my, float W, float H) =>
        ((int)Math.Round((mx - (W / 2f) - _mapPanX) / (MapCellSize * _mapScale)),
         (int)Math.Round((my - (H / 2f) - _mapPanY) / (MapCellSize * _mapScale)));

    /// <summary>Returns index into Positions[] for the cell under (mx, my), or -1.</summary>
    private int MapHitTest(float mx, float my, float W, float H)
    {
        if (!ViewModel.LayoutService.IsReady) return -1;
        (int gx, int gy) = ScreenToCell(mx, my, W, H);
        return ViewModel.LayoutService.CellIndex.TryGetValue((gx, gy), out int idx) ? idx : -1;
    }

    private void MapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyInitialMapScale();
    }

    private void ApplyInitialMapScale()
    {
        if (_isInitialMapScaleApplied || !ViewModel.LayoutService.IsReady ||
            MapCanvas.ActualWidth <= 0 || MapCanvas.ActualHeight <= 0)
        {
            return;
        }

        FitGridToCanvas();
        _isInitialMapScaleApplied = true;
        MapCanvas.Invalidate();
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
        float span = ((maxExt * 2) + 3) * MapCellSize;
        _mapScale = Math.Clamp(Math.Min(W / span, H / span) * InitialMapZoomFactor, 0.08f, 20f);
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
                _mapPanX = curMx - (W / 2f) - ((startMx - (W / 2f) - _mapPinchStartPanX) * ratio);
                _mapPanY = curMy - (H / 2f) - ((startMy - (H / 2f) - _mapPinchStartPanY) * ratio);
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
        return Math.Sqrt((dx * dx) + (dy * dy));
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
        _mapPanX = ((mx - (W / 2f)) * (1f - ratio)) + (_mapPanX * ratio);
        _mapPanY = ((my - (H / 2f)) * (1f - ratio)) + (_mapPanY * ratio);
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
