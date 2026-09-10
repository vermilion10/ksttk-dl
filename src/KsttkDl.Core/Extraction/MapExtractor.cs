using KsttkDl.Core.Catalog;
using KsttkDl.Core.MasterData;

namespace KsttkDl.Core.Extraction;

public sealed record MapMetadata
{
    public required int Area { get; init; }
    public required int No { get; init; }
    public required string Japanese { get; init; }
    public required string AreaJapanese { get; init; }
    public required string Folder { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>o rganizes maps into <c>maps/&lt;area&gt;/&lt;no&gt;/</c>. Map names have no eng translation source, so folders stay numeric and the sidecar carries the jp names. </summary>
public sealed class MapExtractor(ExtractionContext context, Start2 start2)
{
    public ExtractStats Extract(IReadOnlyList<AssetEntry> catalog)
    {
        var stats = ExtractMaps(catalog);

        // gauge sprites are shared across maps, so they sit alongside the per-map folders
        foreach (var entry in catalog.Where(e => e.Group is "gauge-image"))
        {
            var source = context.RawFile(entry);
            if (source is null)
            {
                continue;
            }

            if (context.Copy(source, context.OutputPath("maps", "gauge", Path.GetFileName(entry.Path))))
            {
                stats += new ExtractStats { Files = 1 };
            }
        }

        return stats;
    }

    private ExtractStats ExtractMaps(IReadOnlyList<AssetEntry> catalog)
    {
        var infos = start2.MapInfos.ToDictionary(m => (m.MapAreaId, m.No));
        var metadata = new List<MapMetadata>();
        var files = 0;
        var frames = 0;

        var groups = catalog
            .Where(e => e.Group is "map" or "gauge")
            .GroupBy(e => (Area: e.EntityId, No: e.SecondaryId));

        foreach (var group in groups)
        {
            var folder = Path.Combine($"{group.Key.Area:D3}", $"{group.Key.No:D2}");
            var written = new List<string>();

            // locate the atlas pair before copying, so the sheet is unpacked as well as mirrored.
            string? atlasJson = null;
            string? atlasSheet = null;

            foreach (var entry in group)
            {
                var source = context.RawFile(entry);
                if (source is null)
                {
                    continue;
                }

                var name = entry.AssetType switch
                {
                    "background" => "background.png",
                    "info" => "info.json",
                    "image_atlas" => "image.json",
                    "image_sheet" => "image.png",
                    "gauge" => "gauge.json",
                    _ => Path.GetFileName(entry.Path),
                };

                if (entry.AssetType == "image_atlas")
                {
                    atlasJson = source;
                }
                else if (entry.AssetType == "image_sheet")
                {
                    atlasSheet = source;
                }

                if (context.Copy(source, context.OutputPath("maps", folder, name)))
                {
                    files++;
                }

                written.Add(name);
            }

            if (written.Count == 0)
            {
                continue;
            }

            if (atlasJson is not null && atlasSheet is not null)
            {
                var atlasFrames = AtlasUnpacker.ReadFrames(atlasJson);
                if (atlasFrames is not null)
                {
                    var destination = context.OutputPath("maps", folder, "image");
                    frames += AtlasUnpacker.Unpack(atlasSheet, atlasFrames, destination, context.Log);
                }
            }

            var info = infos.GetValueOrDefault((group.Key.Area, group.Key.No));
            metadata.Add(new MapMetadata
            {
                Area = group.Key.Area,
                No = group.Key.No,
                Japanese = info?.Name ?? "",
                AreaJapanese = start2.MapAreas.GetValueOrDefault(group.Key.Area)?.Name ?? "",
                Folder = folder.Replace(Path.DirectorySeparatorChar, '/'),
                Files = written.Order(StringComparer.Ordinal).ToList(),
            });
        }

        context.WriteJson(
            context.OutputPath("maps", "maps.json"),
            metadata.OrderBy(m => m.Area).ThenBy(m => m.No).ToList());

        return new ExtractStats { Files = files, Frames = frames, Entities = metadata.Count };
    }
}
