using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinIconFinder.Models;

namespace WinIconFinder.Services;

/// <summary>
/// Copies icon data to the clipboard in various formats.
/// All public methods must be called from the UI thread.
/// </summary>
public partial class ClipboardExportService
{
    /// <summary>Copies the glyph code. C# format: \uXXXX, XAML format: &amp;#xXXXX;</summary>
    public void CopyGlyphCode(FluentIcon icon, bool useXaml)
    {
        SetText(useXaml
            ? $"&#x{icon.Codepoint:X4};"
            : $"\\u{icon.Codepoint:X4}");
    }

    public void CopyGlyphCodes(IEnumerable<FluentIcon> icons, bool useXaml)
    {
        string text = string.Join(
            Environment.NewLine,
            icons
                .OrderBy(icon => icon.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(icon => useXaml
                    ? $"&#x{icon.Codepoint:X4};"
                    : $"\\u{icon.Codepoint:X4}"));

        SetText(text);
    }

    /// <summary>Copies a WinUI 3 FontIcon XAML snippet using the bundled Fluent icon font.</summary>
    public void CopyXamlFontIcon(FluentIcon icon)
    {
        SetText(
            $$"""<FontIcon FontFamily="{{IconMatchingService.FontUri}}" Glyph="&#x{{icon.Codepoint:X4}};" />""");
    }

    public void CopyXamlFontIcons(IEnumerable<FluentIcon> icons)
    {
        string text = string.Join(
            Environment.NewLine,
            icons
                .OrderBy(icon => icon.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(icon =>
                    $$"""<FontIcon FontFamily="{{IconMatchingService.FontUri}}" Glyph="&#x{{icon.Codepoint:X4}};" />"""));

        SetText(text);
    }

    /// <summary>Renders the icon to 256×256 PNG and copies it as a bitmap.</summary>
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

        DataPackage dp = new();
        dp.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Clipboard.SetContent(dp);
    }

    /// <summary>Copies a real SVG with vector path data extracted from the font glyph outline.</summary>
    public void CopySvg(FluentIcon icon, IconMatchingService matchingService)
    {
        string svg = matchingService.GetGlyphSvg(icon);
        SetText(svg);
    }

    private static void SetText(string text)
    {
        DataPackage dp = new();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }
}
