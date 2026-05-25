using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation;
using Windows.Storage.Streams;
using WinIconFinder.Models;

namespace WinIconFinder.Services;

/// <summary>
/// Core matching engine: pre-renders all ~1600 Fluent icons to 64×64 bitmaps,
/// L2-normalises them, then finds cosine-similar icons for a given ink sketch.
/// Pre-rendered vectors are cached to disk and reused on subsequent launches.
/// </summary>
public partial class IconMatchingService
{
    public const int GlyphSize = 64;
    public const float BaseFontSize = 52f;

    // ms-appx URI lets Win2D / DirectWrite load the bundled TTF from the package
    public const string FontUri =
        "ms-appx:///Assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";

    // ---- cache ----
    // Bump CacheFormatVersion whenever GlyphSize, BaseFontSize, or rendering
    // logic changes so stale cache files are automatically invalidated.
    private const int CacheFormatVersion = 1;
    private const string CacheFileName = "icon_vectors.bin";
    private static readonly byte[] CacheMagic = [(byte)'W', (byte)'I', (byte)'N', (byte)'F'];

    private IReadOnlyList<FluentIcon>? _icons;
    private float[][]? _glyphVectors;
    private CanvasDevice? _device;
    private bool _initialized;

    /// <summary>Exposes pre-rendered glyph vectors for the similarity layout service.</summary>
    internal float[][] GlyphVectors =>
        _glyphVectors ?? throw new InvalidOperationException("Not initialized. Call InitializeAsync() first.");

    // -------------------------------------------------------------------------
    // Initialisation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pre-renders every icon to a 64×64 grayscale vector (or loads from disk
    /// cache if available). Must be called from the UI thread so the shared
    /// CanvasDevice is captured on the correct thread; heavy work is then
    /// offloaded via Task.Run.
    /// </summary>
    public async Task InitializeAsync(
        IReadOnlyList<FluentIcon> icons,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        _icons = icons;

        // Capture device + cache path on the UI thread before going background.
        _device = CanvasDevice.GetSharedDevice();
        CanvasDevice device = _device;
        string cachePath = Path.Combine(
            Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path,
            CacheFileName);
        int fingerprint = ComputeFingerprint(icons);

        await Task.Run(() =>
        {
            // ── Cache hit ────────────────────────────────────────────────────
            if (TryLoadCache(cachePath, icons.Count, fingerprint))
            {
                progress?.Report(100);
                return;
            }

            // ── Cache miss: render every glyph ────────────────────────────
            _glyphVectors = new float[icons.Count][];
            for (int i = 0; i < icons.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _glyphVectors[i] = RenderGlyphToVector(device, icons[i]);

                if (i % 50 == 0)
                    progress?.Report((int)(100.0 * i / icons.Count));
            }
            progress?.Report(100);

            SaveCache(cachePath, fingerprint);
        }, ct);

        _initialized = true;
    }

    // -------------------------------------------------------------------------
    // Cache helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes a fingerprint over the rendering parameters and the full set of
    /// icon codepoints. Any change to icons or render settings busts the cache.
    /// </summary>
    private static int ComputeFingerprint(IReadOnlyList<FluentIcon> icons)
    {
        HashCode hc = new();
        hc.Add(GlyphSize);
        hc.Add(BaseFontSize);
        foreach (FluentIcon icon in icons)
            hc.Add(icon.Codepoint);
        return hc.ToHashCode();
    }

    /// <summary>
    /// Binary layout (little-endian):
    ///   4 B  magic "WINF"
    ///   4 B  CacheFormatVersion (int32)
    ///   4 B  fingerprint (int32)
    ///   4 B  icon count (int32)
    ///   4 B  vector length = GlyphSize² (int32)
    ///   N × vectorLength × 4 B  raw float32 data
    /// </summary>
    private bool TryLoadCache(string path, int expectedCount, int fingerprint)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 16, useAsync: false);
            using BinaryReader br = new(fs);

            if (!br.ReadBytes(4).SequenceEqual(CacheMagic)) return false;
            if (br.ReadInt32() != CacheFormatVersion) return false;
            if (br.ReadInt32() != fingerprint) return false;

            int iconCount = br.ReadInt32();
            int vectorLen = br.ReadInt32();
            if (iconCount != expectedCount || vectorLen != GlyphSize * GlyphSize) return false;

            float[][] vectors = new float[iconCount][];
            for (int i = 0; i < iconCount; i++)
            {
                float[] vec = new float[vectorLen];
                Span<byte> span = MemoryMarshal.AsBytes(vec.AsSpan());
                if (fs.Read(span) != span.Length) return false;
                vectors[i] = vec;
            }

            _glyphVectors = vectors;
            return true;
        }
        catch
        {
            _glyphVectors = null;
            TryDeleteFile(path);
            return false;
        }
    }

    private void SaveCache(string path, int fingerprint)
    {
        if (_glyphVectors == null) return;
        try
        {
            using FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 16, useAsync: false);
            using BinaryWriter bw = new(fs);

            bw.Write(CacheMagic);
            bw.Write(CacheFormatVersion);
            bw.Write(fingerprint);
            bw.Write(_glyphVectors.Length);
            bw.Write(GlyphSize * GlyphSize);

            foreach (float[] vec in _glyphVectors)
                fs.Write(MemoryMarshal.AsBytes(vec.AsSpan()));
        }
        catch
        {
            // Cache write failure is non-fatal.
            TryDeleteFile(path);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // -------------------------------------------------------------------------
    // Glyph rendering
    // -------------------------------------------------------------------------

    private static float[] RenderGlyphToVector(CanvasDevice device, FluentIcon icon)
    {
        using CanvasRenderTarget rt = new(device, GlyphSize, GlyphSize, 96f);
        using (CanvasDrawingSession ds = rt.CreateDrawingSession())
        {
            ds.Clear(Colors.Black);
            using CanvasTextFormat tf = new()
            {
                FontFamily = FontUri,
                FontSize = BaseFontSize,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
            ds.DrawText(
                icon.GlyphChar.ToString(),
                new Rect(0, 0, GlyphSize, GlyphSize),
                Colors.White,
                tf);
        }
        return BgraToNormalizedGrayscale(rt.GetPixelBytes());
    }

    // -------------------------------------------------------------------------
    // Ink rendering
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renders the current ink strokes to a 64×64 normalised grayscale vector,
    /// scaled to fill the canvas. Safe to call from a background thread after
    /// InitializeAsync completes.
    /// </summary>
    public float[] RenderInkToBitmap(
        IReadOnlyList<IReadOnlyList<Windows.Foundation.Point>> strokes,
        Windows.Foundation.Size canvasSize)
    {
        CanvasDevice device = _device ?? CanvasDevice.GetSharedDevice();
        using CanvasRenderTarget rt = new(device, GlyphSize, GlyphSize, 96f);
        using (CanvasDrawingSession ds = rt.CreateDrawingSession())
        {
            ds.Clear(Colors.Black);

            if (strokes.Count > 0)
                DrawPolyStrokes(ds, strokes, canvasSize);
        }
        return BgraToNormalizedGrayscale(rt.GetPixelBytes());
    }

    private static void DrawPolyStrokes(
        CanvasDrawingSession ds,
        IReadOnlyList<IReadOnlyList<Windows.Foundation.Point>> strokes,
        Windows.Foundation.Size canvasSize)
    {
        double scaleX = canvasSize.Width > 0 ? GlyphSize / canvasSize.Width : 1.0;
        double scaleY = canvasSize.Height > 0 ? GlyphSize / canvasSize.Height : 1.0;
        float scale = (float)Math.Min(scaleX, scaleY);
        float strokeWidth = Math.Max(1.5f, 3f * scale);

        foreach (IReadOnlyList<Point> stroke in strokes)
        {
            IReadOnlyList<Point> pts = stroke;
            if (pts.Count == 0) continue;

            if (pts.Count == 1)
            {
                float px = (float)(pts[0].X * scale);
                float py = (float)(pts[0].Y * scale);
                ds.FillCircle(px, py, strokeWidth, Colors.White);
                continue;
            }

            for (int i = 0; i < pts.Count - 1; i++)
            {
                float x1 = (float)(pts[i].X * scale);
                float y1 = (float)(pts[i].Y * scale);
                float x2 = (float)(pts[i + 1].X * scale);
                float y2 = (float)(pts[i + 1].Y * scale);
                ds.DrawLine(x1, y1, x2, y2, Colors.White, strokeWidth);
            }
        }
    }

    // OLD ink-stroke rendering removed — replaced by DrawPolyStrokes above

    // -------------------------------------------------------------------------
    // Similarity search
    // -------------------------------------------------------------------------

    public List<(FluentIcon Icon, double Score)> FindSimilar(
        float[] inkVector,
        int topN = 10,
        bool tryMirrors = false)
    {
        if (!_initialized || _icons == null || _glyphVectors == null)
            return [];

        float[]? inkH = tryMirrors ? FlipHorizontal(inkVector) : null;
        float[]? inkV = tryMirrors ? FlipVertical(inkVector) : null;

        (FluentIcon Icon, double Score)[] results = new (FluentIcon Icon, double Score)[_icons.Count];
        for (int i = 0; i < _icons.Count; i++)
        {
            float[] gv = _glyphVectors[i];
            int len = Math.Min(inkVector.Length, gv.Length);

            double dot = 0.0;
            for (int j = 0; j < len; j++)
                dot += inkVector[j] * gv[j];

            if (tryMirrors)
            {
                double dotH = 0.0, dotV = 0.0;
                for (int j = 0; j < len; j++)
                {
                    dotH += inkH![j] * gv[j];
                    dotV += inkV![j] * gv[j];
                }
                dot = Math.Max(dot, Math.Max(dotH, dotV));
            }

            results[i] = (_icons[i], dot);
        }

        Array.Sort(results, static (a, b) => b.Score.CompareTo(a.Score));
        return [.. results.Take(topN)];
    }

    /// <summary>Flips a GlyphSize×GlyphSize pixel vector left-to-right.</summary>
    private static float[] FlipHorizontal(float[] vector)
    {
        float[] result = new float[vector.Length];
        for (int row = 0; row < GlyphSize; row++)
            for (int col = 0; col < GlyphSize; col++)
                result[row * GlyphSize + col] = vector[row * GlyphSize + (GlyphSize - 1 - col)];
        return result;
    }

    /// <summary>Flips a GlyphSize×GlyphSize pixel vector top-to-bottom.</summary>
    private static float[] FlipVertical(float[] vector)
    {
        float[] result = new float[vector.Length];
        for (int row = 0; row < GlyphSize; row++)
            for (int col = 0; col < GlyphSize; col++)
                result[row * GlyphSize + col] = vector[(GlyphSize - 1 - row) * GlyphSize + col];
        return result;
    }

    // -------------------------------------------------------------------------
    // PNG export
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renders a single icon glyph to a PNG byte array (black on transparent).
    /// </summary>
    public async Task<byte[]> RenderGlyphToPngAsync(FluentIcon icon, int size = 256, bool useBlack = true)
    {
        CanvasDevice device = _device ?? CanvasDevice.GetSharedDevice();
        using CanvasRenderTarget rt = new(device, size, size, 96f);
        using (CanvasDrawingSession ds = rt.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            using CanvasTextFormat tf = new()
            {
                FontFamily = FontUri,
                FontSize = size * 0.8f,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
            ds.DrawText(
                icon.GlyphChar.ToString(),
                new Rect(0, 0, size, size),
                useBlack ? Colors.Black : Colors.White,
                tf);
        }

        using InMemoryRandomAccessStream stream = new();
        await rt.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);

        DataReader reader = new(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        byte[] bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    // -------------------------------------------------------------------------
    // SVG export
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts the glyph outline directly from the font via Win2D path geometry
    /// and returns a self-contained SVG string with real vector path data.
    /// Uses <c>fill="currentColor"</c> so the icon is theme-aware in Figma etc.
    /// </summary>
    public string GetGlyphSvg(FluentIcon icon)
    {
        const float layoutSize = 512f;
        CanvasDevice device = _device ?? CanvasDevice.GetSharedDevice();

        using CanvasTextFormat tf = new()
        {
            FontFamily = FontUri,
            FontSize = layoutSize * 0.75f,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        using CanvasTextLayout tl = new(device, icon.GlyphChar.ToString(), tf, layoutSize, layoutSize);
        using CanvasGeometry geometry = CanvasGeometry.CreateText(tl);

        Rect bounds = geometry.ComputeBounds();
        SvgPathReceiver receiver = new();
        geometry.SendPathTo(receiver);

        string vb = string.Create(CultureInfo.InvariantCulture,
            $"{bounds.X:F3} {bounds.Y:F3} {bounds.Width:F3} {bounds.Height:F3}");

        return $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="{vb}"><path fill="currentColor" d="{receiver}"/></svg>""";
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static float[] BgraToNormalizedGrayscale(byte[] bgra)
    {
        int pixelCount = bgra.Length / 4;
        float[] result = new float[pixelCount];

        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            float b = bgra[o] / 255f;
            float g = bgra[o + 1] / 255f;
            float r = bgra[o + 2] / 255f;
            result[i] = b * 0.114f + g * 0.587f + r * 0.299f;
        }

        // L2 normalise
        float sumSq = 0f;
        foreach (float v in result) sumSq += v * v;

        if (sumSq > 0f)
        {
            float inv = 1f / MathF.Sqrt(sumSq);
            for (int i = 0; i < result.Length; i++)
                result[i] *= inv;
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // SVG path receiver
    // -------------------------------------------------------------------------

    /// <summary>
    /// Implements <see cref="ICanvasPathReceiver"/> to serialize a Win2D
    /// <see cref="CanvasGeometry"/> into an SVG path <c>d</c> attribute string.
    /// </summary>
    private sealed partial class SvgPathReceiver : ICanvasPathReceiver
    {
        private readonly StringBuilder _sb = new();

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        public void BeginFigure(Vector2 startPoint, CanvasFigureFill figureFill) =>
            _sb.Append($"M{F(startPoint.X)},{F(startPoint.Y)} ");

        public void AddLine(Vector2 endPoint) =>
            _sb.Append($"L{F(endPoint.X)},{F(endPoint.Y)} ");

        public void AddCubicBezier(Vector2 cp1, Vector2 cp2, Vector2 endPoint) =>
            _sb.Append($"C{F(cp1.X)},{F(cp1.Y)} {F(cp2.X)},{F(cp2.Y)} {F(endPoint.X)},{F(endPoint.Y)} ");

        public void AddQuadraticBezier(Vector2 cp, Vector2 endPoint) =>
            _sb.Append($"Q{F(cp.X)},{F(cp.Y)} {F(endPoint.X)},{F(endPoint.Y)} ");

        public void AddArc(Vector2 endPoint, float radiusX, float radiusY,
            float rotationAngle, CanvasSweepDirection sweepDirection, CanvasArcSize arcSize)
        {
            int largeArc = arcSize == CanvasArcSize.Large ? 1 : 0;
            int sweep = sweepDirection == CanvasSweepDirection.Clockwise ? 1 : 0;
            _sb.Append($"A{F(radiusX)},{F(radiusY)} {F(rotationAngle)} {largeArc} {sweep} {F(endPoint.X)},{F(endPoint.Y)} ");
        }

        public void EndFigure(CanvasFigureLoop figureLoop)
        {
            if (figureLoop == CanvasFigureLoop.Closed) _sb.Append("Z ");
        }

        public void SetFilledRegionDetermination(CanvasFilledRegionDetermination value) { }
        public void SetSegmentOptions(CanvasFigureSegmentOptions options) { }

        public override string ToString() => _sb.ToString().TrimEnd();
    }
}
