using System.Text.Json;

namespace KsttkDl.Core.Catalog;

/// <summary> <c>kcs2/version.json</c>, the per-world asset version manifest. static UI assets are versioned by category ("common": "6.3.2.1"); some worlds also publish a nested <c>resources.map</c> of per-map versions and some do not, so both shapes are tolerated </summary>
public sealed class VersionInfo
{
    private readonly Dictionary<string, string> _categories = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _maps = [];

    public static VersionInfo Empty { get; } = new();

    public static VersionInfo Parse(string json)
    {
        var result = new VersionInfo();
        using var document = JsonDocument.Parse(json);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                result._categories[property.Name] = property.Value.GetString()!;
                continue;
            }

            if (property.Name != "resources" || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!property.Value.TryGetProperty("map", out var maps) || maps.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var map in maps.EnumerateObject())
            {
                if (int.TryParse(map.Name, out var key) && map.Value.ValueKind == JsonValueKind.String)
                {
                    result._maps[key] = map.Value.GetString()!;
                }
            }
        }

        return result;
    }

    public IReadOnlyCollection<string> Categories => _categories.Keys;

    public string CategoryQuery(string category) =>
        _categories.TryGetValue(category, out var version) ? $"?version={version}" : "";

    /// <summary>map versions are keyed by <c>area * 10 + no</c> </summary>
    public string MapQuery(int mapAreaId, int no) =>
        _maps.TryGetValue(mapAreaId * 10 + no, out var version) ? $"?version={version}" : "";
}
