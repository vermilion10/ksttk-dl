using KsttkDl.Core.Catalog;
using KsttkDl.Core.MasterData;
using KsttkDl.Core.Naming;

namespace KsttkDl.Core.Extraction;

/// <summary>one sidecar voice line: friendly filename to the CDN's opaque numeric one, plus the text. </summary>
public sealed record VoiceMetadata
{
    public required string OriginalFilename { get; init; }
    public required int VoiceLine { get; init; }
    public string? QuoteText { get; init; }
}

public sealed record BgmMetadata
{
    public required string Japanese { get; init; }
    public required string Kind { get; init; }
    public required string File { get; init; }
}

/// <summary>organizes audio: ship voice lines into per-ship folders with labelled filenames, plus bgm, sound effects, title calls and the npc/event/abyssal banks. nothing is transcoded ` the files are already mp3. </summary>
public sealed class AudioExtractor(
    ExtractionContext context,
    Start2 start2,
    Translations translations,
    NameResolver names)
{
    public ExtractStats Extract(IReadOnlyList<AssetEntry> catalog)
    {
        return ExtractShipVoices(catalog)
            + ExtractBgm(catalog)
            + ExtractSimple(catalog, "se", ["se"])
            + ExtractTitleCalls(catalog)
            + ExtractNpcVoices(catalog);
    }

    private ExtractStats ExtractShipVoices(IReadOnlyList<AssetEntry> catalog)
    {
        var files = 0;
        var folders = 0;

        foreach (var group in catalog.Where(e => e.Group == "voice").GroupBy(e => e.EntityId))
        {
            if (!start2.Ships.TryGetValue(group.Key, out var ship))
            {
                continue;
            }

            var resolved = names.Ship(ship);
            var folder = SafeName.EntityFolder(group.Key, resolved.English);
            var quotes = translations.QuotesFor(group.Key);
            var sidecar = new SortedDictionary<string, VoiceMetadata>(StringComparer.Ordinal);

            foreach (var entry in group.OrderBy(e => e.SecondaryId))
            {
                var source = context.RawFile(entry);
                if (source is null)
                {
                    continue;
                }

                var name = SafeName.Sanitize(entry.AssetType, $"Voice_{entry.SecondaryId}") + ".mp3";
                if (context.Copy(source, context.OutputPath("audio", "voice", folder, name)))
                {
                    files++;
                }

                sidecar[name] = new VoiceMetadata
                {
                    OriginalFilename = Path.GetFileName(entry.Path),
                    VoiceLine = entry.SecondaryId,
                    QuoteText = quotes.GetValueOrDefault(entry.AssetType),
                };
            }

            if (sidecar.Count == 0)
            {
                continue;
            }

            folders++;
            context.WriteJson(context.OutputPath("audio", "voice", folder, "voice.json"), sidecar);
        }

        return new ExtractStats { Files = files, Entities = folders };
    }

    private ExtractStats ExtractBgm(IReadOnlyList<AssetEntry> catalog)
    {
        var bgmNames = start2.Bgms.ToDictionary(b => b.Id, b => b.Name);
        var sidecar = new SortedDictionary<string, BgmMetadata>(StringComparer.Ordinal);
        var files = 0;

        foreach (var entry in catalog.Where(e => e.Group == "bgm"))
        {
            var source = context.RawFile(entry);
            if (source is null)
            {
                continue;
            }

            // only port bgm is named in master data; battle and fanfare fall back to id
            var japanese = bgmNames.GetValueOrDefault(entry.EntityId, "");
            var stem = japanese.Length > 0
                ? $"{entry.EntityId:D3}_{SafeName.Sanitize(japanese, entry.EntityId.ToString())}"
                : $"{entry.EntityId:D3}";

            var relative = Path.Combine(entry.AssetType, stem + ".mp3");
            if (context.Copy(source, context.OutputPath("audio", "bgm", relative)))
            {
                files++;
            }

            sidecar[$"{entry.AssetType}/{entry.EntityId}"] = new BgmMetadata
            {
                Japanese = japanese,
                Kind = entry.AssetType,
                File = relative.Replace(Path.DirectorySeparatorChar, '/'),
            };
        }

        if (sidecar.Count > 0)
        {
            context.WriteJson(context.OutputPath("audio", "bgm", "bgm.json"), sidecar);
        }

        return new ExtractStats { Files = files };
    }

    private ExtractStats ExtractSimple(IReadOnlyList<AssetEntry> catalog, string folder, string[] groups)
    {
        var files = 0;

        foreach (var entry in catalog.Where(e => groups.Contains(e.Group)))
        {
            var source = context.RawFile(entry);
            if (source is null)
            {
                continue;
            }

            if (context.Copy(source, context.OutputPath("audio", folder, Path.GetFileName(entry.Path))))
            {
                files++;
            }
        }

        return new ExtractStats { Files = files };
    }

    private ExtractStats ExtractTitleCalls(IReadOnlyList<AssetEntry> catalog)
    {
        var files = 0;

        foreach (var entry in catalog.Where(e => e.Group == "titlecall"))
        {
            var source = context.RawFile(entry);
            if (source is null)
            {
                continue;
            }

            var destination = context.OutputPath(
                "audio", "titlecall", $"set_{entry.SecondaryId}", Path.GetFileName(entry.Path));

            if (context.Copy(source, destination))
            {
                files++;
            }
        }

        return new ExtractStats { Files = files };
    }

    /// <summary>npc, event and abyssal lines have no entity to hang a name off, so they keep their numeric filenames and the sidecar carries the text. </summary>
    private ExtractStats ExtractNpcVoices(IReadOnlyList<AssetEntry> catalog)
    {
        var files = 0;

        foreach (var section in new[] { "npc", "event", "abyssal" })
        {
            var entries = catalog.Where(e => e.Group == section).ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            translations.Quotes.TryGetValue(section, out var quotes);
            var sidecar = new SortedDictionary<string, VoiceMetadata>(StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                var source = context.RawFile(entry);
                if (source is null)
                {
                    continue;
                }

                var name = Path.GetFileName(entry.Path);
                if (context.Copy(source, context.OutputPath("audio", "voice", section, name)))
                {
                    files++;
                }

                sidecar[name] = new VoiceMetadata
                {
                    OriginalFilename = name,
                    VoiceLine = entry.EntityId,
                    QuoteText = quotes?.GetValueOrDefault(entry.AssetType),
                };
            }

            if (sidecar.Count > 0)
            {
                context.WriteJson(context.OutputPath("audio", "voice", section, "voice.json"), sidecar);
            }
        }

        return new ExtractStats { Files = files };
    }
}
