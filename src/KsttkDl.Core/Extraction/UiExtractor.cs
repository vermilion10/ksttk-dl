using KsttkDl.Core.Catalog;
using KsttkDl.Core.MasterData;
using KsttkDl.Core.Naming;

namespace KsttkDl.Core.Extraction;

public sealed record UseItemMetadata
{
    public required string Japanese { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>organizes the static UI assets. most of the interface ships as <c>&lt;name&gt;.json</c> + <c>&lt;name&gt;.png</c> atlas pairs per category; each named frame becomes its own png under <c>ui/&lt;category&gt;/&lt;atlas&gt;/</c>. </summary>
public sealed class UiExtractor(ExtractionContext context, Start2 start2)
{
    public ExtractStats Extract(IReadOnlyList<AssetEntry> catalog)
    {
        return ExtractAtlases(catalog) + ExtractStatic(catalog) + ExtractUseItems(catalog);
    }

    private ExtractStats ExtractAtlases(IReadOnlyList<AssetEntry> catalog)
    {
        var files = 0;
        var frames = 0;

        // pair by path stem: a .json with a same-named sibling .png is an atlas candidate. everything else is a standalone image or a sheet-less data file, copied as-is.
        var byStem = catalog
            .Where(e => e.Group == "atlas")
            .GroupBy(e => StemOf(e.Path), StringComparer.Ordinal);

        foreach (var group in byStem)
        {
            var category = group.First().AssetType;
            var stem = group.Key;
            var name = stem[(stem.LastIndexOf('/') + 1)..];

            // frames may nest one level ("battle_telop/txt_start.png"), so keep any subdirectory the stem carries beyond the category.
            var prefix = $"kcs2/img/{category}/";
            var relative = stem.StartsWith(prefix, StringComparison.Ordinal) ? stem[prefix.Length..] : name;

            var json = group.FirstOrDefault(e => e.Path.EndsWith(".json", StringComparison.Ordinal));
            var png = group.FirstOrDefault(e => e.Path.EndsWith(".png", StringComparison.Ordinal));

            var jsonFile = json is null ? null : context.RawFile(json);
            var pngFile = png is null ? null : context.RawFile(png);

            if (jsonFile is not null && pngFile is not null)
            {
                var atlasFrames = AtlasUnpacker.ReadFrames(jsonFile);
                if (atlasFrames is not null)
                {
                    var destination = context.OutputPath(
                        "ui", SafeName.Sanitize(category), relative.Replace('/', Path.DirectorySeparatorChar));

                    frames += AtlasUnpacker.Unpack(pngFile, atlasFrames, destination, context.Log);
                    continue;
                }
            }

            // not an atlas after all ` mirror whatever files exist.
            foreach (var entry in group)
            {
                var source = context.RawFile(entry);
                if (source is null)
                {
                    continue;
                }

                var destination = context.OutputPath(
                    "ui",
                    SafeName.Sanitize(category),
                    (relative + Path.GetExtension(entry.Path)).Replace('/', Path.DirectorySeparatorChar));

                if (context.Copy(source, destination))
                {
                    files++;
                }
            }
        }

        return new ExtractStats { Files = files, Frames = frames };
    }

    private static string StemOf(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 ? path : path[..dot];
    }

    /// <summary>fixed-path assets (fonts, backgrounds, card frames) keep their original layout under <c>ui/static/</c>: their paths are what identifies them. </summary>
    private ExtractStats ExtractStatic(IReadOnlyList<AssetEntry> catalog)
    {
        var files = 0;

        foreach (var entry in catalog.Where(e => e.Group == "static"))
        {
            var source = context.RawFile(entry);
            if (source is null)
            {
                continue;
            }

            var relative = entry.Path
                .Replace("kcs2/img/", "", StringComparison.Ordinal)
                .Replace("kcs2/resources/", "", StringComparison.Ordinal)
                .Replace("kcs2/", "", StringComparison.Ordinal)
                .Replace('/', Path.DirectorySeparatorChar);

            if (context.Copy(source, context.OutputPath("ui", "static", relative)))
            {
                files++;
            }
        }

        return new ExtractStats { Files = files };
    }

    private ExtractStats ExtractUseItems(IReadOnlyList<AssetEntry> catalog)
    {
        var byId = start2.UseItems.ToDictionary(i => i.Id);
        var metadata = new SortedDictionary<int, UseItemMetadata>();
        var files = 0;

        foreach (var group in catalog.Where(e => e.Group == "useitem").GroupBy(e => e.EntityId))
        {
            var written = new List<string>();

            foreach (var entry in group)
            {
                var source = context.RawFile(entry);
                if (source is null)
                {
                    continue;
                }

                var name = $"{entry.EntityId:D3}_{entry.AssetType}.png";
                if (context.Copy(source, context.OutputPath("ui", "useitem", name)))
                {
                    files++;
                }

                written.Add(name);
            }

            if (written.Count == 0)
            {
                continue;
            }

            metadata[group.Key] = new UseItemMetadata
            {
                Japanese = byId.GetValueOrDefault(group.Key)?.Name ?? "",
                Files = written.Order(StringComparer.Ordinal).ToList(),
            };
        }

        if (metadata.Count > 0)
        {
            context.WriteJson(context.OutputPath("ui", "useitem", "useitems.json"), metadata);
        }

        return new ExtractStats { Files = files, Entities = metadata.Count };
    }
}
