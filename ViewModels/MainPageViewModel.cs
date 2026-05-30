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
    private readonly IconCollectionsService _collectionsService = new();
    private readonly CollectionExportService _collectionExportService = new();
    private readonly List<FluentIcon> _selectedCollectionIcons = [];

    private Dictionary<string, FluentIcon> _iconsByName = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    public partial IReadOnlyList<FluentIcon> AllIcons { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<FluentIcon> FilteredIcons { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<IconCollection> Collections { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<FluentIcon> CollectionIcons { get; set; } = [];

    [ObservableProperty]
    public partial FluentIcon? SelectedIcon { get; set; }

    [ObservableProperty]
    public partial IconCollection? SelectedCollection { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int InitProgress { get; set; }

    [ObservableProperty]
    public partial int SelectedCollectionIconCount { get; set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Draw something to search for an icon";

    [ObservableProperty]
    public partial string CollectionsStatusText { get; set; } = "Open Collections to organize saved icons";

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

    public bool HasSelectedCollectionIcons => SelectedCollectionIconCount > 0;

    public IReadOnlyList<FluentIcon> SelectedCollectionIcons => _selectedCollectionIcons;

    partial void OnTryMatchMirrorsChanged(bool value) => RequestResearch?.Invoke();

    partial void OnIsMapModeChanged(bool value) => MapModeChanged?.Invoke(value);

    partial void OnSelectedCollectionChanged(IconCollection? value)
    {
        RefreshCollectionIcons();
    }

    partial void OnSelectedCollectionIconCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelectedCollectionIcons));
        RefreshCollectionsStatus();
    }

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    public event Action? RequestClearCanvas;

    public event Action? RequestResearch;

    public event Action<IList<FluentIcon>>? TopMatchesFound;

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
            _iconsByName = AllIcons.ToDictionary(icon => icon.Name, StringComparer.OrdinalIgnoreCase);

            await _collectionsService.LoadAsync();
            SynchronizeCollectionState();

            Progress<int> progress = new(p => InitProgress = p);
            await _matchingService.InitializeAsync(AllIcons, progress);

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
        FluentIcon? previousSelected = SelectedIcon;

        FilteredIcons.Clear();
        string query = SearchQuery.Trim();

        IEnumerable<FluentIcon> filtered = AllIcons
            .Where(icon => string.IsNullOrEmpty(query)
                || icon.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));

        IOrderedEnumerable<FluentIcon> sorted = filtered
            .OrderByDescending(icon => icon.MatchScore)
            .ThenBy(icon => icon.DisplayName, StringComparer.OrdinalIgnoreCase);

        foreach (FluentIcon icon in sorted)
        {
            FilteredIcons.Add(icon);
        }

        if (previousSelected is not null && FilteredIcons.Contains(previousSelected))
        {
            SelectedIcon = previousSelected;
        }
    }

    // -------------------------------------------------------------------------
    // Collections
    // -------------------------------------------------------------------------

    public bool IsIconInCollection(FluentIcon icon, string collectionName) =>
        _collectionsService.IsIconInCollection(collectionName, icon.Name);

    public void SetSelectedCollectionIcons(IEnumerable<FluentIcon> icons)
    {
        _selectedCollectionIcons.Clear();

        foreach (FluentIcon icon in icons
                     .OrderBy(icon => icon.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .DistinctBy(icon => icon.Name, StringComparer.OrdinalIgnoreCase))
        {
            _selectedCollectionIcons.Add(icon);
        }

        SelectedCollectionIconCount = _selectedCollectionIcons.Count;
    }

    public async Task ToggleFavoriteAsync(FluentIcon icon)
    {
        await _collectionsService.ToggleDefaultMembershipAsync(icon.Name);
        SynchronizeCollectionState(SelectedCollection?.Name);

        StatusText = icon.IsFavorite
            ? $"Added {icon.DisplayName} to {IconCollectionsService.DefaultCollectionName}"
            : $"Removed {icon.DisplayName} from {IconCollectionsService.DefaultCollectionName}";
    }

    public async Task SetIconCollectionMembershipAsync(FluentIcon icon, string collectionName, bool isMember)
    {
        await _collectionsService.SetMembershipAsync(collectionName, [icon.Name], isMember);
        SynchronizeCollectionState(SelectedCollection?.Name);

        StatusText = isMember
            ? $"Added {icon.DisplayName} to {collectionName}"
            : $"Removed {icon.DisplayName} from {collectionName}";
    }

    public async Task<string> CreateCollectionAsync(string collectionName, IEnumerable<FluentIcon>? initialIcons = null)
    {
        string createdCollectionName = await _collectionsService.CreateCollectionAsync(
            collectionName,
            initialIcons?.Select(icon => icon.Name));

        SynchronizeCollectionState(createdCollectionName);

        int initialCount = initialIcons?.DistinctBy(icon => icon.Name, StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        StatusText = initialCount > 0
            ? $"Created {createdCollectionName} with {initialCount} icon{(initialCount == 1 ? "" : "s")}"
            : $"Created {createdCollectionName}";

        return createdCollectionName;
    }

    public async Task AddIconsToCollectionAsync(string collectionName, IEnumerable<FluentIcon> icons)
    {
        IReadOnlyList<string> iconNames = icons
            .DistinctBy(icon => icon.Name, StringComparer.OrdinalIgnoreCase)
            .Select(icon => icon.Name)
            .ToList();

        if (iconNames.Count == 0)
        {
            return;
        }

        await _collectionsService.SetMembershipAsync(collectionName, iconNames, true);
        SynchronizeCollectionState(SelectedCollection?.Name);

        StatusText = iconNames.Count == 1
            ? $"Added {icons.First().DisplayName} to {collectionName}"
            : $"Added {iconNames.Count} icons to {collectionName}";
    }

    public async Task RemoveSelectedIconsFromCurrentCollectionAsync()
    {
        if (SelectedCollection is null || _selectedCollectionIcons.Count == 0)
        {
            return;
        }

        await _collectionsService.SetMembershipAsync(
            SelectedCollection.Name,
            _selectedCollectionIcons.Select(icon => icon.Name),
            false);

        string collectionName = SelectedCollection.Name;
        int removedCount = _selectedCollectionIcons.Count;

        SynchronizeCollectionState(collectionName);

        StatusText = removedCount == 1
            ? $"Removed 1 icon from {collectionName}"
            : $"Removed {removedCount} icons from {collectionName}";
    }

    public void CopySelectedCollectionGlyphs(bool useXaml)
    {
        if (_selectedCollectionIcons.Count == 0)
        {
            return;
        }

        _clipboardService.CopyGlyphCodes(_selectedCollectionIcons, useXaml);
        StatusText = $"Copied {SelectedCollectionIconCount} glyph code{(SelectedCollectionIconCount == 1 ? "" : "s")}";
    }

    public void CopySelectedCollectionXaml()
    {
        if (_selectedCollectionIcons.Count == 0)
        {
            return;
        }

        _clipboardService.CopyXamlFontIcons(_selectedCollectionIcons);
        StatusText = $"Copied {SelectedCollectionIconCount} XAML snippet{(SelectedCollectionIconCount == 1 ? "" : "s")}";
    }

    public async Task ExportSelectedCollectionPngsAsync(string folderPath, bool useBlack)
    {
        if (_selectedCollectionIcons.Count == 0)
        {
            return;
        }

        await _collectionExportService.ExportPngsAsync(_selectedCollectionIcons, _matchingService, folderPath, useBlack);
        StatusText = $"Exported {SelectedCollectionIconCount} PNG file{(SelectedCollectionIconCount == 1 ? "" : "s")}";
    }

    public async Task ExportSelectedCollectionSvgsAsync(string folderPath)
    {
        if (_selectedCollectionIcons.Count == 0)
        {
            return;
        }

        await _collectionExportService.ExportSvgsAsync(_selectedCollectionIcons, _matchingService, folderPath);
        StatusText = $"Exported {SelectedCollectionIconCount} SVG file{(SelectedCollectionIconCount == 1 ? "" : "s")}";
    }

    private void SynchronizeCollectionState(string? preferredCollectionName = null)
    {
        IReadOnlyList<IconCollectionRecord> collectionRecords = _collectionsService.GetCollections();

        HashSet<string> defaultIconNames = new(
            _collectionsService.GetIconNames(IconCollectionsService.DefaultCollectionName),
            StringComparer.OrdinalIgnoreCase);

        Dictionary<string, int> iconMembershipCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (IconCollectionRecord collectionRecord in collectionRecords)
        {
            foreach (string iconName in collectionRecord.IconNames)
            {
                iconMembershipCounts.TryGetValue(iconName, out int count);
                iconMembershipCounts[iconName] = count + 1;
            }
        }

        foreach (FluentIcon icon in AllIcons)
        {
            iconMembershipCounts.TryGetValue(icon.Name, out int count);
            icon.IsInDefaultCollection = defaultIconNames.Contains(icon.Name);
            icon.CollectionCount = count;
        }

        string? selectedCollectionName = preferredCollectionName ?? SelectedCollection?.Name;

        Collections.Clear();
        foreach (IconCollectionRecord collectionRecord in collectionRecords)
        {
            Collections.Add(new IconCollection(
                collectionRecord.Name,
                string.Equals(collectionRecord.Name, IconCollectionsService.DefaultCollectionName, StringComparison.OrdinalIgnoreCase),
                collectionRecord.IconNames.Count));
        }

        SelectedCollection = Collections.FirstOrDefault(collection =>
                                 string.Equals(collection.Name, selectedCollectionName, StringComparison.OrdinalIgnoreCase))
                             ?? Collections.FirstOrDefault(collection => collection.IsDefault)
                             ?? Collections.FirstOrDefault();
    }

    private void RefreshCollectionIcons()
    {
        CollectionIcons.Clear();

        if (SelectedCollection is null)
        {
            _selectedCollectionIcons.Clear();
            SelectedCollectionIconCount = 0;
            RefreshCollectionsStatus();
            return;
        }

        foreach (string iconName in _collectionsService.GetIconNames(SelectedCollection.Name))
        {
            if (_iconsByName.TryGetValue(iconName, out FluentIcon? icon))
            {
                CollectionIcons.Add(icon);
            }
        }

        _selectedCollectionIcons.Clear();
        SelectedCollectionIconCount = 0;
        RefreshCollectionsStatus();
    }

    private void RefreshCollectionsStatus()
    {
        if (SelectedCollection is null)
        {
            CollectionsStatusText = "Open Collections to organize saved icons";
            return;
        }

        if (CollectionIcons.Count == 0)
        {
            CollectionsStatusText = $"{SelectedCollection.Name} is empty";
            return;
        }

        CollectionsStatusText = SelectedCollectionIconCount > 0
            ? $"{SelectedCollectionIconCount} of {CollectionIcons.Count} selected in {SelectedCollection.Name}"
            : $"{CollectionIcons.Count} icon{(CollectionIcons.Count == 1 ? "" : "s")} in {SelectedCollection.Name}";
    }

    // -------------------------------------------------------------------------
    // Ink search
    // -------------------------------------------------------------------------

    public async Task SearchByInkAsync(
        IReadOnlyList<IReadOnlyList<Windows.Foundation.Point>> strokes,
        Windows.Foundation.Size canvasSize)
    {
        if (strokes.Count == 0)
        {
            return;
        }

        float[] inkVector = await Task.Run(() =>
            _matchingService.RenderInkToBitmap(strokes, canvasSize));

        List<(FluentIcon Icon, double Score)> matches = _matchingService.FindSimilar(inkVector, 10, TryMatchMirrors);
        if (matches.Count == 0)
        {
            return;
        }

        foreach (FluentIcon icon in AllIcons)
        {
            icon.IsMatch = false;
            icon.MatchScore = 0;
        }

        foreach ((FluentIcon icon, double score) in matches)
        {
            icon.IsMatch = true;
            icon.MatchScore = score;
        }

        ApplyFilter();

        StatusText = $"Best match: {matches[0].Icon.DisplayName} ({matches[0].Score:P0})";
        TopMatchesFound?.Invoke([.. matches.Select(match => match.Icon)]);
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
        {
            _clipboardService.CopyGlyphCode(SelectedIcon, useXaml);
        }
    }

    [RelayCommand]
    private void CopyAsXaml()
    {
        if (SelectedIcon is not null)
        {
            _clipboardService.CopyXamlFontIcon(SelectedIcon);
        }
    }

    [RelayCommand]
    private async Task CopyAsPngAsync(bool useBlack = true)
    {
        if (SelectedIcon is not null)
        {
            await _clipboardService.CopyPngAsync(SelectedIcon, _matchingService, useBlack);
        }
    }

    [RelayCommand]
    private void CopyAsSvg()
    {
        if (SelectedIcon is not null)
        {
            _clipboardService.CopySvg(SelectedIcon, _matchingService);
        }
    }

    [RelayCommand]
    private void SetMapPivot(FluentIcon icon)
    {
        MapPivotIcon = icon;
        SelectedIcon = icon;
        MapStatusText = $"Pivot: {icon.DisplayName} — visual neighbors highlighted";
    }
}
