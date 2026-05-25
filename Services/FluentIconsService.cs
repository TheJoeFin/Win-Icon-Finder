using System.Globalization;
using System.Text.Json;
using Windows.Storage;
using WinIconFinder.Models;

namespace WinIconFinder.Services;

/// <summary>
/// Loads Fluent System Icons metadata from the bundled icons.json asset.
/// The JSON format is a flat dictionary: { "ic_fluent_name_size_regular": codepoint_decimal, ... }
/// </summary>
public partial class FluentIconsService
{
    private IReadOnlyList<FluentIcon>? _icons;

    public IReadOnlyList<FluentIcon> Icons =>
        _icons ?? throw new InvalidOperationException("Icons not loaded. Call LoadAsync() first.");

    public async Task LoadAsync()
    {
        Uri uri = new("ms-appx:///Assets/icons.json");
        StorageFile file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(uri);
        string json = await Windows.Storage.FileIO.ReadTextAsync(file);

        // Parse the flat dict: key = "ic_fluent_name_size_regular", value = decimal codepoint
        Dictionary<string, int> dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json)
                   ?? throw new InvalidOperationException("Failed to parse icons.json");

        // Group by base name (without size), then pick the variant closest to 24px.
        // Key: baseName (e.g. "arrow_circle_up"), Value: list of (size, key, codepoint)
        Dictionary<string, List<(int Size, string Key, int Codepoint)>> groups = new(
            StringComparer.Ordinal);

        foreach ((string? key, int codepoint) in dict)
        {
            if (!key.EndsWith("_regular", StringComparison.Ordinal)) continue;
            if (codepoint is <= 0 or > 0xFFFF) continue;

            if (!TryParseBaseAndSize(key, out string? baseName, out int size)) continue;

            if (!groups.TryGetValue(baseName, out List<(int Size, string Key, int Codepoint)>? list))
                groups[baseName] = list = [];

            list.Add((size, key, codepoint));
        }

        List<FluentIcon> icons = new(groups.Count);
        foreach ((string? baseName, List<(int Size, string Key, int Codepoint)>? variants) in groups)
        {
            // Pick the variant whose size is closest to 24; prefer 24 exactly.
            (int Size, string Key, int Codepoint) = variants.MinBy(v => Math.Abs(v.Size - 24));

            icons.Add(new FluentIcon
            {
                Name = Key,
                DisplayName = BuildDisplayName(baseName),
                Codepoint = (uint)Codepoint,
                GlyphChar = (char)Codepoint
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
        size = 0;

        const string prefix = "ic_fluent_";
        const string suffix = "_regular";

        if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
            !key.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        // "arrow_circle_up_24"
        string middle = key[prefix.Length..^suffix.Length];

        int lastUnderscore = middle.LastIndexOf('_');
        if (lastUnderscore < 0) return false;

        string sizeToken = middle[(lastUnderscore + 1)..];
        if (!int.TryParse(sizeToken, out size)) return false;

        baseName = middle[..lastUnderscore];
        return baseName.Length > 0;
    }

    private static string BuildDisplayName(string baseName)
    {
        // "arrow_circle_up" → "Arrow Circle Up"
        TextInfo textInfo = CultureInfo.InvariantCulture.TextInfo;
        return textInfo.ToTitleCase(baseName.Replace('_', ' '));
    }
}
