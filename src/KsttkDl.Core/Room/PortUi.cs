using System.Text.Json.Serialization;
using KsttkDl.Core.Catalog;
using KsttkDl.Core.Fetch;

namespace KsttkDl.Core.Room;

/// <summary>the port's interface skins, by the client's skin id.</summary>
public enum PortSkin
{
    /// <summary>skin 1 with the fleet count, as the port first looked.</summary>
    Classic = 101,
    Skin1 = 102,
    Skin2 = 201,
    Skin3 = 301,
    Skin3Kai = 311,
}

public enum TextAnchor
{
    Left,
    Right,
    Centre,
}

/// <summary>
/// a line of text in the game's font: <paramref name="X"/> is its left edge, right edge or centre by
/// <paramref name="Anchor"/>, and <paramref name="Y"/> the top of its line, as the client positions text
/// </summary>
public sealed record RoomText(string Text, int X, int Y, double Size, uint Color, TextAnchor Anchor, bool Bold = false);

/// <summary>what the port's header and clock say. everything is text, so any value can be typed in.</summary>
public sealed record PortUiValues
{
    public string Name { get; init; } = "提督";

    public string Level { get; init; } = "120";

    public string Rank { get; init; } = "元帥";

    public string Fleets { get; init; } = "4";

    public int Ships { get; init; } = 250;

    public int ShipMax { get; init; } = 300;

    public int Equipment { get; init; } = 1200;

    public int EquipmentMax { get; init; } = 1500;

    public string Fuel { get; init; } = "300000";

    public string Ammo { get; init; } = "300000";

    public string Steel { get; init; } = "300000";

    public string Bauxite { get; init; } = "300000";

    public string Buckets { get; init; } = "3000";

    public string DevMaterials { get; init; } = "3000";

    public string Screws { get; init; } = "100";

    /// <summary>the clock shows the time the room is drawn instead of <see cref="Date"/> and <see cref="Time"/>.</summary>
    public bool LiveClock { get; init; } = true;

    public string Date { get; init; } = "01/01";

    public string Time { get; init; } = "12:00";

    public bool ShowClock { get; init; } = true;

    /// <summary>the friend fleet button's look while a friend fleet event runs.</summary>
    public bool FriendFleetEvent { get; init; }

    [JsonIgnore]
    public string ShipsText => Ships.ToString();

    [JsonIgnore]
    public string EquipmentText => Equipment.ToString();
}

/// <summary>
/// the port screen's interface over the room, laid out from the client's own code: the header bar with the
/// admiral's name, rank and stocks, the frame, the 母港 circle, the ring menu, the clock, the log tab and the
/// option gear. each skin has its own sheet and a few positions of its own. animated parts are drawn at rest,
/// and the hover-only pieces are left out
/// </summary>
public sealed class PortUi(GameLibrary library)
{
    private const uint Light = 0xFFF6F2;
    private const uint Dark = 0x4C4843;
    private const uint Warning = 0xDC5F2A;
    private const uint Caution = 0xDCAB00;

    public static IReadOnlyList<string> Ranks { get; } =
        ["元帥", "大将", "中将", "少将", "大佐", "中佐", "新米中佐", "少佐", "中堅少佐", "新米少佐"];

    private static readonly string[] Shared = ["port_main", "port_ringmenu", "port_option"];

    private static string SkinSheet(PortSkin skin) => skin switch
    {
        PortSkin.Classic or PortSkin.Skin1 => "port_skin_1",
        PortSkin.Skin2 => "port_skin_2",
        PortSkin.Skin3 => "port_skin_3",
        _ => "port_skin_3k",
    };

    private static IEnumerable<string> Sheets(PortSkin skin)
    {
        foreach (var sheet in Shared)
        {
            yield return sheet;
        }

        yield return SkinSheet(skin);
        if (skin is PortSkin.Skin2 or PortSkin.Skin3)
        {
            yield return "port_skin_circle_2";
        }
    }

    /// <summary>the sprite sheets a skin needs, json and png.</summary>
    public IReadOnlyList<AssetEntry> Entries(PortSkin skin) =>
        Sheets(skin).SelectMany(s => new[] { $"kcs2/img/port/{s}.json", $"kcs2/img/port/{s}.png" })
            .Select(path => library.Catalog.FirstOrDefault(e => e.Path == path)
                            ?? new AssetEntry { Path = path, Category = AssetCategory.Ui, Group = "atlas", AssetType = "port" })
            .ToList();

    /// <summary>the interface's pictures, bottom first, and its text; pictures from a missing sheet are left out.</summary>
    public (IReadOnlyList<RoomSprite> Sprites, IReadOnlyList<RoomText> Texts) Compose(PortSkin skin, PortUiValues values, DateTime now)
    {
        var sheets = Sheets(skin).ToDictionary(s => s, Load);
        var sprites = new List<RoomSprite>();
        var texts = new List<RoomText>();

        void Add(RoomSprite? sprite)
        {
            if (sprite is not null)
            {
                sprites.Add(sprite);
            }
        }

        RoomSprite? At(string sheet, int frame, int x, int y) =>
            sheets.GetValueOrDefault(sheet)?.Sprite($"{sheet}_{frame}", x, y);

        RoomSprite? Centred(string sheet, int frame, int x, int y) =>
            sheets.GetValueOrDefault(sheet)?.Centred($"{sheet}_{frame}", x, y);

        var s = SkinSheet(skin);

        // ring menu, in the client's child order
        Add(Centred("port_ringmenu", 17, 476, 333)); // 改装
        Add(Centred("port_ringmenu", 5, 407, 543)); // 工廠
        foreach (var (frame, dx, dy) in new[] { (4, -125, 2), (3, -50, 2), (0, -102, -101), (2, -102, -86), (1, -69, -18) })
        {
            Add(At("port_ringmenu", frame, 296 + dx, 390 + dy)); // 出撃
        }

        Add(Centred("port_ringmenu", 15, 297, 203)); // 編成
        Add(Centred("port_ringmenu", 19, 186, 543)); // 入渠
        Add(Centred("port_ringmenu", 29, 117, 333)); // 補給

        // the log tab, closed
        Add(At("port_main", 3, 534, 673));
        Add(At("port_main", 2, 557, 680));

        // the header bar
        var header = Header(skin);
        Add(Centred(s, header.Gear, header.GearX, header.GearY));
        Add(At(s, header.Background, 0, 0));
        foreach (var (frame, x, y) in header.Parts)
        {
            Add(At(s, frame, x, y));
        }

        var friend = values.FriendFleetEvent ? header.FriendEvent : header.Friend;
        foreach (var (frame, x, y) in new[]
                 {
                     (header.Record, 180, 45), (friend, 300, header.FriendY), (header.Album, 420, 45), (header.Item, 540, 45),
                     (header.Interior, 660, 45), (header.Duty, 780, 45), (header.Shop, 898, 43),
                 })
        {
            Add(At(s, frame, x, y));
        }

        // the frame down the left and along the bottom, with its gear in the corner
        var frameLayer = FrameParts(skin);
        Add(Centred(s, frameLayer.Gear, 1242, 758));
        Add(At(s, frameLayer.Side, 0, 0));
        Add(At(s, frameLayer.Bottom, frameLayer.BottomX, frameLayer.BottomY));

        if (values.ShowClock)
        {
            Add(At("port_main", 18, 0, 573));
        }

        // the 母港 circle
        switch (skin)
        {
            case PortSkin.Classic or PortSkin.Skin1:
                Add(At(s, 1, -40, -46));
                Add(Centred(s, 3, 63, 58));
                break;
            case PortSkin.Skin2:
                Add(At(s, 0, -46, -55));
                Add(Centred("port_skin_circle_2", 2, 67, 58));
                break;
            case PortSkin.Skin3:
                Add(At(s, 0, -40, -49));
                Add(Centred("port_skin_circle_2", 2, 67, 58));
                break;
            default:
                Add(At(s, 2, -40, -49));
                Add(Centred(s, 4, 67, 58));
                break;
        }

        Add(At("port_option", 3, 1131, 645));

        // header text
        var ink = skin is PortSkin.Skin3 or PortSkin.Skin3Kai ? Dark : Light;
        var layout = TextLayout(skin);
        texts.Add(new RoomText(values.Name, 175, 12, 19, ink, TextAnchor.Left));
        texts.Add(new RoomText(values.Level, layout.LevelX, 13, 21, ink, TextAnchor.Left));
        texts.Add(new RoomText(values.Rank.Length > 0 ? $"[{values.Rank}]" : "", layout.RankX, 14, 18, ink, TextAnchor.Left));
        if (skin == PortSkin.Classic)
        {
            texts.Add(new RoomText(values.Fleets, 837, 13, 21, ink, TextAnchor.Left));
        }

        // counts near their limit turn orange, then red, as in game
        var shipsLeft = values.ShipMax - values.Ships;
        texts.Add(new RoomText(values.ShipsText, layout.ShipsX, 13, 21, shipsLeft <= 5 ? Warning : ink, TextAnchor.Right));
        if (skin != PortSkin.Classic)
        {
            var slotsLeft = values.EquipmentMax - values.Equipment;
            texts.Add(new RoomText(values.EquipmentText, layout.EquipmentX, 13, 21,
                slotsLeft <= 5 ? Warning : slotsLeft < 20 ? Caution : ink, TextAnchor.Right));
        }

        texts.Add(new RoomText(values.Fuel, 1092, 50, 19, ink, TextAnchor.Right));
        texts.Add(new RoomText(values.Ammo, 1092, 78, 19, ink, TextAnchor.Right));
        texts.Add(new RoomText(values.Steel, 1197, 50, 19, ink, TextAnchor.Right));
        texts.Add(new RoomText(values.Bauxite, 1197, 78, 19, ink, TextAnchor.Right));
        texts.Add(new RoomText(values.Buckets, layout.BucketsX, 14, 21, ink, TextAnchor.Right));
        texts.Add(new RoomText(values.DevMaterials, layout.DevX, 14, 21, ink, TextAnchor.Right));
        if (skin != PortSkin.Classic)
        {
            texts.Add(new RoomText(values.Screws, 1194, 14, 21, ink, TextAnchor.Right));
        }

        if (values.ShowClock)
        {
            var date = values.LiveClock ? now.ToString("MM'/'dd") : values.Date;
            var time = values.LiveClock ? now.ToString("HH':'mm") : values.Time;
            texts.Add(new RoomText(date, 58, 612, 26, 0xFFFFFF, TextAnchor.Centre));
            texts.Add(new RoomText(time, 75, 651, 41, 0xFFFFFF, TextAnchor.Centre));
        }

        return (sprites, texts);
    }

    private Atlas? Load(string sheet)
    {
        var json = library.Catalog.FirstOrDefault(e => e.Path == $"kcs2/img/port/{sheet}.json");
        var png = library.Catalog.FirstOrDefault(e => e.Path == $"kcs2/img/port/{sheet}.png");
        return json is not null && png is not null && library.LocalPath(json) is { } jsonPath && library.LocalPath(png) is { } pngPath
            ? Atlas.Load(jsonPath, pngPath)
            : null;
    }

    private sealed record HeaderParts(
        int Gear, int GearX, int GearY, int Background, (int Frame, int X, int Y)[] Parts,
        int Record, int Friend, int FriendEvent, int FriendY, int Album, int Item, int Interior, int Duty, int Shop);

    private static HeaderParts Header(PortSkin skin)
    {
        (int, int, int)[] skin1Common = [(18, 991, 51), (19, 988, 79), (20, 1095, 52), (21, 1096, 78), (12, 415, 0)];
        (int, int, int)[] skin23 =
        [
            (19, 413, 16), (20, 698, 18), (21, 791, 18), (14, 904, 17), (15, 1003, 17), (16, 1105, 16),
            (10, 991, 51), (11, 988, 79), (12, 1095, 52), (13, 1096, 78), (4, 415, 0),
        ];

        return skin switch
        {
            PortSkin.Classic => new(2, 68, 60, 14,
                [(30, 433, 16), (31, 745, 18), (32, 862, 18), (22, 1011, 17), (23, 1099, 17), .. skin1Common],
                40, 45, 42, 43, 47, 33, 35, 37, 25),
            PortSkin.Skin1 => new(2, 68, 60, 14,
                [(27, 413, 16), (28, 698, 18), (29, 791, 18), (22, 904, 17), (23, 1003, 17), (24, 1105, 16), .. skin1Common],
                40, 45, 42, 43, 47, 33, 35, 37, 25),
            PortSkin.Skin2 => new(1, 66, 58, 6, skin23, 29, 34, 31, 44, 36, 22, 24, 26, 17),
            PortSkin.Skin3 => new(1, 67, 60, 6, skin23, 29, 34, 31, 45, 36, 22, 24, 26, 17),
            _ => new(3, 67, 60, 22,
                [
                    (32, 409, 20), (33, 629, 21), (34, 737, 21), (27, 904, 17), (28, 1003, 17), (29, 1105, 16),
                    (23, 991, 51), (24, 988, 79), (25, 1095, 52), (26, 1096, 78), (20, 415, 0),
                ],
                42, 45, 44, 45, 49, 35, 37, 39, 30),
        };
    }

    private static (int Gear, int Side, int Bottom, int BottomX, int BottomY) FrameParts(PortSkin skin) => skin switch
    {
        PortSkin.Classic or PortSkin.Skin1 => (2, 13, 11, 388, 706),
        PortSkin.Skin2 => (2, 5, 3, 382, 705),
        PortSkin.Skin3 => (2, 5, 3, 387, 707),
        _ => (6, 21, 19, 387, 707),
    };

    private static (int LevelX, int RankX, int ShipsX, int EquipmentX, int BucketsX, int DevX) TextLayout(PortSkin skin) => skin switch
    {
        PortSkin.Classic => (600, 651, 999, 0, 1094, 1194),
        PortSkin.Skin3Kai => (477, 528, 720, 890, 987, 1098),
        _ => (551, 602, 780, 887, 987, 1098),
    };
}
