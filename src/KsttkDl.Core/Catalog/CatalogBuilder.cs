using KsttkDl.Core.MasterData;
using KsttkDl.Core.Naming;

namespace KsttkDl.Core.Catalog;

/// <summary> enumerates every asset the game is expected to have, from master data alone. a port of preload.js's caching functions: which asset types exist for a friendly ship versus an abyssal versus a seasonal variant, which equipment gets airplane assets, which ids are known-dead. those rules have enough edge cases that guessing produces a catalog that is mostly 404s. combinations that still 404 are handled by the downloader rather than by narrowing the catalog </summary>
public sealed class CatalogBuilder(
    Start2 start2,
    Translations translations,
    VersionInfo versions,
    bool includeRare)
{
    /// <summary>equipment ids at or above this are abyssal and have a much smaller asset set.</summary>
    private const int FriendlyEquipCutoff = 1500;

    /// <summary>seasonal shipgraph entries whose CGs were never served </summary>
    private static readonly HashSet<int> DeadSeasonalShips =
        [5358, 5433, 5434, 5435, 5439, 5440, 5441, 5446];

    /// <summary>furniture ids with no "normal" image despite being inactive.</summary>
    private static readonly HashSet<int> FurnitureWithoutNormal =
        [8, 32, 43, 62, 121, 131, 134, 150, 153, 163, 167, 169, 173, 177, 190, 191];

    private static readonly HashSet<int> MissingSoundEffects =
        [119, 232, 233, 234, 236, 251, 259, 260, 261, 262, 263];

    private static readonly HashSet<int> UseItemsWithoutCard =
        [2, 10, 31, 32, 33, 34, 44, 49, 50, 51, 53, 76];

    public IReadOnlyList<AssetEntry> Build(IReadOnlySet<AssetCategory> categories)
    {
        var entries = new List<AssetEntry>();

        if (categories.Contains(AssetCategory.Ships))
        {
            AddShips(entries);
        }

        if (categories.Contains(AssetCategory.Equipment))
        {
            AddEquipment(entries);
        }

        if (categories.Contains(AssetCategory.Maps))
        {
            AddMaps(entries);
        }

        if (categories.Contains(AssetCategory.Audio))
        {
            AddBgm(entries);
            AddSoundEffects(entries);
            AddTitleCalls(entries);
            AddShipVoices(entries);
            AddNpcVoices(entries);
        }

        if (categories.Contains(AssetCategory.Ui))
        {
            AddStaticAssets(entries);
            AddUseItems(entries);
        }

        if (categories.Contains(AssetCategory.Furniture))
        {
            AddFurniture(entries);
        }

        // the same path can be reached twice (a BGM id in both the fixed range and  api_mst_mapbgm). The duplicate carries the same metadata, so keep the first.
        return entries
            .GroupBy(e => e.Path, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private void AddShips(List<AssetEntry> entries)
    {
        foreach (var graph in start2.ShipGraphs)
        {
            // sortno 0 with a boko_d is an unused friendly slot.
            if (graph.SortNo == 0 && graph.HasBokoD)
            {
                continue;
            }

            var version = graph.Version.Length > 0 && graph.Version[0] != "1"
                ? $"?version={graph.Version[0]}"
                : "";

            start2.Ships.TryGetValue(graph.Id, out var ship);
            var named = ship is not null && ship.Name.Length > 0;

            var types = new List<string>();
            var keyedTypes = new List<string>();

            // seasonal CGs are a separate id space (5001+) with no api_mst_ship row, so they cannot be named and are grouped apart from the ships that can be
            var group = graph.HasBattleN ? "ship" : "ship-seasonal";

            if (!graph.HasBattleN)
            {
                // seasonal CG: no battle graphics, only the port art
                if (DeadSeasonalShips.Contains(graph.Id))
                {
                    continue;
                }

                types.AddRange(["character_full", "character_up"]);
                if (includeRare)
                {
                    types.AddRange(["card", "character_full_dmg", "character_up_dmg"]);
                }
            }
            else if (graph.HasBokoD && named)
            {
                types.AddRange([
                    "card", "card_dmg",
                    "banner", "banner_dmg", "banner_g_dmg",
                    "banner2", "banner2_dmg", "banner2_g_dmg",
                    "character_full", "character_full_dmg",
                    "character_up", "character_up_dmg",
                    "remodel", "remodel_dmg",
                    "supply_character", "supply_character_dmg",
                    "album_status",
                ]);
                keyedTypes.AddRange(["full", "full_dmg"]);

                if (VoiceLines.SpecialCg.Contains(graph.Id))
                {
                    types.Add("special");
                }

                if (includeRare)
                {
                    types.AddRange([
                        "card_round", "icon_box",
                        "reward_card", "reward_icon",
                        "sp_remodel/text_remodel_mes", "sp_remodel/full_x2",
                        "sp_remodel/text_class", "sp_remodel/text_name", "sp_remodel/silhouette",
                    ]);

                    entries.Add(new AssetEntry
                    {
                        Path = $"kcs2/resources/ship/sp_remodel/animation_key/{graph.Id:D4}_remodel.json",
                        VersionQuery = version,
                        Category = AssetCategory.Ships,
                        Group = "ship",
                        EntityId = graph.Id,
                        AssetType = "sp_remodel_animation_key",
                    });
                }
            }
            else if (named)
            {
                // abyssal: banners and the full cg
                types.AddRange(["banner", "banner_g_dmg", "banner3", "banner3_g_dmg"]);
                keyedTypes.Add("full");

                if (includeRare)
                {
                    types.AddRange(["banner_d", "banner_dmg"]);
                    keyedTypes.AddRange(["full_dmg", "full_d", "full_d_dmg"]);
                }
            }

            foreach (var type in types)
            {
                entries.Add(ShipEntry(graph.Id, type, version, group,
                    AssetPath.GetPath(graph.Id, "ship", type, "png")));
            }

            foreach (var type in keyedTypes)
            {
                entries.Add(ShipEntry(graph.Id, type, version, group,
                    AssetPath.GetPath(graph.Id, "ship", type, "png", graph.FileName)));
            }
        }
    }

    private static AssetEntry ShipEntry(int id, string type, string version, string group, string path) => new()
    {
        Path = path,
        VersionQuery = version,
        Category = AssetCategory.Ships,
        Group = group,
        EntityId = id,
        AssetType = type,
    };

    private void AddEquipment(List<AssetEntry> entries)
    {
        string[] friendlyTypes = includeRare
            ? ["card", "card_t", "item_character", "item_on", "item_up", "remodel", "statustop_item", "btxt_flat"]
            : ["card", "card_t", "item_character", "item_on", "item_up", "remodel", "statustop_item"];

        string[] abyssalTypes = includeRare ? ["item_up", "btxt_flat"] : [];

        foreach (var item in start2.SlotItems)
        {
            var version = item.Version is null ? "" : $"?version={item.Version}";
            var isFriendly = item.Id < FriendlyEquipCutoff;

            foreach (var type in isFriendly ? friendlyTypes : abyssalTypes)
            {
                // equipment 42 has no item_character despite being friendly
                if (item.Id == 42 && type == "item_character")
                {
                    continue;
                }

                entries.Add(EquipEntry(item.Id, type, version, AssetPath.GetPath(item.Id, "slot", type, "png")));
            }

            // api_type[4] is the icon id; non-zero means an aircraft, which gets land-base air unit art
            if ((item.Type.Length > 4 && item.Type[4] != 0 && isFriendly) || includeRare)
            {
                foreach (var type in new[] { "airunit_fairy", "airunit_banner", "airunit_name" })
                {
                    entries.Add(EquipEntry(item.Id, type, version, AssetPath.GetPath(item.Id, "slot", type, "png")));
                }
            }

            // plane sprites are unhashed and only exist for a small id range
            if (item.Id < 5 || item.Id is > 10 and < 38 || includeRare)
            {
                entries.Add(EquipEntry(item.Id, "plane", "", $"kcs2/resources/plane/{item.Id:D3}.png"));
                entries.Add(EquipEntry(item.Id, "plane_r", "", $"kcs2/resources/plane/r{item.Id:D3}.png"));
            }

            if (item.Id < 18 || includeRare)
            {
                entries.Add(EquipEntry(item.Id, "plane_e", "", $"kcs2/resources/plane/e{item.Id:D3}.png"));
            }
        }
    }

    private static AssetEntry EquipEntry(int id, string type, string version, string path) => new()
    {
        Path = path,
        VersionQuery = version,
        Category = AssetCategory.Equipment,
        Group = "equipment",
        EntityId = id,
        AssetType = type,
    };

    private void AddMaps(List<AssetEntry> entries)
    {
        foreach (var map in start2.MapInfos)
        {
            var version = versions.MapQuery(map.MapAreaId, map.No);
            var stem = $"kcs2/resources/map/{map.MapAreaId:D3}/{map.No:D2}";

            foreach (var (suffix, type) in new[]
                     {
                         (".png", "background"),
                         ("_info.json", "info"),
                         ("_image.json", "image_atlas"),
                         ("_image.png", "image_sheet"),
                     })
            {
                entries.Add(new AssetEntry
                {
                    Path = stem + suffix,
                    VersionQuery = version,
                    Category = AssetCategory.Maps,
                    Group = "map",
                    EntityId = map.MapAreaId,
                    SecondaryId = map.No,
                    AssetType = type,
                });
            }

            if (map.HasGauge)
            {
                entries.Add(new AssetEntry
                {
                    Path = $"kcs2/resources/gauge/{map.MapAreaId:D3}{map.No:D2}.json",
                    VersionQuery = version,
                    Category = AssetCategory.Maps,
                    Group = "gauge",
                    EntityId = map.MapAreaId,
                    SecondaryId = map.No,
                    AssetType = "gauge",
                });
            }
        }
    }

    private void AddBgm(List<AssetEntry> entries)
    {
        // a known contiguous range, widened with anything api_mst_mapbgm references but the range does not cover yet
        var battle = new SortedSet<int>(Enumerable.Range(1, 187).Where(i => i != 24));
        foreach (var map in start2.MapBgms)
        {
            battle.Add(map.MovingBgm);
            foreach (var id in map.BattleBgm.Concat(map.BossBgm))
            {
                battle.Add(id);
            }
        }

        foreach (var id in battle)
        {
            entries.Add(BgmEntry(id, "battle"));
        }

        var port = new SortedSet<int>(Enumerable.Range(101, 43).Concat(Enumerable.Range(201, 49)));
        foreach (var bgm in start2.Bgms)
        {
            port.Add(bgm.Id);
        }

        foreach (var id in port)
        {
            entries.Add(BgmEntry(id, "port"));
        }

        foreach (var id in Enumerable.Range(1, 5))
        {
            entries.Add(BgmEntry(id, "fanfare"));
        }
    }

    private static AssetEntry BgmEntry(int id, string kind) => new()
    {
        Path = AssetPath.GetPath(id, "bgm", kind, "mp3"),
        Category = AssetCategory.Audio,
        Group = "bgm",
        EntityId = id,
        AssetType = kind,
    };

    private static void AddSoundEffects(List<AssetEntry> entries)
    {
        var ids = Enumerable.Range(101, 20)
            .Concat(Enumerable.Range(201, 64))
            .Where(i => !MissingSoundEffects.Contains(i))
            .Concat(Enumerable.Range(301, 27));

        foreach (var id in ids)
        {
            entries.Add(new AssetEntry
            {
                Path = $"kcs2/resources/se/{id}.mp3",
                Category = AssetCategory.Audio,
                Group = "se",
                EntityId = id,
                AssetType = "se",
            });
        }
    }

    private static void AddTitleCalls(List<AssetEntry> entries)
    {
        foreach (var (set, count) in new[] { (1, 86), (2, 49) })
        {
            foreach (var i in Enumerable.Range(1, count))
            {
                entries.Add(new AssetEntry
                {
                    Path = $"kcs2/resources/voice/titlecall_{set}/{i:D3}.mp3",
                    Category = AssetCategory.Audio,
                    Group = "titlecall",
                    EntityId = i,
                    SecondaryId = set,
                    AssetType = $"titlecall_{set}",
                });
            }
        }
    }

    private void AddShipVoices(List<AssetEntry> entries)
    {
        foreach (var graph in start2.ShipGraphs)
        {
            // only fully implemented friendly ships have voice folders
            if (graph.SortNo == 0 || !graph.HasBattleN || !graph.HasBokoD)
            {
                continue;
            }

            if (!start2.Ships.TryGetValue(graph.Id, out var ship))
            {
                continue;
            }

            foreach (var line in VoiceLines.LinesFor(graph.Id, ship.VoiceFlag, includeRare))
            {
                // voice assets version separately from cgs: index 2 covers Poke(2)/Poke(3), index 1 everything else
                var index = line is 2 or 3 ? 2 : 1;
                var version = graph.Version.Length > index && graph.Version[index] != "1"
                    ? $"?version={graph.Version[index]}"
                    : "";

                entries.Add(new AssetEntry
                {
                    Path = $"kcs/sound/kc{graph.FileName}/{VoiceLines.Filename(graph.Id, line)}.mp3",
                    VersionQuery = version,
                    Category = AssetCategory.Audio,
                    Group = "voice",
                    EntityId = graph.Id,
                    SecondaryId = line,
                    AssetType = VoiceLines.Label(line),
                });
            }
        }
    }

    /// <summary> npc, event and abyssal lines live in fixed pseudo-ship folders with no master-data listing; which ids exist is only discoverable from the translation data </summary>
    private void AddNpcVoices(List<AssetEntry> entries)
    {
        foreach (var (section, folder) in new[] { ("npc", "kc9999"), ("event", "kc9997"), ("abyssal", "kc9998") })
        {
            if (!translations.Quotes.TryGetValue(section, out var lines))
            {
                continue;
            }

            foreach (var (id, text) in lines)
            {
                // "_old" keys are superseded lines that no longer resolve
                if (text.Length == 0 || id.Contains("_old", StringComparison.Ordinal))
                {
                    continue;
                }

                entries.Add(new AssetEntry
                {
                    Path = $"kcs/sound/{folder}/{id}.mp3",
                    Category = AssetCategory.Audio,
                    Group = section,
                    EntityId = int.TryParse(id, out var numeric) ? numeric : 0,
                    AssetType = id,
                });
            }
        }
    }

    private void AddFurniture(List<AssetEntry> entries)
    {
        for (var i = 0; i <= 8; i++)
        {
            for (var j = 1; j <= 5; j++)
            {
                entries.Add(new AssetEntry
                {
                    Path = $"kcs2/resources/furniture/outside/window_bg_{i}-{j}.png",
                    Category = AssetCategory.Furniture,
                    Group = "furniture-outside",
                    EntityId = i,
                    SecondaryId = j,
                    AssetType = "window_bg",
                });
            }
        }

        foreach (var furniture in start2.Furniture)
        {
            var version = furniture.Version is not null && furniture.Version != "1"
                ? $"?version={furniture.Version}"
                : "";

            if (furniture.ActiveFlag == 1)
            {
                // animated furniture: a script plus its movable sprite sheet
                entries.Add(FurnitureEntry(furniture.Id, "scripts", "json", version));
                entries.Add(FurnitureEntry(furniture.Id, "movable", "json", version));
                entries.Add(FurnitureEntry(furniture.Id, "movable", "png", version));
                entries.Add(FurnitureEntry(furniture.Id, "thumbnail", "png", version));
            }
            else if (!FurnitureWithoutNormal.Contains(furniture.Id))
            {
                entries.Add(FurnitureEntry(furniture.Id, "normal", "png", version));
            }

            if (includeRare)
            {
                entries.Add(FurnitureEntry(furniture.Id, "reward", "png", version));
                entries.Add(FurnitureEntry(furniture.Id, "card", "png", version));
            }
        }
    }

    private static AssetEntry FurnitureEntry(int id, string type, string ext, string version) => new()
    {
        Path = AssetPath.GetPath(id, "furniture", type, ext),
        VersionQuery = version,
        Category = AssetCategory.Furniture,
        Group = "furniture",
        EntityId = id,
        AssetType = ext == "json" ? type + "_json" : type,
    };

    private void AddUseItems(List<AssetEntry> entries)
    {
        foreach (var item in start2.UseItems)
        {
            if (item.Name.Length == 0)
            {
                continue;
            }

            if (!UseItemsWithoutCard.Contains(item.Id))
            {
                entries.Add(UseItemEntry(item.Id, "card", $"kcs2/resources/useitem/card/{item.Id:D3}.png"));
            }

            if (item.Id < 49 && item.Id != 10)
            {
                entries.Add(UseItemEntry(item.Id, "card_small", $"kcs2/resources/useitem/card_/{item.Id:D3}.png"));
            }
        }
    }

    private static AssetEntry UseItemEntry(int id, string type, string path) => new()
    {
        Path = path,
        Category = AssetCategory.Ui,
        Group = "useitem",
        EntityId = id,
        AssetType = type,
    };

    private void AddStaticAssets(List<AssetEntry> entries)
    {
        foreach (var path in StaticAssets.FixedUrls)
        {
            entries.Add(new AssetEntry
            {
                Path = path,
                Category = AssetCategory.Ui,
                Group = "static",
                AssetType = System.IO.Path.GetFileNameWithoutExtension(path),
            });
        }

        foreach (var (category, files) in StaticAssets.AtlasCategories)
        {
            var version = versions.CategoryQuery(category);
            foreach (var file in files)
            {
                entries.Add(new AssetEntry
                {
                    Path = $"kcs2/img/{category}/{file}",
                    VersionQuery = version,
                    Category = AssetCategory.Ui,
                    Group = "atlas",
                    AssetType = category,
                });
            }
        }

        var mapVersion = versions.CategoryQuery("map");
        foreach (var name in new[] { "bg_h", "bg_y" })
        {
            entries.Add(new AssetEntry
            {
                Path = $"kcs2/img/common/bg_map/{name}.png",
                VersionQuery = mapVersion,
                Category = AssetCategory.Ui,
                Group = "static",
                AssetType = name,
            });
        }

        if (!includeRare)
        {
            return;
        }

        for (var i = 0; i < 50; i++)
        {
            foreach (var path in new[]
                     {
                         $"kcs2/resources/stype/etext/{i:D3}.png",
                         $"kcs2/resources/stype/etext/sp{i:D3}.png",
                         $"kcs2/resources/area/airunit/{i:D3}.png",
                         $"kcs2/resources/area/airunit_extend_confirm/{i:D3}_.png",
                         $"kcs2/resources/area/airunit_extend_confirm/{i:D3}.png",
                         $"kcs2/resources/area/sally/{i:D3}.png",
                     })
            {
                entries.Add(new AssetEntry
                {
                    Path = path,
                    Category = AssetCategory.Ui,
                    Group = "static",
                    EntityId = i,
                    AssetType = System.IO.Path.GetFileNameWithoutExtension(path),
                });
            }
        }

        for (var i = 1; i <= 20; i++)
        {
            foreach (var suffix in new[] { "", "_off" })
            {
                entries.Add(new AssetEntry
                {
                    Path = $"kcs2/resources/worldselect/btn_chinjyufu{i}{suffix}.png",
                    Category = AssetCategory.Ui,
                    Group = "static",
                    EntityId = i,
                    AssetType = $"btn_chinjyufu{suffix}",
                });
            }
        }
    }
}
