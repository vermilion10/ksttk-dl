using System.Text.Json;

namespace KsttkDl.Core.MasterData;

public sealed record ShipGraph(
    int Id,
    string FileName,
    string[] Version,
    int? SortNo,
    bool HasBattleN,
    bool HasBokoD);

/// <summary><paramref name="Rarity"/> is api_backs (card frame, 1-8) and <paramref name="SortNo"/> the library number; both are absent, and 0, on abyssal ships.</summary>
public sealed record MstShip(
    int Id, string Name, string Yomi, int VoiceFlag, int ShipType = 0, int Rarity = 0, int SortNo = 0, int SortId = 0);

/// <summary>api_type[2] is the equipment category shown in game; <paramref name="Rarity"/> is api_rare (0 common to 7).</summary>
public sealed record SlotItem(int Id, string Name, int[] Type, string? Version, int Rarity = 0, int SortNo = 0)
{
    public int Category => Type.Length > 2 ? Type[2] : 0;
}

public sealed record MapInfo(int MapAreaId, int No, string Name, bool HasGauge);

public sealed record MapBgm(int MapAreaId, int No, int MovingBgm, int[] BattleBgm, int[] BossBgm);

public sealed record Bgm(int Id, string Name);

/// <summary><paramref name="Type"/> is the room slot: 0 floor, 1 wall, 2 window, 3 object, 4 chest, 5 desk.</summary>
public sealed record Furniture(
    int Id, string Title, int ActiveFlag, string? Version, int Type = 0, int No = 0, int Rarity = 0, string Description = "");

public sealed record UseItem(int Id, string Name);

public sealed record MapArea(int Id, string Name);

/// <summary>yhe subset of api_start2 this tool needs, from the Tibowl/api_start2 mirror. the file is a flat object of api_mst_* arrays - no api_data wrapper - and about 3 mb, so it is parsed once into these records and the json document dropped.</summary>
public sealed class Start2
{
    public required IReadOnlyList<ShipGraph> ShipGraphs { get; init; }
    public required IReadOnlyDictionary<int, MstShip> Ships { get; init; }
    public required IReadOnlyList<SlotItem> SlotItems { get; init; }
    public required IReadOnlyList<MapInfo> MapInfos { get; init; }
    public required IReadOnlyList<MapBgm> MapBgms { get; init; }
    public required IReadOnlyList<Bgm> Bgms { get; init; }
    public required IReadOnlyList<Furniture> Furniture { get; init; }
    public required IReadOnlyList<UseItem> UseItems { get; init; }
    public required IReadOnlyDictionary<int, MapArea> MapAreas { get; init; }
    public required IReadOnlyDictionary<int, string> ShipTypes { get; init; }
    public required IReadOnlyDictionary<int, string> EquipmentTypes { get; init; }

    public static Start2 Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        return new Start2
        {
            ShipGraphs = Read(root, "api_mst_shipgraph", ReadShipGraph),
            Ships = Read(root, "api_mst_ship", ReadShip).ToDictionary(s => s.Id),
            SlotItems = Read(root, "api_mst_slotitem", ReadSlotItem),
            MapInfos = Read(root, "api_mst_mapinfo", ReadMapInfo),
            MapBgms = Read(root, "api_mst_mapbgm", ReadMapBgm),
            Bgms = Read(root, "api_mst_bgm", e => new Bgm(Int(e, "api_id"), Str(e, "api_name"))),
            Furniture = Read(root, "api_mst_furniture", ReadFurniture),
            UseItems = Read(root, "api_mst_useitem", e => new UseItem(Int(e, "api_id"), Str(e, "api_name"))),
            MapAreas = Read(root, "api_mst_maparea", e => new MapArea(Int(e, "api_id"), Str(e, "api_name")))
                .ToDictionary(a => a.Id),
            ShipTypes = Read(root, "api_mst_stype", e => (Id: Int(e, "api_id"), Name: Str(e, "api_name")))
                .ToDictionary(t => t.Id, t => t.Name),
            EquipmentTypes = Read(root, "api_mst_slotitem_equiptype", e => (Id: Int(e, "api_id"), Name: Str(e, "api_name")))
                .ToDictionary(t => t.Id, t => t.Name),
        };
    }

    private static List<T> Read<T>(JsonElement root, string name, Func<JsonElement, T> map)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<T>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            result.Add(map(element));
        }

        return result;
    }

    private static ShipGraph ReadShipGraph(JsonElement e) => new(
        Int(e, "api_id"),
        Str(e, "api_filename"),
        // an array of *strings* here, unlike slotitem/furniture where it is a bare number
        e.TryGetProperty("api_version", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "1").ToArray()
            : ["1"],
        // Nullable on purpose: preload.js compares against 0 in JS, where a missing api_sortno is undefined and `undefined == 0` is false. Folding absent into 0  would skip entries the reference implementation keeps.
        e.TryGetProperty("api_sortno", out var sortNo) && sortNo.ValueKind == JsonValueKind.Number
            ? sortNo.GetInt32()
            : null,
        // Presence, not value: preload.js keys friendly/abyssal/seasonal off whether these coordinate pairs exist at all.
        e.TryGetProperty("api_battle_n", out _),
        e.TryGetProperty("api_boko_d", out _));

    private static MstShip ReadShip(JsonElement e) => new(
        Int(e, "api_id"),
        Str(e, "api_name"),
        Str(e, "api_yomi"),
        Int(e, "api_voicef"),
        Int(e, "api_stype"),
        Int(e, "api_backs"),
        Int(e, "api_sortno"),
        Int(e, "api_sort_id"));

    private static SlotItem ReadSlotItem(JsonElement e) => new(
        Int(e, "api_id"),
        Str(e, "api_name"),
        e.TryGetProperty("api_type", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetInt32()).ToArray()
            : [],
        e.TryGetProperty("api_version", out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32().ToString()
            : null,
        Int(e, "api_rare"),
        Int(e, "api_sortno"));

    private static MapInfo ReadMapInfo(JsonElement e) => new(
        Int(e, "api_maparea_id"),
        Int(e, "api_no"),
        Str(e, "api_name"),
        // The gauge is only requested for maps that have one.
        NotNull(e, "api_required_defeat_count") || NotNull(e, "api_max_maphp"));

    private static MapBgm ReadMapBgm(JsonElement e) => new(
        Int(e, "api_maparea_id"),
        Int(e, "api_no"),
        Int(e, "api_moving_bgm"),
        Ints(e, "api_map_bgm"),
        Ints(e, "api_boss_bgm"));

    private static Furniture ReadFurniture(JsonElement e) => new(
        Int(e, "api_id"),
        Str(e, "api_title"),
        Int(e, "api_active_flag"),
        e.TryGetProperty("api_version", out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32().ToString()
            : null,
        Int(e, "api_type"),
        Int(e, "api_no"),
        Int(e, "api_rarity"),
        Str(e, "api_description"));

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int[] Ints(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetInt32()).ToArray()
            : [];

    private static bool NotNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
}
