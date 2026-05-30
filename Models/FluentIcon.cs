using CommunityToolkit.Mvvm.ComponentModel;

namespace WinIconFinder.Models;

public partial class FluentIcon : ObservableObject
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public uint Codepoint { get; init; }
    public char GlyphChar { get; init; }

    /// <summary>Single-char string for XAML TextBlock binding.</summary>
    public string GlyphString => GlyphChar.ToString();

    /// <summary>Formatted as U+XXXX for display.</summary>
    public string CodepointHex => $"U+{Codepoint:X4}";

    /// <summary>Escape sequence for clipboard export.</summary>
    public string CodepointEscape => $"\\u{Codepoint:X4}";

    public bool IsFavorite => IsInDefaultCollection;

    public string MetadataText =>
        CollectionCount > 0
            ? $"{CodepointHex} · {CollectionCountLabel}"
            : CodepointHex;

    public string CollectionCountLabel =>
        CollectionCount == 1 ? "1 collection" : $"{CollectionCount:N0} collections";

    [ObservableProperty]
    public partial bool IsInDefaultCollection { get; set; }

    [ObservableProperty]
    public partial int CollectionCount { get; set; }

    [ObservableProperty]
    public partial bool IsMatch { get; set; }

    [ObservableProperty]
    public partial double MatchScore { get; set; }

    partial void OnIsInDefaultCollectionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(MetadataText));
    }

    partial void OnCollectionCountChanged(int value) => OnPropertyChanged(nameof(MetadataText));
}
