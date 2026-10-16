using System.Text.Json;
using KsttkDl.Core.Catalog;
using KsttkDl.Core.Fetch;

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
/// and where its top-left corner lands on the 1200×720 room.
/// </summary>
public sealed record RoomSprite(string Path, SpriteRect? Source, int X, int Y);

public readonly record struct SpriteRect(int X, int Y, int Width, int Height);

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

    public AssetEntry? SecretaryEntry(int shipId, bool damaged) =>
        library.Find("ship", shipId, damaged ? "full_dmg" : "full") ?? library.Find("ship", shipId, "full");

    public static TimeOfDay TimeAt(int hour) => hour switch
    {
        >= 20 or < 4 => TimeOfDay.Night,
        < 8 => TimeOfDay.Dawn,
        < 16 => TimeOfDay.Day,
        < 18 => TimeOfDay.Evening,
        _ => TimeOfDay.Dusk,
    };

    /// <summary>the sprites of one piece of furniture at its slot's anchor; empty while its files are missing.</summary>
    public IReadOnlyList<RoomSprite> Furniture(int furnitureId)
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

        return AnimatedFirstFrame(furnitureId, slot);
    }

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
    public RoomSprite? Secretary(int shipId, bool damaged, bool leftOfCentre)
    {
        var graph = library.Start2.ShipGraphs.FirstOrDefault(g => g.Id == shipId);
        if (graph is null || SecretaryEntry(shipId, damaged) is not { } entry || library.LocalPath(entry) is not { } path)
        {
            return null;
        }

        var offset = (damaged ? graph.BokoD : graph.BokoN) ?? graph.BokoN ?? [0, 0];
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

    private IReadOnlyList<RoomSprite> AnimatedFirstFrame(int furnitureId, FurnitureSlot slot)
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
