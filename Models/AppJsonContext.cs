using System.Text.Json.Serialization;

namespace DrawSymbolFinder.Models;

/// <summary>
/// Source-generated JSON metadata so serialization works under PublishTrimmed
/// (reflection-based System.Text.Json is disabled when trimming).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(IconCollectionsStore))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
