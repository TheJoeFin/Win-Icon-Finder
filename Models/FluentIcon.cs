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

    [ObservableProperty]
    public partial bool IsMatch { get; set; }

    [ObservableProperty]
    public partial double MatchScore { get; set; }
}
