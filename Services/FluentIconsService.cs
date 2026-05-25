using System.Globalization;
using System.Text.Json;
using WinIconFinder.Models;

namespace WinIconFinder.Services;

/// <summary>
/// Loads Fluent System Icons metadata from the bundled icons.json asset.
/// The JSON format is a flat dictionary: { "ic_fluent_name_size_regular": codepoint_decimal, ... }
/// </summary>
public class FluentIconsService
{
    private IReadOnlyList<FluentIcon>? _icons;

    public IReadOnlyList<FluentIcon> Icons =>
        _icons ?? throw new InvalidOperationException("Icons not loaded. Call LoadAsync() first.");

    public async Task LoadAsync()
    {
        var uri = new Uri("ms-appx:///Assets/icons.json");
        var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(uri);
        var json = await Windows.Storage.FileIO.ReadTextAsync(file);

        // Parse the flat dict: key = "ic_fluent_name_size_regular", value = decimal codepoint
        var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json)
                   ?? throw new InvalidOperationException("Failed to parse icons.json");

        // Group by base name (without size), then pick the variant closest to 24px.
        // Key: baseName (e.g. "arrow_circle_up"), Value: list of (size, key, codepoint)
        var groups = new Dictionary<string, List<(int Size, string Key, int Codepoint)>>(
            StringComparer.Ordinal);

        foreach (var (key, codepoint) in dict)
        {
            if (!key.EndsWith("_regular", StringComparison.Ordinal)) continue;
            if (codepoint is <= 0 or > 0xFFFF) continue;

            if (!TryParseBaseAndSize(key, out var baseName, out var size)) continue;

            if (!groups.TryGetValue(baseName, out var list))
                groups[baseName] = list = [];

            list.Add((size, key, codepoint));
        }

        var icons = new List<FluentIcon>(groups.Count);
        foreach (var (baseName, variants) in groups)
        {
            // Pick the variant whose size is closest to 24; prefer 24 exactly.
            var best = variants.MinBy(v => Math.Abs(v.Size - 24));

            icons.Add(new FluentIcon
            {
                Name        = best.Key,
                DisplayName = BuildDisplayName(baseName),
                Codepoint   = (uint)best.Codepoint,
                GlyphChar   = (char)best.Codepoint
            });
        }

        icons.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        _icons = icons;
    }

    /// <summary>
    /// Splits "ic_fluent_arrow_circle_up_24_regular" into baseName="arrow_circle_up" and size=24.
    /// Returns false if the key doesn't match the expected pattern.
    /// </summary>
    private static bool TryParseBaseAndSize(string key, out string baseName, out int size)
    {
        baseName = "";
        size     = 0;

        const string prefix = "ic_fluent_";
        const string suffix = "_regular";

        if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
            !key.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        // "arrow_circle_up_24"
        var middle = key[prefix.Length..^suffix.Length];

        var lastUnderscore = middle.LastIndexOf('_');
        if (lastUnderscore < 0) return false;

        var sizeToken = middle[(lastUnderscore + 1)..];
        if (!int.TryParse(sizeToken, out size)) return false;

        baseName = middle[..lastUnderscore];
        return baseName.Length > 0;
    }

    private static string BuildDisplayName(string baseName)
    {
        // "arrow_circle_up" → "Arrow Circle Up"
        var textInfo = CultureInfo.InvariantCulture.TextInfo;
        return textInfo.ToTitleCase(baseName.Replace('_', ' '));
    }
}
