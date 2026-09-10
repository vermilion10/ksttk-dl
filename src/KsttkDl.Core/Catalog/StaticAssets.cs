using System.Text.Json;
using KsttkDl.Core.MasterData;

namespace KsttkDl.Core.Catalog;

/// <summary> fixed-path static asset lists, from KCCacheProxy's preloader data. refreshed alongside the master data rather than hardcoded, so they track the game's ui as it changes </summary>
public static class StaticAssets
{
    public static IReadOnlyList<string> FixedUrls { get; private set; } = [];

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AtlasCategories { get; private set; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public static void Load(DataStore store)
    {
        FixedUrls = ReadArray(store.PathFor(DataStore.StaticUrlsFile));
        AtlasCategories = ReadCategories(store.PathFor(DataStore.StaticAssetsFile));
    }

    private static List<string> ReadArray(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        return document.RootElement.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadCategories(string path)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            result[property.Name] = property.Value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
        }

        return result;
    }
}
