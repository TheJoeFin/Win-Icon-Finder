using CommunityToolkit.Mvvm.ComponentModel;

namespace WinIconFinder.Models;

public partial class FluentIcon : ObservableObject
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public uint Codepoint { get; init; }
    public Microsoft.UI.Xaml.Media.FontFamily FontFamily { get; init; } =
        new(Services.FontSourceService.DefaultFontUri);

    /// <summary>Unicode scalar text for XAML and Win2D glyph rendering.</summary>
    public string GlyphString => char.ConvertFromUtf32(checked((int)Codepoint));

    /// <summary>Formatted as U+XXXX for display.</summary>
    public string CodepointHex => $"U+{Codepoint:X4}";

    /// <summary>Escape sequence for clipboard export.</summary>
    public string CodepointEscape => Codepoint <= 0xFFFF
        ? $"\\u{Codepoint:X4}"
        : $"\\U{Codepoint:X8}";

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
