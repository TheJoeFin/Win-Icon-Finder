using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using DrawSymbolFinder.Models;

namespace DrawSymbolFinder.Services;

public partial class ClipboardExportService
{
    public void CopyGlyphCode(FluentIcon icon, bool useXaml)
    {
        SetText(useXaml ? $"&#x{icon.Codepoint:X4};" : icon.CodepointEscape);
    }

    public void CopyGlyphCodes(IEnumerable<FluentIcon> icons, bool useXaml)
    {
        SetText(string.Join(Environment.NewLine, icons
            .OrderBy(icon => icon.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(icon => useXaml ? $"&#x{icon.Codepoint:X4};" : icon.CodepointEscape)));
    }

    public void CopyXamlFontIcon(FluentIcon icon, string fontUri)
    {
        SetText($"<FontIcon FontFamily=\"{fontUri}\" Glyph=\"&#x{icon.Codepoint:X4};\" />");
    }

    public void CopyXamlFontIcons(IEnumerable<FluentIcon> icons, string fontUri)
    {
        SetText(string.Join(Environment.NewLine, icons
            .OrderBy(icon => icon.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(icon => $"<FontIcon FontFamily=\"{fontUri}\" Glyph=\"&#x{icon.Codepoint:X4};\" />")));
    }

    public void CopyXamlPathIcon(FluentIcon icon, IconMatchingService matchingService)
    {
        SetText(BuildPathIconMarkup(icon, matchingService));
    }

    public void CopyXamlPathIcons(IEnumerable<FluentIcon> icons, IconMatchingService matchingService)
    {
        SetText(string.Join(Environment.NewLine, icons
            .OrderBy(icon => icon.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(icon => BuildPathIconMarkup(icon, matchingService))));
    }

    private static string BuildPathIconMarkup(FluentIcon icon, IconMatchingService matchingService) =>
        $"<PathIcon Data=\"{matchingService.GetGlyphPathData(icon)}\" />";

    public async Task CopyPngAsync(FluentIcon icon, IconMatchingService matchingService, bool useBlack = true)
    {
        byte[] pngBytes = await matchingService.RenderGlyphToPngAsync(icon, 256, useBlack);
        InMemoryRandomAccessStream stream = new();
        using (DataWriter writer = new(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
        }
        stream.Seek(0);

        DataPackage dataPackage = new();
        dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Clipboard.SetContent(dataPackage);
    }

    public void CopySvg(FluentIcon icon, IconMatchingService matchingService, bool useBlack = true)
    {
        SetText(matchingService.GetGlyphSvg(icon, useBlack));
    }

    private static void SetText(string text)
    {
        DataPackage dataPackage = new();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
    }
}
