using Windows.Foundation;

namespace DrawSymbolFinder.Models;

public sealed record InkStrokeData(IReadOnlyList<Point> Points, float Width);
