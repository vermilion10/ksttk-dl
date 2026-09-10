using KsttkDl.Core.Catalog;
using KsttkDl.Core.MasterData;
using KsttkDl.Core.Naming;

namespace KsttkDl.Core.Extraction;

/// <summary>t he sidecar shape shared by ships, equipment and furniture. The jp name and numeric id sit alongside the eng name so nothing is lost when a translation is missing or wrong. </summary>
public sealed record EntityMetadata
{
    public required string Japanese { get; init; }
    public required string English { get; init; }
    public string? Yomi { get; init; }
    public required string Folder { get; init; }

    /// <summary>false when <see cref="English"/> is a fallback to the jp name.</summary>
    public required bool Translated { get; init; }

    /// <summary>set only when something about this entity's naming needs explaining.</summary>
    public string? Note { get; init; }

    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>organizes the per-entity categories ` ships, equipment, furniture ` into English-named folders with one sidecar per category. </summary>
public sealed class EntityExtractor(ExtractionContext context, Start2 start2, NameResolver names)
{
    public ExtractStats ExtractShips(IReadOnlyList<AssetEntry> catalog)
    {
        var metadata = new SortedDictionary<int, EntityMetadata>();
        var files = 0;

        foreach (var group in catalog.Where(e => e.Group == "ship").GroupBy(e => e.EntityId))
        {
            if (!start2.Ships.TryGetValue(group.Key, out var ship))
            {
                continue;
            }

            var resolved = names.Ship(ship);
            var folder = SafeName.EntityFolder(group.Key, resolved.English);
            var written = new List<string>();

            foreach (var entry in group)
            {
                var source = context.RawFile(entry);
                if (source is null)
                {
                    continue;
                }

                // "sp_remodel/full_x2" is one asset type, not a subdirectory.
                var name = SafeName.Sanitize(entry.AssetType.Replace('/', '_'), "asset");
                var extension = Path.GetExtension(entry.Path);
                var destination = context.OutputPath("ships", folder, name + extension);

                if (context.Copy(source, destination))
                {
                    files++;
                }

                written.Add(name + extension);
            }

            if (written.Count == 0)
            {
                continue;
            }

            metadata[group.Key] = new EntityMetadata
            {
                Japanese = resolved.Japanese,
                English = resolved.English,
                Yomi = resolved.Yomi.Length == 0 ? null : resolved.Yomi,
                Folder = folder,
                Translated = resolved.Translated,
                Files = written.Order(StringComparer.Ordinal).ToList(),
            };
        }

        var seasonal = ExtractSeasonalShips(catalog, metadata);

        context.WriteJson(context.OutputPath("ships", "ships.json"), metadata);
        return new ExtractStats { Files = files + seasonal, Entities = metadata.Count };
    }

    /// <summary>seasonal cgs have their own id space (5001+) and no api_mst_ship row, so there is no name to translate ` not even a jp one. they keep the id and the CG's asset key as the folder name, under a "seasonal" subtree, and the sidecar says why </summary>
    private int ExtractSeasonalShips(
        IReadOnlyList<AssetEntry> catalog,
        SortedDictionary<int, EntityMetadata> metadata)
    {
        var graphs = start2.ShipGraphs.ToDictionary(g => g.Id);
        var files = 0;

        foreach (var group in catalog.Where(e => e.Group == "ship-seasonal").GroupBy(e => e.EntityId))
        {
            var key = graphs.GetValueOrDefault(group.Key)?.FileName ?? "";
            var folder = Path.Combine("seasonal", SafeName.EntityFolder(group.Key, key));
            var written = new List<string>();

            foreach (var entry in group)
            {
                var source = context.RawFile(entry);
                if (source is null)
                {
                    continue;
                }

                var name = SafeName.Sanitize(entry.AssetType, "asset") + ".png";
                if (context.Copy(source, context.OutputPath("ships", folder, name)))
                {
                    files++;
                }

                written.Add(name);
            }

            if (written.Count == 0)
            {
                continue;
            }

            metadata[group.Key] = new EntityMetadata
            {
                Japanese = "",
                English = "",
                Folder = folder.Replace(Path.DirectorySeparatorChar, '/'),
                Translated = false,
                Note = "Seasonal CG; no api_mst_ship entry, so no name is available in master data.",
                Files = written.Order(StringComparer.Ordinal).ToList(),
            };
        }

        return files;
    }

    public ExtractStats ExtractEquipment(IReadOnlyList<AssetEntry> catalog)
    {
        var byId = start2.SlotItems.ToDictionary(i => i.Id);
        var metadata = new SortedDictionary<int, EntityMetadata>();
        var files = 0;

        foreach (var group in catalog.Where(e => e.Group == "equipment").GroupBy(e => e.EntityId))
        {
            if (!byId.TryGetValue(group.Key, out var item))
            {
                continue;
            }

            var resolved = names.Item(item);
            var folder = SafeName.EntityFolder(group.Key, resolved.English);
            var written = new List<string>();

            foreach (var entry in group)
            {
                var source = context.RawFile(entry);
                if (source is null)
                {
                    continue;
                }

                var name = SafeName.Sanitize(entry.AssetType, "asset") + ".png";
                if (context.Copy(source, context.OutputPath("equipment", folder, name)))
                {
                    files++;
                }

                written.Add(name);
            }

            if (written.Count == 0)
            {
                continue;
            }

            metadata[group.Key] = new EntityMetadata
            {
                Japanese = resolved.Japanese,
                English = resolved.English,
                Folder = folder,
                Translated = resolved.Translated,
                Files = written.Order(StringComparer.Ordinal).ToList(),
            };
        }

        context.WriteJson(context.OutputPath("equipment", "equipment.json"), metadata);
        return new ExtractStats { Files = files, Entities = metadata.Count };
    }

    public ExtractStats ExtractFurniture(IReadOnlyList<AssetEntry> catalog)
    {
        var byId = start2.Furniture.ToDictionary(f => f.Id);
        var metadata = new SortedDictionary<int, EntityMetadata>();
        var files = 0;

        foreach (var entry in catalog.Where(e => e.Group == "furniture-outside"))
        {
            var source = context.RawFile(entry);
            if (source is null)
            {
                continue;
            }

            var name = Path.GetFileName(entry.Path);
            if (context.Copy(source, context.OutputPath("furniture", "outside", name)))
            {
                files++;
            }
        }

        var byEntity = catalog
            .Where(e => e.Group is "furniture" or "furniture-picture")
            .GroupBy(e => e.EntityId);

        foreach (var group in byEntity)
        {
            if (!byId.TryGetValue(group.Key, out var furniture))
            {
                continue;
            }

            // no eng translation source, so the jp title is the folder name.
            var resolved = names.Furniture(furniture);
            var folder = SafeName.EntityFolder(group.Key, resolved.English);
            var written = new List<string>();

            foreach (var entry in group)
            {
                var source = context.RawFile(entry);
                if (source is null)
                {
                    continue;
                }

                // popup pictures are keyed by their own id, not the owner's.
                var stem = entry.Group == "furniture-picture"
                    ? $"picture_{entry.SecondaryId}"
                    : SafeName.Sanitize(entry.AssetType.Replace("_json", ""), "asset");

                var name = stem + Path.GetExtension(entry.Path);
                if (context.Copy(source, context.OutputPath("furniture", folder, name)))
                {
                    files++;
                }

                written.Add(name);
            }

            if (written.Count == 0)
            {
                continue;
            }

            metadata[group.Key] = new EntityMetadata
            {
                Japanese = resolved.Japanese,
                English = resolved.English,
                Folder = folder,
                Translated = resolved.Translated,
                Files = written.Order(StringComparer.Ordinal).ToList(),
            };
        }

        context.WriteJson(context.OutputPath("furniture", "furniture.json"), metadata);
        return new ExtractStats { Files = files, Entities = metadata.Count };
    }
}
