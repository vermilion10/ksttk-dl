using System.Text.Json;

namespace KsttkDl.Core.Catalog;

/// <summary> a second pass over assets only discoverable by reading something already downloaded: map gauge images named inside the gauge json, and furniture popup pictures referenced from an animated furniture's script </summary>
public static class DerivedCatalog
{
    /// <summary> the world's three name plates, keyed by its IP with each octet zero-padded to three </summary>
    public static IEnumerable<AssetEntry> ServerNameAssets(string serverIp)
    {
        if (serverIp.Length == 0)
        {
            yield break;
        }

        var stem = string.Join('_', serverIp.Split('.').Select(part => part.PadLeft(3, '0')));

        foreach (var size in new[] { "t", "s", "l" })
        {
            yield return new AssetEntry
            {
                Path = $"kcs2/resources/world/{stem}_{size}.png",
                Category = AssetCategory.Ui,
                Group = "static",
                AssetType = $"world_name_{size}",
            };
        }
    }

    /// <summary> Gauge sprites named by the "img" fields in each gauge JSON; each yields a normal and a "_light" variant. </summary>
    public static IReadOnlyList<AssetEntry> GaugeImages(string rawRoot, IEnumerable<AssetEntry> gaugeEntries)
    {
        var entries = new List<AssetEntry>();

        foreach (var gauge in gaugeEntries)
        {
            var file = Path.Combine(rawRoot, gauge.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                continue;
            }

            foreach (var name in ReadGaugeImageNames(file))
            {
                foreach (var suffix in new[] { "", "_light" })
                {
                    entries.Add(new AssetEntry
                    {
                        Path = $"kcs2/resources/gauge/{name}{suffix}.png",
                        VersionQuery = gauge.VersionQuery,
                        Category = AssetCategory.Maps,
                        Group = "gauge-image",
                        EntityId = gauge.EntityId,
                        SecondaryId = gauge.SecondaryId,
                        AssetType = $"gauge{suffix}",
                    });
                }
            }
        }

        return Deduplicate(entries);
    }

    private static IEnumerable<string> ReadGaugeImageNames(string file)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(file));
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            if (root.TryGetProperty("img", out var img) && img.ValueKind == JsonValueKind.String)
            {
                yield return img.GetString()!;
            }

            if (root.TryGetProperty("vertical", out var vertical)
                && vertical.ValueKind == JsonValueKind.Object
                && vertical.TryGetProperty("img", out var verticalImg)
                && verticalImg.ValueKind == JsonValueKind.String)
            {
                yield return verticalImg.GetString()!;
            }
        }
    }

    /// <summary> Popup pictures from an animated furniture's script - a state machine, where the pictures hang off the action the "standard" state's hit area triggers. </summary>
    public static IReadOnlyList<AssetEntry> FurniturePictures(string rawRoot, IEnumerable<AssetEntry> scriptEntries)
    {
        var entries = new List<AssetEntry>();

        foreach (var script in scriptEntries)
        {
            var file = Path.Combine(rawRoot, script.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                continue;
            }

            foreach (var pictureId in ReadFurniturePictureIds(file))
            {
                entries.Add(new AssetEntry
                {
                    Path = Naming.AssetPath.GetPath(pictureId, "furniture", "picture", "png"),
                    VersionQuery = script.VersionQuery,
                    Category = AssetCategory.Furniture,
                    Group = "furniture-picture",
                    EntityId = script.EntityId,
                    SecondaryId = pictureId,
                    AssetType = "picture",
                });
            }
        }

        return Deduplicate(entries);
    }

    private static List<int> ReadFurniturePictureIds(string file)
    {
        var ids = new List<int>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(file).Trim());
        }
        catch (JsonException)
        {
            return ids;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("standard", out var standard)
                || standard.ValueKind != JsonValueKind.Object
                || !standard.TryGetProperty("hitarea", out var hitArea)
                || hitArea.ValueKind != JsonValueKind.Object
                || !hitArea.TryGetProperty("state", out var state)
                || state.ValueKind != JsonValueKind.String)
            {
                return ids;
            }

            var action = state.GetString()!;
            if (!root.TryGetProperty(action, out var actionNode)
                || actionNode.ValueKind != JsonValueKind.Object
                || !actionNode.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }

            foreach (var frame in data.EnumerateArray())
            {
                if (frame.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var step in frame.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object
                        || !step.TryGetProperty("popup", out var popup)
                        || popup.ValueKind != JsonValueKind.Object
                        || !popup.TryGetProperty("src", out var src))
                    {
                        continue;
                    }

                    // Sometimes a number, sometimes a numeric string; preload.js coerces with a unary plus.
                    var id = src.ValueKind switch
                    {
                        JsonValueKind.Number => src.GetInt32(),
                        JsonValueKind.String when int.TryParse(src.GetString(), out var parsed) => parsed,
                        _ => 0,
                    };

                    if (id > 0)
                    {
                        ids.Add(id);
                    }
                }
            }
        }

        return ids;
    }

    private static List<AssetEntry> Deduplicate(List<AssetEntry> entries) => entries
        .GroupBy(e => e.Path, StringComparer.Ordinal)
        .Select(g => g.First())
        .ToList();
}
