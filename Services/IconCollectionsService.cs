using System.Text.Json;
using WinIconFinder.Models;

namespace WinIconFinder.Services;

public sealed class IconCollectionsService
{
    public const string DefaultCollectionName = "Default";

    private const string StoreFileName = "icon-collections.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HashSet<string>> _collections = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public async Task LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_loaded)
            {
                return;
            }

            _collections.Clear();

            if (File.Exists(StorePath))
            {
                string json = await File.ReadAllTextAsync(StorePath);
                IconCollectionsStore? store = JsonSerializer.Deserialize<IconCollectionsStore>(json, JsonOptions);
                if (store is null)
                {
                    throw new InvalidOperationException("Failed to parse the collections store.");
                }

                foreach (IconCollectionRecord record in store.Collections)
                {
                    AddCollectionLocked(record.Name, record.IconNames);
                }
            }

            EnsureDefaultCollectionLocked();
            _loaded = true;

            await SaveStoreLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<IconCollectionRecord> GetCollections()
    {
        EnsureLoaded();

        return _collections
            .OrderBy(kvp => string.Equals(kvp.Key, DefaultCollectionName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => new IconCollectionRecord
            {
                Name = kvp.Key,
                IconNames = [.. kvp.Value.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)]
            })
            .ToList();
    }

    public IReadOnlyList<string> GetCollectionsForIcon(string iconName)
    {
        EnsureLoaded();

        string normalizedIconName = NormalizeIconName(iconName);

        return _collections
            .Where(kvp => kvp.Value.Contains(normalizedIconName))
            .OrderBy(kvp => string.Equals(kvp.Key, DefaultCollectionName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    public IReadOnlyList<string> GetIconNames(string collectionName)
    {
        EnsureLoaded();

        string normalizedCollectionName = NormalizeCollectionName(collectionName);
        if (!_collections.TryGetValue(normalizedCollectionName, out HashSet<string>? iconNames))
        {
            throw new InvalidOperationException($"Collection '{normalizedCollectionName}' does not exist.");
        }

        return [.. iconNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    public bool IsIconInCollection(string collectionName, string iconName)
    {
        EnsureLoaded();

        string normalizedCollectionName = NormalizeCollectionName(collectionName);
        string normalizedIconName = NormalizeIconName(iconName);

        return _collections.TryGetValue(normalizedCollectionName, out HashSet<string>? iconNames)
            && iconNames.Contains(normalizedIconName);
    }

    public async Task ToggleDefaultMembershipAsync(string iconName)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureLoaded();

            string normalizedIconName = NormalizeIconName(iconName);
            HashSet<string> defaultIcons = _collections[DefaultCollectionName];
            bool shouldAdd = !defaultIcons.Contains(normalizedIconName);

            SetMembershipLocked(DefaultCollectionName, [normalizedIconName], shouldAdd);
            await SaveStoreLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetMembershipAsync(string collectionName, IEnumerable<string> iconNames, bool isMember)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureLoaded();
            SetMembershipLocked(collectionName, iconNames, isMember);
            await SaveStoreLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> CreateCollectionAsync(string collectionName, IEnumerable<string>? initialIconNames = null)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureLoaded();

            string normalizedCollectionName = NormalizeCollectionName(collectionName);
            if (_collections.ContainsKey(normalizedCollectionName))
            {
                throw new InvalidOperationException($"Collection '{normalizedCollectionName}' already exists.");
            }

            AddCollectionLocked(normalizedCollectionName, initialIconNames);
            await SaveStoreLockedAsync();
            return normalizedCollectionName;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string StorePath =>
        Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, StoreFileName);

    private void AddCollectionLocked(string collectionName, IEnumerable<string>? iconNames)
    {
        string normalizedCollectionName = NormalizeCollectionName(collectionName);
        if (_collections.ContainsKey(normalizedCollectionName))
        {
            throw new InvalidOperationException($"Collection '{normalizedCollectionName}' already exists.");
        }

        HashSet<string> members = new(StringComparer.OrdinalIgnoreCase);
        if (iconNames is not null)
        {
            foreach (string iconName in iconNames)
            {
                members.Add(NormalizeIconName(iconName));
            }
        }

        _collections.Add(normalizedCollectionName, members);
    }

    private void EnsureDefaultCollectionLocked()
    {
        if (!_collections.ContainsKey(DefaultCollectionName))
        {
            _collections.Add(DefaultCollectionName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private void SetMembershipLocked(string collectionName, IEnumerable<string> iconNames, bool isMember)
    {
        string normalizedCollectionName = NormalizeCollectionName(collectionName);
        if (!_collections.TryGetValue(normalizedCollectionName, out HashSet<string>? members))
        {
            throw new InvalidOperationException($"Collection '{normalizedCollectionName}' does not exist.");
        }

        foreach (string iconName in iconNames)
        {
            string normalizedIconName = NormalizeIconName(iconName);
            if (isMember)
            {
                members.Add(normalizedIconName);
            }
            else
            {
                members.Remove(normalizedIconName);
            }
        }
    }

    private async Task SaveStoreLockedAsync()
    {
        IconCollectionsStore store = new()
        {
            Collections = GetCollections().ToList()
        };

        string json = JsonSerializer.Serialize(store, JsonOptions);
        string tempPath = StorePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, StorePath, true);
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("Collections are not loaded. Call LoadAsync() first.");
        }
    }

    private static string NormalizeCollectionName(string collectionName)
    {
        string normalized = collectionName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Collection name cannot be empty.", nameof(collectionName));
        }

        return normalized;
    }

    private static string NormalizeIconName(string iconName)
    {
        string normalized = iconName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Icon name cannot be empty.", nameof(iconName));
        }

        return normalized;
    }
}
