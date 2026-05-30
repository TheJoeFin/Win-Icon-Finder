using System.Text;
using WinIconFinder.Models;

namespace WinIconFinder.Services;

public sealed class CollectionExportService
{
    public async Task ExportPngsAsync(
        IEnumerable<FluentIcon> icons,
        IconMatchingService matchingService,
        string folderPath,
        bool useBlack,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        Directory.CreateDirectory(folderPath);

        foreach (FluentIcon icon in EnumerateDistinctIcons(icons))
        {
            byte[] pngBytes = await matchingService.RenderGlyphToPngAsync(icon, 256, useBlack);
            string filePath = Path.Combine(folderPath, BuildFileName(icon, ".png"));
            await File.WriteAllBytesAsync(filePath, pngBytes, cancellationToken);
        }
    }

    public async Task ExportSvgsAsync(
        IEnumerable<FluentIcon> icons,
        IconMatchingService matchingService,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        Directory.CreateDirectory(folderPath);

        foreach (FluentIcon icon in EnumerateDistinctIcons(icons))
        {
            string svg = matchingService.GetGlyphSvg(icon);
            string filePath = Path.Combine(folderPath, BuildFileName(icon, ".svg"));
            await File.WriteAllTextAsync(filePath, svg, Encoding.UTF8, cancellationToken);
        }
    }

    private static IEnumerable<FluentIcon> EnumerateDistinctIcons(IEnumerable<FluentIcon> icons)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (FluentIcon icon in icons.OrderBy(icon => icon.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(icon.Name))
            {
                yield return icon;
            }
        }
    }

    private static string BuildFileName(FluentIcon icon, string extension)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string safeName = string.Concat(icon.DisplayName.Select(ch => invalidChars.Contains(ch) ? '_' : ch)).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = icon.Name;
        }

        return $"{safeName}_{icon.Codepoint:X4}{extension}";
    }
}
