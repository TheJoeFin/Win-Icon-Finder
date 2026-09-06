using Windows.Foundation;

namespace WinIconFinder.Models;

public sealed record InkStrokeData(IReadOnlyList<Point> Points, float Width);
