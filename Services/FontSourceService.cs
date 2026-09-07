using System.Text;
using Windows.Security.Cryptography;
using Windows.Storage;

namespace WinIconFinder.Services;

public sealed record FontSource(
    string DisplayName,
    string FontUri,
    string CacheIdentity,
    IReadOnlyList<uint> Codepoints,
    bool IsCustom);

/// <summary>
/// Persists the selected font in app-local storage and exposes the font family and mapped glyphs.
/// </summary>
public sealed class FontSourceService
{
    public const string DefaultFontUri = "ms-appx:///Assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";

    private const string FontFolderName = "Fonts";
    private const string ActiveFontFileName = "active-font";
    private const string FontFileExtensionKey = "CustomFontFileExtension";
    private const string FontFamilyNameKey = "CustomFontFamilyName";

    public async Task<FontSource> GetActiveSourceAsync()
    {
        if (ApplicationData.Current.LocalSettings.Values.TryGetValue(FontFileExtensionKey, out object? extensionValue) &&
            extensionValue is string extension &&
            ApplicationData.Current.LocalSettings.Values.TryGetValue(FontFamilyNameKey, out object? familyValue) &&
            familyValue is string familyName)
        {
            StorageFolder fontsFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                FontFolderName,
                CreationCollisionOption.OpenIfExists);
            IStorageItem? storedItem = await fontsFolder.TryGetItemAsync(ActiveFontFileName + extension);
            if (storedItem is StorageFile file)
            {
                try
                {
                    IReadOnlyList<uint> codepoints = await ReadCodepointsAsync(file);
                    if (codepoints.Count > 0)
                    {
                        return new FontSource(
                            familyName,
                            $"ms-appdata:///local/{FontFolderName}/{file.Name}#{familyName}",
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await ReadBytesAsync(file))),
                            codepoints,
                            IsCustom: true);
                    }
                }
                catch (InvalidDataException)
                {
                }
            }

            ClearStoredSelection();
        }

        return new FontSource("Fluent System Icons", DefaultFontUri, "bundled-fluent-system-icons", [], IsCustom: false);
    }

    public async Task<FontSource> SetCustomFontAsync(StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        string extension = Path.GetExtension(file.Name).ToLowerInvariant();
        if (extension is not ".ttf" and not ".otf")
        {
            throw new InvalidDataException("Select a TrueType (.ttf) or OpenType (.otf) font file.");
        }

        byte[] bytes = await ReadBytesAsync(file);
        string familyName = ReadFamilyName(bytes);
        IReadOnlyList<uint> codepoints = ReadCodepoints(bytes);
        if (codepoints.Count == 0)
        {
            throw new InvalidDataException("The selected font does not contain usable Unicode glyphs.");
        }

        StorageFolder fontsFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
            FontFolderName,
            CreationCollisionOption.OpenIfExists);
        StorageFile destination = await fontsFolder.CreateFileAsync(
            ActiveFontFileName + extension,
            CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteBytesAsync(destination, bytes);

        string otherExtension = extension == ".ttf" ? ".otf" : ".ttf";
        IStorageItem? obsoleteFile = await fontsFolder.TryGetItemAsync(ActiveFontFileName + otherExtension);
        if (obsoleteFile is StorageFile oldFile)
        {
            await oldFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }

        ApplicationData.Current.LocalSettings.Values[FontFileExtensionKey] = extension;
        ApplicationData.Current.LocalSettings.Values[FontFamilyNameKey] = familyName;

        return new FontSource(
            familyName,
            $"ms-appdata:///local/{FontFolderName}/{destination.Name}#{familyName}",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            codepoints,
            IsCustom: true);
    }

    public async Task ClearCustomFontAsync()
    {
        if (ApplicationData.Current.LocalSettings.Values.TryGetValue(FontFileExtensionKey, out object? extensionValue) &&
            extensionValue is string extension)
        {
            StorageFolder fontsFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                FontFolderName,
                CreationCollisionOption.OpenIfExists);
            IStorageItem? file = await fontsFolder.TryGetItemAsync(ActiveFontFileName + extension);
            if (file is StorageFile fontFile)
            {
                await fontFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
        }

        ClearStoredSelection();
    }

    private static async Task<IReadOnlyList<uint>> ReadCodepointsAsync(StorageFile file) =>
        ReadCodepoints(await ReadBytesAsync(file));

    private static async Task<byte[]> ReadBytesAsync(StorageFile file)
    {
        Windows.Storage.Streams.IBuffer buffer = await FileIO.ReadBufferAsync(file);
        CryptographicBuffer.CopyToByteArray(buffer, out byte[] bytes);
        return bytes;
    }

    private static void ClearStoredSelection()
    {
        ApplicationData.Current.LocalSettings.Values.Remove(FontFileExtensionKey);
        ApplicationData.Current.LocalSettings.Values.Remove(FontFamilyNameKey);
    }

    private static string ReadFamilyName(byte[] data)
    {
        (int offset, int tableLength) = GetTable(data, "name");
        EnsureRange(data, offset, 6);
        int count = ReadUInt16(data, offset + 2);
        int stringOffset = ReadUInt16(data, offset + 4);
        int recordsOffset = offset + 6;
        EnsureRange(data, recordsOffset, count * 12);

        string? fallback = null;
        for (int i = 0; i < count; i++)
        {
            int record = recordsOffset + (i * 12);
            int platformId = ReadUInt16(data, record);
            int languageId = ReadUInt16(data, record + 4);
            int nameId = ReadUInt16(data, record + 6);
            int valueLength = ReadUInt16(data, record + 8);
            int valueOffset = offset + stringOffset + ReadUInt16(data, record + 10);
            if (nameId is not 1 and not 16)
            {
                continue;
            }

            EnsureRange(data, valueOffset, valueLength);
            string value = platformId is 0 or 3
                ? Encoding.BigEndianUnicode.GetString(data, valueOffset, valueLength)
                : Encoding.Latin1.GetString(data, valueOffset, valueLength);
            value = value.Trim('\0').Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (nameId == 16 && (languageId == 0x0409 || platformId == 0))
            {
                return value;
            }

            fallback ??= value;
        }

        return fallback ?? throw new InvalidDataException("The font does not contain a family name.");
    }

    private static IReadOnlyList<uint> ReadCodepoints(byte[] data)
    {
        (int offset, int length) = GetTable(data, "cmap");
        EnsureRange(data, offset, 4);
        int count = ReadUInt16(data, offset + 2);
        EnsureRange(data, offset + 4, count * 8);

        int format12Offset = -1;
        int format4Offset = -1;
        for (int i = 0; i < count; i++)
        {
            int record = offset + 4 + (i * 8);
            int subtableOffset = offset + (int)ReadUInt32(data, record + 4);
            EnsureRange(data, subtableOffset, 2);
            int format = ReadUInt16(data, subtableOffset);
            if (format == 12)
            {
                format12Offset = subtableOffset;
            }
            else if (format == 4)
            {
                format4Offset = subtableOffset;
            }
        }

        SortedSet<uint> codepoints = [];
        if (format12Offset >= 0)
        {
            AddFormat12Codepoints(data, format12Offset, codepoints);
        }
        else if (format4Offset >= 0)
        {
            AddFormat4Codepoints(data, format4Offset, codepoints);
        }
        else
        {
            throw new InvalidDataException("The font does not have a supported Unicode character map.");
        }

        return [.. codepoints];
    }

    private static void AddFormat12Codepoints(byte[] data, int offset, ISet<uint> codepoints)
    {
        EnsureRange(data, offset, 16);
        int groupCount = checked((int)ReadUInt32(data, offset + 12));
        EnsureRange(data, offset + 16, checked(groupCount * 12));

        for (int i = 0; i < groupCount; i++)
        {
            int group = offset + 16 + (i * 12);
            uint start = ReadUInt32(data, group);
            uint end = Math.Min(ReadUInt32(data, group + 4), 0x10FFFF);
            uint glyph = ReadUInt32(data, group + 8);
            for (uint codepoint = start; codepoint <= end; codepoint++, glyph++)
            {
                if (codepoint != 0 && codepoint is not (>= 0xD800 and <= 0xDFFF) && glyph != 0)
                {
                    codepoints.Add(codepoint);
                }
            }
        }
    }

    private static void AddFormat4Codepoints(byte[] data, int offset, ISet<uint> codepoints)
    {
        EnsureRange(data, offset, 14);
        int length = ReadUInt16(data, offset + 2);
        EnsureRange(data, offset, length);
        int segmentCount = ReadUInt16(data, offset + 6) / 2;
        int endCodes = offset + 14;
        int startCodes = endCodes + (segmentCount * 2) + 2;
        int deltas = startCodes + (segmentCount * 2);
        int rangeOffsets = deltas + (segmentCount * 2);
        EnsureRange(data, rangeOffsets, segmentCount * 2);

        for (int i = 0; i < segmentCount; i++)
        {
            ushort start = ReadUInt16(data, startCodes + (i * 2));
            ushort end = ReadUInt16(data, endCodes + (i * 2));
            short delta = unchecked((short)ReadUInt16(data, deltas + (i * 2)));
            ushort rangeOffset = ReadUInt16(data, rangeOffsets + (i * 2));
            for (uint codepoint = start; codepoint <= end && codepoint != 0xFFFF; codepoint++)
            {
                ushort glyph = rangeOffset == 0
                    ? unchecked((ushort)(codepoint + delta))
                    : ReadGlyphIndex(data, rangeOffsets + (i * 2) + rangeOffset + (int)((codepoint - start) * 2), delta);
                if (codepoint != 0 && glyph != 0)
                {
                    codepoints.Add(codepoint);
                }
            }
        }
    }

    private static ushort ReadGlyphIndex(byte[] data, int offset, short delta)
    {
        EnsureRange(data, offset, 2);
        ushort glyph = ReadUInt16(data, offset);
        return glyph == 0 ? (ushort)0 : unchecked((ushort)(glyph + delta));
    }

    private static (int Offset, int Length) GetTable(byte[] data, string tag)
    {
        EnsureRange(data, 0, 12);
        int count = ReadUInt16(data, 4);
        EnsureRange(data, 12, count * 16);
        for (int i = 0; i < count; i++)
        {
            int record = 12 + (i * 16);
            if (Encoding.ASCII.GetString(data, record, 4) == tag)
            {
                int offset = checked((int)ReadUInt32(data, record + 8));
                int length = checked((int)ReadUInt32(data, record + 12));
                EnsureRange(data, offset, length);
                return (offset, length);
            }
        }

        throw new InvalidDataException($"The font does not contain a {tag} table.");
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static void EnsureRange(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new InvalidDataException("The font file is malformed.");
        }
    }
}
