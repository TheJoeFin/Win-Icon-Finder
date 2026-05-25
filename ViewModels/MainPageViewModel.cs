using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using WinIconFinder.Models;
using WinIconFinder.Services;

namespace WinIconFinder.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly FluentIconsService _iconsService = new();
    private readonly IconMatchingService _matchingService = new();
    private readonly ClipboardExportService _clipboardService = new();
    private readonly SimilarityLayoutService _layoutService = new();

    [ObservableProperty]
    public partial IReadOnlyList<FluentIcon> AllIcons { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<FluentIcon> FilteredIcons { get; set; } = [];

    [ObservableProperty]
    public partial FluentIcon? SelectedIcon { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int InitProgress { get; set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Draw something to search for an icon";

    [ObservableProperty]
    public partial bool TryMatchMirrors { get; set; } = false;

    [ObservableProperty]
    public partial string LoadingPhase { get; set; } = "Pre-rendering icons…";

    [ObservableProperty]
    public partial bool IsMapMode { get; set; }

    [ObservableProperty]
    public partial FluentIcon? MapPivotIcon { get; set; }

    [ObservableProperty]
    public partial string MapStatusText { get; set; } = "Click any icon to explore its visual neighborhood";

    partial void OnTryMatchMirrorsChanged(bool value) => RequestResearch?.Invoke();
    partial void OnIsMapModeChanged(bool value) => MapModeChanged?.Invoke(value);

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>Raised by ClearCanvasCommand; code-behind clears the InkCanvas.</summary>
    public event Action? RequestClearCanvas;

    /// <summary>Raised when search options change; code-behind re-runs the last search.</summary>
    public event Action? RequestResearch;

    /// <summary>Raised after a search completes; code-behind scrolls to first match.</summary>
    public event Action<IList<FluentIcon>>? TopMatchesFound;

    /// <summary>Raised when the mode switches; bool = true means map mode.</summary>
    public event Action<bool>? MapModeChanged;

    // -------------------------------------------------------------------------
    // Internal accessors (used by MainPage code-behind)
    // -------------------------------------------------------------------------

    internal SimilarityLayoutService LayoutService => _layoutService;
    internal float[][] GlyphVectors => _matchingService.GlyphVectors;

    // -------------------------------------------------------------------------
    // Initialisation
    // -------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            LoadingPhase = "Pre-rendering icons…";
            await _iconsService.LoadAsync();
            AllIcons = _iconsService.Icons;

            Progress<int> progress = new(p => InitProgress = p);
            await _matchingService.InitializeAsync(AllIcons, progress);

            // Grid layout is instantaneous — just pre-compute the spiral order
            _layoutService.Initialize(AllIcons);

            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -------------------------------------------------------------------------
    // Filtering
    // -------------------------------------------------------------------------

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        // Preserve the current selection — FilteredIcons.Clear() would otherwise null it
        // out via the TwoWay SelectedItem binding before the items are re-added.
        FluentIcon? previousSelected = SelectedIcon;

        FilteredIcons.Clear();
        string query = SearchQuery.Trim();

        IEnumerable<FluentIcon> filtered = AllIcons
            .Where(i => string.IsNullOrEmpty(query) ||
                        i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));

        // Matched icons float to top by score; everything else stays alphabetical
        IOrderedEnumerable<FluentIcon> sorted = filtered
            .OrderByDescending(i => i.MatchScore)
            .ThenBy(i => i.DisplayName);

        foreach (FluentIcon? icon in sorted)
            FilteredIcons.Add(icon);

        // Restore selection so the trace overlay survives re-sorts triggered by drawing.
        // It is only replaced when the user explicitly picks a different icon from the list.
        if (previousSelected != null && FilteredIcons.Contains(previousSelected))
            SelectedIcon = previousSelected;
    }

    // -------------------------------------------------------------------------
    // Ink search
    // -------------------------------------------------------------------------

    public async Task SearchByInkAsync(
        IReadOnlyList<IReadOnlyList<Windows.Foundation.Point>> strokes,
        Windows.Foundation.Size canvasSize)
    {
        if (strokes.Count == 0) return;

        float[] inkVector = await Task.Run(() =>
            _matchingService.RenderInkToBitmap(strokes, canvasSize));

        List<(FluentIcon Icon, double Score)> matches = _matchingService.FindSimilar(inkVector, 10, TryMatchMirrors);
        if (matches.Count == 0) return;

        // Clear previous matches
        foreach (FluentIcon icon in AllIcons)
        {
            icon.IsMatch = false;
            icon.MatchScore = 0;
        }

        // Apply new matches
        foreach ((FluentIcon? icon, double score) in matches)
        {
            icon.IsMatch = true;
            icon.MatchScore = score;
        }

        // Re-sort the list so highest-scoring icons appear at top
        ApplyFilter();

        StatusText = $"Best match: {matches[0].Icon.DisplayName} ({matches[0].Score:P0})";
        TopMatchesFound?.Invoke([.. matches.Select(m => m.Icon)]);
    }

    // -------------------------------------------------------------------------
    // Commands
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void ClearCanvas()
    {
        RequestClearCanvas?.Invoke();
        StatusText = "Draw something to search for an icon";
        foreach (FluentIcon icon in AllIcons)
        {
            icon.IsMatch = false;
            icon.MatchScore = 0;
        }
    }

    [RelayCommand]
    private void CopyAsGlyph(bool useXaml)
    {
        if (SelectedIcon is not null)
            _clipboardService.CopyGlyphCode(SelectedIcon, useXaml);
    }

    [RelayCommand]
    private void CopyAsXaml()
    {
        if (SelectedIcon is not null)
            _clipboardService.CopyXamlFontIcon(SelectedIcon);
    }

    [RelayCommand]
    private async Task CopyAsPngAsync(bool useBlack = true)
    {
        if (SelectedIcon is not null)
            await _clipboardService.CopyPngAsync(SelectedIcon, _matchingService, useBlack);
    }

    [RelayCommand]
    private void CopyAsSvg()
    {
        if (SelectedIcon is not null)
            _clipboardService.CopySvg(SelectedIcon, _matchingService);
    }

    [RelayCommand]
    private void SetMapPivot(FluentIcon icon)
    {
        MapPivotIcon = icon;
        SelectedIcon = icon;
        MapStatusText = $"Pivot: {icon.DisplayName} — visual neighbors highlighted";
    }
}
