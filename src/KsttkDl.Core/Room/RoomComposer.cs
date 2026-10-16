using System.Text.Json;
using KsttkDl.Core.Catalog;
using KsttkDl.Core.Fetch;
using KsttkDl.Core.MasterData;
using KsttkDl.Core.Naming;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace KsttkDl.Core.Room;

/// <summary>the six furniture slots of the port room, in api_type order.</summary>
public enum FurnitureSlot
{
    Floor = 0,
    Wall = 1,
    Window = 2,
    Object = 3,
    Chest = 4,
    Desk = 5,
}

/// <summary>what the window shows outside: the game picks it from the hour.</summary>
public enum TimeOfDay
{
    Day = 1,
    Evening = 2,
    Dusk = 3,
    Night = 4,
    Dawn = 5,
}

/// <summary>
/// one picture to draw: a file, the part of it to use (null for the whole file, otherwise an atlas frame),
/// and where its top-left corner lands on the 1200×720 room. <see cref="Scale"/> sizes the picture, as for the
/// double-size special-remodel art drawn at half size
/// </summary>
public sealed record RoomSprite(string Path, SpriteRect? Source, int X, int Y)
{
    public double Scale { get; init; } = 1;

    public double Opacity { get; init; } = 1;
}

public readonly record struct SpriteRect(int X, int Y, int Width, int Height);

/// <summary>
/// one picture a secretary can be drawn with. <paramref name="Key"/> is "full", "full_dmg", "sp" (the
/// special-remodel art) or "history:{type}:{version}" for a copy kept when a newer CG replaced it
/// </summary>
public sealed record SecretaryArt(string Key, string Label, string Note);

/// <summary>
/// lays out the port room the way the game client does. each slot has a fixed anchor (floor and desk sit on
/// the bottom edge, the chest on the right edge, the window 297 px in, the rest at the origin), the view
/// outside sits 294 px in behind the window, and the secretary's full CG is placed from the shipgraph's port
/// offset. animated furniture is drawn as the first frame of each sprite group in its "standard" state, with
/// the group's own offset, so a sand floor gets its waves and a clock its pendulum
/// </summary>
public sealed class RoomComposer(GameLibrary library)
{
    public const int Width = 1200;
    public const int Height = 720;

    private const int WindowX = 297;
    private const int OutsideX = 294;
    private const int SecretaryX = 491;
    private const int SecretaryY = -88;

    // ships the client moves less when the flagship stands to the left
    private static readonly HashSet<int> ShortMoveShips = [49, 253];

    /// <summary>the files a piece of furniture needs before it can be drawn.</summary>
    public IReadOnlyList<AssetEntry> FurnitureEntries(int furnitureId) =>
        library.EntriesFor("furniture", furnitureId)
            .Where(e => e.AssetType is "normal" or "movable" or "movable_json" or "scripts_json")
            .ToList();

    public AssetEntry? OutsideEntry(int outsideId, TimeOfDay time) =>
        library.EntriesFor("furniture-outside", outsideId).FirstOrDefault(e => e.SecondaryId == (int)time);

    public const string CurrentArt = "full";
    public const string DamagedArt = "full_dmg";
    public const string SpecialArt = "sp";
    public const string OriginalArt = "original";
    public const string OriginalDamagedArt = "original_dmg";
    private const string SpecialType = "sp_remodel/full_x2";

    // where a full CG's art typically stands in the room (its centre and top), and how much larger it is than
    // the card-sized character_full art: medians over 290 ships. pictures without offsets of their own are placed
    // by these
    private const double CardArtScale = 1.91;
    private const int CardArtCentreX = 910;
    private const int CardArtTop = 107;

    /// <summary>
    /// the pictures a ship can stand in the room with: its current CG (seasonal art while a season runs, since
    /// the game swaps the file in place), the damaged one, the special-remodel art, which keeps the original
    /// art at twice the size, and any earlier CGs kept in the history folder
    /// </summary>
    /// <param name="hasSpecialArt">the server has confirmed the ship's special-remodel art.</param>
    public IReadOnlyList<SecretaryArt> SecretaryArts(int shipId, bool hasSpecialArt = false)
    {
        // a past seasonal CG from the archive: only its card-sized pictures are kept
        if (IsSeasonalArchive(shipId))
        {
            const string archived = "A past seasonal CG, enlarged from the card-sized picture the server keeps once the "
                                    + "season ends. Its size and place are estimates; adjust them with Size and a drag";
            return
            [
                new(OriginalArt, "Seasonal CG (enlarged)", archived),
                new(OriginalDamagedArt, "Seasonal CG, damaged (enlarged)", archived),
            ];
        }

        // during a season the server only has the seasonal full CG, while the card-sized art keeps the original
        const string current = "The full CG the port shows now: seasonal art while a season runs";
        const string original = "The original art, enlarged from the card-sized picture (character full in the ship "
                                + "menu). Its size and place are estimates; adjust them with Size and a drag";
        var arts = new List<SecretaryArt>
        {
            new(CurrentArt, "Current CG", current),
            new(DamagedArt, "Current CG, damaged (中破)", current),
            new(OriginalArt, "Original CG (enlarged)", original),
            new(OriginalDamagedArt, "Original CG, damaged (enlarged)", original),
        };

        // offered once the server has confirmed it; only ships with a special remodel have one
        if (hasSpecialArt || library.LocalPath(SpecialEntry(shipId)) is not null)
        {
            arts.Add(new(SpecialArt, "Special-remodel art",
                "The ship's own art from the special-remodel animation, drawn at half size"));
        }

        foreach (var (type, version, _) in HistoryFiles(shipId))
        {
            arts.Add(new($"history:{type}:{version}",
                type == DamagedArt ? $"Earlier CG, damaged (v{version})" : $"Earlier CG (v{version})",
                "A copy kept when a newer CG replaced it"));
        }

        return arts;
    }

    /// <summary>the file a secretary picture needs; null for history copies, which are already on disk.</summary>
    public AssetEntry? SecretaryEntry(int shipId, string art) => art switch
    {
        SpecialArt => SpecialEntry(shipId),
        OriginalArt => CardArtEntry(shipId, false),
        OriginalDamagedArt => CardArtEntry(shipId, true) ?? CardArtEntry(shipId, false),
        DamagedArt => library.Find("ship", shipId, "full_dmg") ?? library.Find("ship", shipId, "full"),
        _ when art.StartsWith("history:", StringComparison.Ordinal) => null,
        _ => library.Find("ship", shipId, "full"),
    };

    /// <summary>the card-sized art; it keeps the original look while a seasonal CG replaces the full one.</summary>
    public AssetEntry? CardArtEntry(int shipId, bool damaged) =>
        library.Find("ship", shipId, damaged ? "character_full_dmg" : "character_full")
        ?? library.Find("ship-seasonal", shipId, damaged ? "character_full_dmg" : "character_full");

    /// <summary>graph ids 5001 and up: past seasonal CGs, with no ship of their own.</summary>
    public bool IsSeasonalArchive(int shipId) => library.Find("ship-seasonal", shipId, "character_full") is not null;

    /// <summary>the special-remodel art, which only ships with a special remodel have.</summary>
    public AssetEntry SpecialEntry(int shipId) =>
        library.Find("ship", shipId, SpecialType) ?? new AssetEntry
        {
            Path = AssetPath.GetPath(shipId, "ship", SpecialType, "png"),
            Category = AssetCategory.Ships,
            Group = "ship",
            EntityId = shipId,
            AssetType = SpecialType,
        };

    private static Rectangle AlphaBounds(Image<Rgba32> image)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].A <= 40)
                    {
                        continue;
                    }

                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        });
        return right < 0 ? Rectangle.Empty : new Rectangle(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>kept copies of a ship's CGs: (full or full_dmg, version, path), newest first.</summary>
    private IEnumerable<(string Type, string Version, string Path)> HistoryFiles(int shipId)
    {
        foreach (var type in new[] { CurrentArt, DamagedArt })
        {
            var folder = Path.Combine(library.Workspace.HistoryRoot, "kcs2", "resources", "ship", type);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            // name.v51.png: the version is the inner extension
            var found = Directory.EnumerateFiles(folder, $"{shipId:D4}_*.v*.png")
                .Select(path => (Type: type, Version: Path.GetExtension(Path.GetFileNameWithoutExtension(path)).TrimStart('.', 'v'), Path: path))
                .OrderByDescending(f => int.TryParse(f.Version, out var v) ? v : 0);
            foreach (var file in found)
            {
                yield return file;
            }
        }
    }

    public static TimeOfDay TimeAt(int hour) => hour switch
    {
        >= 20 or < 4 => TimeOfDay.Night,
        < 8 => TimeOfDay.Dawn,
        < 16 => TimeOfDay.Day,
        < 18 => TimeOfDay.Evening,
        _ => TimeOfDay.Dusk,
    };

    /// <summary>
    /// the sprites of one piece of furniture at its slot's anchor; empty while its files are missing.
    /// <paramref name="secretaryId"/> matters to furniture that changes with the secretary, like the umbrella stand
    /// </summary>
    public IReadOnlyList<RoomSprite> Furniture(int furnitureId, int secretaryId = 0)
    {
        var furniture = library.Start2.Furniture.FirstOrDefault(f => f.Id == furnitureId);
        if (furniture is null)
        {
            return [];
        }

        var slot = (FurnitureSlot)furniture.Type;
        var normal = library.Find("furniture", furnitureId, "normal") is { } entry ? library.LocalPath(entry) : null;
        if (normal is not null)
        {
            var (w, h) = PngSize(normal);
            var (x, y) = Anchor(slot, w, h);
            return [new RoomSprite(normal, null, x, y)];
        }

        return AnimatedFirstFrame(furnitureId, slot, secretaryId);
    }

    /// <summary>true when the furniture's pictures depend on who the secretary is.</summary>
    public bool DependsOnSecretary(int furnitureId) =>
        library.Find("furniture", furnitureId, "scripts_json") is { } entry
        && library.LocalPath(entry) is { } path
        && File.ReadAllText(path).Contains("\"umbrellara\"", StringComparison.Ordinal);

    public RoomSprite? Outside(int windowFurnitureId, TimeOfDay time)
    {
        var window = library.Start2.Furniture.FirstOrDefault(f => f.Id == windowFurnitureId);
        if (window is null || OutsideEntry(window.OutsideId, time) is not { } entry || library.LocalPath(entry) is not { } path)
        {
            return null;
        }

        return new RoomSprite(path, null, OutsideX, 0);
    }

    /// <summary>
    /// the secretary's full CG. <paramref name="leftOfCentre"/> is the port's alternative flagship position,
    /// which shifts most ships 200 px left
    /// </summary>
    public RoomSprite? Secretary(int shipId, string art, bool leftOfCentre)
    {
        var graph = library.Start2.ShipGraphs.FirstOrDefault(g => g.Id == shipId);
        if (graph is null)
        {
            return null;
        }

        string? path;
        int[]? offset;

        if (art.StartsWith("history:", StringComparison.Ordinal))
        {
            // placed with the offsets that were current alongside that version, when they were recorded
            var parts = art.Split(':');
            var kept = HistoryFiles(shipId)
                .FirstOrDefault(f => f.Type == parts.ElementAtOrDefault(1) && f.Version == parts.ElementAtOrDefault(2));
            path = kept.Path;
            var recorded = kept.Version is null ? null : ShipGraphHistory.Load(library.Workspace.Data).Get(shipId, kept.Version);
            offset = kept.Type == DamagedArt
                ? recorded?.Damaged ?? recorded?.Normal ?? graph.BokoD ?? graph.BokoN
                : recorded?.Normal ?? graph.BokoN;
        }
        else if (art is OriginalArt or OriginalDamagedArt or SpecialArt)
        {
            // neither picture carries offsets that fit it while a seasonal CG holds the current ones, so the art is
            // centred where full CGs usually stand: the special-remodel art at half size, the card art enlarged
            path = SecretaryEntry(shipId, art) is { } original ? library.LocalPath(original) : null;
            if (path is null)
            {
                return null;
            }

            Rectangle box;
            try
            {
                using var image = Image.Load<Rgba32>(path);
                box = AlphaBounds(image);
            }
            catch (Exception ex) when (ex is IOException or UnknownImageFormatException or InvalidImageContentException)
            {
                return null;
            }

            var artScale = art == SpecialArt ? 0.5 : CardArtScale;
            var artShift = leftOfCentre ? (ShortMoveShips.Contains(shipId) ? -150 : -200) : 0;
            var x = (int)Math.Round(CardArtCentreX - artScale * (box.Left + box.Width / 2.0)) + artShift;
            var y = (int)Math.Round(CardArtTop - artScale * box.Top);
            return new RoomSprite(path, null, x, y) { Scale = artScale };
        }
        else
        {
            path = SecretaryEntry(shipId, art) is { } entry ? library.LocalPath(entry) : null;
            offset = art == DamagedArt ? graph.BokoD ?? graph.BokoN : graph.BokoN;
        }

        if (path is null)
        {
            return null;
        }

        offset ??= [0, 0];
        var shift = leftOfCentre ? (ShortMoveShips.Contains(shipId) ? -150 : -200) : 0;
        return new RoomSprite(path, null, SecretaryX + offset[0] + shift, SecretaryY + offset[1]);
    }

    public static (int X, int Y) Anchor(FurnitureSlot slot, int width, int height) => slot switch
    {
        FurnitureSlot.Floor or FurnitureSlot.Desk => (0, Height - height),
        FurnitureSlot.Window => (WindowX, 0),
        FurnitureSlot.Chest => (Width - width, 0),
        _ => (0, 0),
    };

    private IReadOnlyList<RoomSprite> AnimatedFirstFrame(int furnitureId, FurnitureSlot slot, int secretaryId)
    {
        string? Local(string type) => library.Find("furniture", furnitureId, type) is { } e ? library.LocalPath(e) : null;

        var atlas = Local("movable");
        var framesPath = Local("movable_json");
        var scriptsPath = Local("scripts_json");
        if (atlas is null || framesPath is null || scriptsPath is null)
        {
            return [];
        }

        try
        {
            using var frames = JsonDocument.Parse(ReadJson(framesPath));
            using var scripts = JsonDocument.Parse(ReadJson(scriptsPath));
            if (!frames.RootElement.TryGetProperty("frames", out var frameTable)
                || !scripts.RootElement.TryGetProperty("standard", out var standard)
                || !standard.TryGetProperty("data", out var groups)
                || groups.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sprites = new List<RoomSprite>();
            (int X, int Y)? anchor = null;

            // the umbrella stand names placeholder frames in its script and picks the real ones by the secretary
            if (scripts.RootElement.TryGetProperty("umbrellara", out _))
            {
                foreach (var name in UmbrellaFrames(scripts.RootElement, groups.GetArrayLength(), secretaryId))
                {
                    if (!frameTable.TryGetProperty(name, out var frame))
                    {
                        continue;
                    }

                    var rect = frame.GetProperty("frame");
                    var (trimX, trimY) = frame.TryGetProperty("spriteSourceSize", out var trim) ? (Int(trim, "x"), Int(trim, "y")) : (0, 0);
                    var (fullW, fullH) = frame.TryGetProperty("sourceSize", out var size)
                        ? (Int(size, "w"), Int(size, "h"))
                        : (Int(rect, "w"), Int(rect, "h"));
                    anchor ??= Anchor(slot, fullW, fullH);
                    sprites.Add(new RoomSprite(atlas, new SpriteRect(Int(rect, "x"), Int(rect, "y"), Int(rect, "w"), Int(rect, "h")),
                        anchor.Value.X + trimX, anchor.Value.Y + trimY));
                }

                return sprites;
            }

            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array || group.GetArrayLength() == 0)
                {
                    continue;
                }

                var first = group[0];
                if (!first.TryGetProperty("filename", out var nameElement)
                    || nameElement.GetString() is not { Length: > 0 } name
                    || !frameTable.TryGetProperty(name, out var frame))
                {
                    continue;
                }

                var rect = frame.GetProperty("frame");
                var source = new SpriteRect(Int(rect, "x"), Int(rect, "y"), Int(rect, "w"), Int(rect, "h"));

                // a trimmed frame is shifted back to where it sat in its untrimmed source, and sourceSize sizes the anchor
                var (trimX, trimY) = frame.TryGetProperty("spriteSourceSize", out var trim) ? (Int(trim, "x"), Int(trim, "y")) : (0, 0);
                var (fullW, fullH) = frame.TryGetProperty("sourceSize", out var size)
                    ? (Int(size, "w"), Int(size, "h"))
                    : (source.Width, source.Height);

                // the first group's frame fixes the anchor, as the client sizes the slot from it
                anchor ??= Anchor(slot, fullW, fullH);

                var (offsetX, offsetY) = first.TryGetProperty("offset", out var offset) && offset.GetArrayLength() >= 2
                    ? (offset[0].GetInt32(), offset[1].GetInt32())
                    : (0, 0);

                sprites.Add(new RoomSprite(atlas, source, anchor.Value.X + offsetX + trimX, anchor.Value.Y + offsetY + trimY));
            }

            return sprites;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// the client's choice of umbrella stand frames: the set comes from the secretary's reading or ship type (set 3
    /// when neither is listed), then the stand itself plus one umbrella per listed ship, leaving out the
    /// secretary's own umbrella when the script says so
    /// </summary>
    private IEnumerable<string> UmbrellaFrames(JsonElement script, int groupCount, int secretaryId)
    {
        library.Start2.Ships.TryGetValue(secretaryId, out var ship);
        var yomi = ship?.Yomi ?? "";
        var shipType = ship?.ShipType ?? 0;
        var takesOwn = script.TryGetProperty("AnimationFlag", out var flag) && flag.ValueKind == JsonValueKind.True;

        static List<string> Strings(JsonElement e, string name) =>
            e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString()).ToList()
                : [];

        var fileNo = 3;
        if (script.TryGetProperty("standtype", out var standTypes))
        {
            foreach (var standType in standTypes.EnumerateArray())
            {
                if (Strings(standType, "shipyomi").Contains(yomi) || Strings(standType, "shiptype").Contains(shipType.ToString()))
                {
                    fileNo = Int(standType, "fileno");
                    break;
                }
            }
        }

        // the secretary's place among the listed ships, and how many umbrellas the chosen set has
        var own = 0;
        int? count = null;
        foreach (var set in script.GetProperty("umbrellara").EnumerateArray())
        {
            var names = Strings(set, "shipyomi");
            own = names.IndexOf(yomi) + 1;
            if (Int(set, "fileno") == fileNo)
            {
                count = names.Count;
            }

            if (own > 0)
            {
                break;
            }
        }

        var prefix = $"f385_e07{fileNo - 1}_umbrellarack_{fileNo}";
        yield return prefix + "_A.png";
        for (var i = 1; i < groupCount && !(i > count); i++)
        {
            if (!(i == own && takesOwn))
            {
                yield return $"{prefix}_B{i}.png";
            }
        }
    }

    // some scripts carry a byte order mark, which JsonDocument does not skip on its own
    private static string ReadJson(string path) => File.ReadAllText(path).TrimStart('﻿');

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    /// <summary>width and height from the png header, without decoding the image.</summary>
    public static (int Width, int Height) PngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(header);
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..]));
    }
}
