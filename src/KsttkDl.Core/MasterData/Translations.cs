using System.Text.Json;

namespace KsttkDl.Core.MasterData;

public sealed record WorldServer(int Num, string Host, string Name, string JapaneseName, string Ip);

/// <summary> eng names from KC3Kai/kc3-translations. api_start2 carries jp names only, so these produce the eng folder names; the jp original is always kept alongside in the sidecars, because a translation can be missing. </summary>
public sealed class Translations
{
    public required IReadOnlyDictionary<string, string> Ships { get; init; }
    public required IReadOnlyDictionary<string, string> Items { get; init; }
    public required IReadOnlyDictionary<string, string> ShipPrefixes { get; init; }
    public required IReadOnlyDictionary<string, string> ShipSuffixes { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Quotes { get; init; }
    public required IReadOnlyList<WorldServer> Servers { get; init; }

    public static Translations Load(DataStore store) => new()
    {
        Ships = LoadFlat(store.PathFor(DataStore.ShipsFile)),
        Items = LoadFlat(store.PathFor(DataStore.ItemsFile)),
        ShipPrefixes = LoadAffix(store.PathFor(DataStore.ShipAffixFile), "prefixes"),
        ShipSuffixes = LoadAffix(store.PathFor(DataStore.ShipAffixFile), "suffixes"),
        Quotes = LoadQuotes(store.PathFor(DataStore.QuotesFile)),
        Servers = LoadServers(store.PathFor(DataStore.ServersFile)),
    };

    /// <summary> Voice lines for a ship, keyed by friendly label. Note quotes.json is keyed by ship id, not by the ship's voice folder name.  </summary>
    public IReadOnlyDictionary<string, string> QuotesFor(int shipId) =>
        Quotes.TryGetValue(shipId.ToString(), out var q) ? q : new Dictionary<string, string>();

    private static Dictionary<string, string> LoadFlat(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var result = new Dictionary<string, string>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                result[property.Name] = property.Value.GetString()!;
            }
        }

        return result;
    }

    private static Dictionary<string, string> LoadAffix(string path, string section)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var result = new Dictionary<string, string>();
        if (!document.RootElement.TryGetProperty(section, out var element))
        {
            return result;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                result[property.Name] = property.Value.GetString()!;
            }
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadQuotes(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var ship in document.RootElement.EnumerateObject())
        {
            if (ship.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var lines = new Dictionary<string, string>();
            foreach (var line in ship.Value.EnumerateObject())
            {
                var text = FlattenQuote(line.Value);
                if (text is not null)
                {
                    lines[line.Name] = text;
                }
            }

            result[ship.Name] = lines;
        }

        return result;
    }

    /// <summary> A quote is a plain string, or - when the line has subtitle timing splits ` an object keyed by millisecond offsets ("0", "1990", "3850,7292"). Those are one spoken line broken into display chunks, so they are rejoined in offset order. </summary>
    private static string? FlattenQuote(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var segments = new List<(long Offset, string Text)>();
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var head = property.Name.Split(',')[0];
            var offset = long.TryParse(head, out var parsed) ? parsed : long.MaxValue;
            segments.Add((offset, property.Value.GetString()!));
        }

        var joined = string.Join(" ", segments.OrderBy(s => s.Offset).Select(s => s.Text.Trim()))
            .Trim();

        return joined.Length == 0 ? null : joined;
    }

    private static List<WorldServer> LoadServers(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var result = new List<WorldServer>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            result.Add(new WorldServer(
                value.TryGetProperty("num", out var num) ? num.GetInt32() : 0,
                property.Name,
                value.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                value.TryGetProperty("jp", out var jp) ? jp.GetString() ?? "" : "",
                value.TryGetProperty("ip", out var ip) ? ip.GetString() ?? "" : ""));
        }

        return result.OrderBy(s => s.Num).ToList();
    }
}
