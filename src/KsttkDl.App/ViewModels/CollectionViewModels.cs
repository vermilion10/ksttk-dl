using System.Collections.ObjectModel;
using System.Windows.Input;
using KsttkDl.App.Infrastructure;
using KsttkDl.Core.Catalog;
using KsttkDl.Core.Fetch;

namespace KsttkDl.App.ViewModels;

/// <summary>a gallery tile for anything that is not a ship.</summary>
public sealed class EntityTile(int id, string title, string subtitle, int group, AssetEntry? picture, string description = "")
    : Tile
{
    public override int Id { get; } = id;

    public override string Title { get; } = title;

    public override string Subtitle { get; } = subtitle;

    /// <summary>the filter key: equipment category or furniture slot.</summary>
    public int Group { get; } = group;

    public string Description { get; } = description;

    public override AssetEntry? Picture { get; } = picture;
}

/// <summary>an item's page: every picture it has, fetched when it opens.</summary>
public sealed class EntityDetailViewModel : Observable
{
    private ImageAsset? _selected;
    private string _status = "";

    public EntityDetailViewModel(EntityTile tile, string facts, IReadOnlyList<AssetEntry> entries, Action close)
    {
        Tile = tile;
        Facts = facts;
        Images = new ObservableCollection<ImageAsset>(entries
            .Where(e => e.Path.EndsWith(".png", StringComparison.Ordinal))
            .Select(e => new ImageAsset(e, e.AssetType.Replace('_', ' '))));
        Close = new RelayCommand(close);
        Select = new RelayCommand(p => Selected = p as ImageAsset);
        OpenFolder = new RelayCommand(() =>
        {
            if (Images.Select(i => i.ImagePath).FirstOrDefault(p => p is not null) is { } path)
            {
                SyncViewModel.OpenFolder(Path.GetDirectoryName(path)!);
            }
        });

        foreach (var image in Images)
        {
            image.Refresh();
        }

        Selected = Images.FirstOrDefault(i => i.Picture == tile.Picture) ?? Images.FirstOrDefault();
        _ = FetchAsync(entries);
    }

    public EntityTile Tile { get; }

    public string Facts { get; }

    public ObservableCollection<ImageAsset> Images { get; }

    public ImageAsset? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public ICommand Close { get; }

    public ICommand Select { get; }

    public ICommand OpenFolder { get; }

    private async Task FetchAsync(IReadOnlyList<AssetEntry> entries)
    {
        try
        {
            var stats = await AppServices.Current.Fetcher.FetchNowAsync(entries);
            Status = stats.Downloaded > 0 ? $"Downloaded {stats.Downloaded} files" : "";
        }
        catch (Exception ex)
        {
            Status = "Download failed: " + ex.Message;
        }

        foreach (var image in Images)
        {
            image.Refresh();
        }

        // absent variants (no airplane art on a gun, say) are dropped once the cdn has said so
        foreach (var missing in Images.Where(i => i.Absent).ToList())
        {
            Images.Remove(missing);
        }

        Selected ??= Images.FirstOrDefault();
    }
}

/// <summary>shared shape of the equipment and furniture pages: a filtered gallery and an item page.</summary>
public abstract class CollectionViewModel : Observable
{
    private List<EntityTile> _all = [];
    private string _search = "";
    private EntityDetailViewModel? _detail;

    protected CollectionViewModel()
    {
        Open = new RelayCommand(p =>
        {
            if (p is EntityTile tile && AppServices.Current.Library is { } library)
            {
                Detail = new EntityDetailViewModel(tile, Facts(tile), Entries(library, tile), () => Detail = null);
            }
        });
        ApplyFilters = new RelayCommand(Apply);
        AppServices.Current.LibraryChanged += (_, _) => Load();
    }

    public Gallery<EntityTile> Gallery { get; } = new();

    public ObservableCollection<Filter> Groups { get; } = [];

    public abstract string Kanji { get; }

    public abstract string Heading { get; }

    /// <summary>tile picture size: equipment cards are square, furniture is wide.</summary>
    public abstract double TileHeight { get; }

    public bool HasData => AppServices.Current.Library is not null;

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                Apply();
            }
        }
    }

    public EntityDetailViewModel? Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public string Summary => $"{Gallery.Count:N0} of {_all.Count:N0}";

    public ICommand Open { get; }

    public ICommand ApplyFilters { get; }

    protected abstract IEnumerable<EntityTile> Tiles(GameLibrary library);

    protected abstract IEnumerable<(int Value, string Label)> GroupNames(GameLibrary library, IEnumerable<int> used);

    protected abstract string Facts(EntityTile tile);

    protected abstract IReadOnlyList<AssetEntry> Entries(GameLibrary library, EntityTile tile);

    protected void Load()
    {
        Raise(nameof(HasData));
        var library = AppServices.Current.Library;
        _all = library is null ? [] : Tiles(library).ToList();

        Groups.Clear();
        if (library is not null)
        {
            foreach (var (value, label) in GroupNames(library, _all.Select(t => t.Group).Distinct()))
            {
                Groups.Add(new Filter(label, value));
            }
        }

        Apply();
    }

    private void Apply()
    {
        var groups = Groups.Where(g => g.On).Select(g => g.Value).ToHashSet();
        var search = _search.Trim();
        Gallery.SetItems(_all
            .Where(t => groups.Count == 0 || groups.Contains(t.Group))
            .Where(t => search.Length == 0
                        || t.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || t.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || t.Id.ToString() == search)
            .ToList());
        Raise(nameof(Summary));
    }
}

public sealed class EquipmentViewModel : CollectionViewModel
{
    private const int FriendlyCutoff = 1500;

    public EquipmentViewModel() => Load();

    public override string Kanji => "装備";

    public override string Heading => "Equipment";

    public override double TileHeight => 150;

    protected override IEnumerable<EntityTile> Tiles(GameLibrary library) =>
        library.Start2.SlotItems
            .Where(i => i.Id < FriendlyCutoff && i.Name.Length > 0)
            .OrderBy(i => i.SortNo > 0 ? i.SortNo : i.Id)
            .Select(i => new EntityTile(i.Id, library.Names.Item(i).English, i.Name, i.Category,
                library.Find("equipment", i.Id, "card")));

    protected override IEnumerable<(int, string)> GroupNames(GameLibrary library, IEnumerable<int> used) =>
        used.Order().Select(id => (id, library.Start2.EquipmentTypes.GetValueOrDefault(id, $"Type {id}")));

    protected override string Facts(EntityTile tile)
    {
        var library = AppServices.Current.Library!;
        var item = library.Start2.SlotItems.First(i => i.Id == tile.Id);
        var type = library.Start2.EquipmentTypes.GetValueOrDefault(item.Category, "");
        return string.Join("  ·  ", new[] { type, $"Equipment ID {item.Id}", item.Rarity > 0 ? $"Rarity {item.Rarity}" : "" }
            .Where(s => s.Length > 0));
    }

    protected override IReadOnlyList<AssetEntry> Entries(GameLibrary library, EntityTile tile) =>
        library.EntriesFor("equipment", tile.Id);
}

public sealed class FurnitureViewModel : CollectionViewModel
{
    private static readonly string[] Slots = ["Floor", "Wall", "Window", "Object", "Chest", "Desk"];
    private static readonly string[] SlotKanji = ["床", "壁紙", "窓", "装飾", "家具", "机"];

    public FurnitureViewModel() => Load();

    public override string Kanji => "家具";

    public override string Heading => "Furniture";

    public override double TileHeight => 110;

    protected override IEnumerable<EntityTile> Tiles(GameLibrary library) =>
        library.Start2.Furniture
            .Where(f => f.Title.Length > 0)
            .OrderBy(f => f.Type).ThenBy(f => f.No)
            .Select(f => new EntityTile(f.Id, f.Title, SlotName(f.Type), f.Type,
                library.Find("furniture", f.Id, "normal") ?? library.Find("furniture", f.Id, "thumbnail"),
                f.Description.Replace("<br>", "\n")));

    protected override IEnumerable<(int, string)> GroupNames(GameLibrary library, IEnumerable<int> used) =>
        used.Order().Select(t => (t, SlotName(t)));

    protected override string Facts(EntityTile tile) => $"{tile.Subtitle}  ·  Furniture ID {tile.Id}";

    protected override IReadOnlyList<AssetEntry> Entries(GameLibrary library, EntityTile tile) =>
        library.EntriesFor("furniture", tile.Id);

    private static string SlotName(int type) =>
        type >= 0 && type < Slots.Length ? $"{SlotKanji[type]} {Slots[type]}" : $"Type {type}";
}

/// <summary>one sortie map in the list.</summary>
public sealed class MapItem(int area, int no, string name, string areaName) : Tile
{
    private AssetEntry? _background;

    public int Area { get; } = area;

    public int No { get; } = no;

    public override int Id => Area * 10 + No;

    public string Code => $"{Area}-{No}";

    public override string Title { get; } = name;

    public override string Subtitle { get; } = areaName;

    public override AssetEntry? Picture => _background ??=
        AppServices.Current.Library?.MapEntries(Area, No).FirstOrDefault(e => e.AssetType == "background");
}

public sealed class MapsViewModel : Observable
{
    private MapItem? _selected;
    private string _status = "";

    public MapsViewModel()
    {
        AppServices.Current.LibraryChanged += (_, _) => Load();
        OpenFolder = new RelayCommand(() =>
        {
            if (_selected?.ImagePath is { } path)
            {
                SyncViewModel.OpenFolder(Path.GetDirectoryName(path)!);
            }
        });
        Load();
    }

    public ObservableCollection<MapItem> Maps { get; } = [];

    public bool HasData => AppServices.Current.Library is not null;

    public MapItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value) && value is not null)
            {
                _ = FetchAsync(value);
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public ICommand OpenFolder { get; }

    private void Load()
    {
        Raise(nameof(HasData));
        Maps.Clear();
        var library = AppServices.Current.Library;
        if (library is null)
        {
            return;
        }

        foreach (var map in library.Start2.MapInfos.OrderBy(m => m.MapAreaId).ThenBy(m => m.No))
        {
            var area = library.Start2.MapAreas.GetValueOrDefault(map.MapAreaId)?.Name ?? "";
            Maps.Add(new MapItem(map.MapAreaId, map.No, map.Name, area));
        }

        Selected = Maps.FirstOrDefault();
    }

    private async Task FetchAsync(MapItem map)
    {
        var library = AppServices.Current.Library;
        if (library is null)
        {
            return;
        }

        map.Refresh();
        if (map.HasImage)
        {
            Status = "";
            return;
        }

        Status = "Downloading the map…";
        try
        {
            await AppServices.Current.Fetcher.FetchNowAsync(library.MapEntries(map.Area, map.No));
            Status = "";
        }
        catch (Exception ex)
        {
            Status = "Download failed: " + ex.Message;
        }

        map.Refresh();
    }
}
