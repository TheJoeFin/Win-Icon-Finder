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
        DataPackage dp = new();
        dp.SetText(useXaml
            ? $"&#x{icon.Codepoint:X4};"
            : $"\\u{icon.Codepoint:X4}");
        Clipboard.SetContent(dp);
    }

    /// <summary>Copies a WinUI 3 FontIcon XAML snippet using SymbolThemeFontFamily.</summary>
    public void CopyXamlFontIcon(FluentIcon icon)
    {
        DataPackage dp = new();
        dp.SetText(
            $$"""<FontIcon FontFamily="{ThemeResource SymbolThemeFontFamily}" Glyph="&#x{{icon.Codepoint:X4}};" />""");
        Clipboard.SetContent(dp);
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
        DataPackage dp = new();
        dp.SetText(svg);
        Clipboard.SetContent(dp);
    }
}
