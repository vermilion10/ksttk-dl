using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using KsttkDl.App.Infrastructure;
using KsttkDl.Core.Catalog;
using KsttkDl.Core.Fetch;
using KsttkDl.Core.MasterData;

namespace KsttkDl.App.ViewModels;

public sealed class ShipTile(MstShip ship, string english, string type, AssetEntry? card) : Tile
{
    public MstShip Ship { get; } = ship;

    public override int Id => Ship.Id;

    public override string Title => english.Length > 0 ? english : Ship.Name;

    public override string Subtitle => Ship.Name;

    public string ShipType { get; } = type;

    public override AssetEntry? Picture { get; } = card;

    public int Rarity => Ship.Rarity;

    public string Number => Ship.SortNo > 0 ? $"No.{Ship.SortNo}" : $"#{Ship.Id}";

    /// <summary>the card frame colour, following the game's rarity ladder from blue to holo.</summary>
    public Brush Frame => Frames[Math.Clamp(Ship.Rarity, 0, Frames.Length - 1)];

    private static readonly Brush[] Frames =
    [
        Solid("#7E8790"),
        Solid("#6E8DB8"),
        Solid("#4C86D6"),
        Solid("#33AFC0"),
        Solid("#A7B0B8"),
        Gradient("#E6C35A", "#B8902F"),
        Gradient("#F5A3C7", "#8FD8F0", "#F1E38A"),
        Gradient("#F7A1C4", "#A0E3F5", "#F4E07A", "#B49AF0"),
        Gradient("#FF8FB8", "#7FE2FF", "#FFE36E", "#9C8CFF"),
    ];

    private static Brush Solid(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Brush Gradient(params string[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        for (var i = 0; i < colors.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]),
                (double)i / (colors.Length - 1)));
        }

        brush.Freeze();
        return brush;
    }
}

public sealed class Filter(string label, int value) : Observable
{
    private bool _on;

    public string Label { get; } = label;

    public int Value { get; } = value;

    public bool On
    {
        get => _on;
        set => Set(ref _on, value);
    }
}

public sealed class ShipsViewModel : Observable
{
    private readonly MainViewModel _main;
    private List<ShipTile> _all = [];
    private string _search = "";
    private bool _abyssal;
    private ShipDetailViewModel? _detail;

    public ShipsViewModel(MainViewModel main)
    {
        _main = main;
        Open = new RelayCommand(p =>
        {
            if (p is ShipTile tile)
            {
                Detail = new ShipDetailViewModel(tile, () => Detail = null);
            }
        });
        GoToSync = new RelayCommand(() => _main.Page = Page.Sync);
        ClearFilters = new RelayCommand(() =>
        {
            foreach (var filter in Types)
            {
                filter.On = false;
            }

            Search = "";
            Apply();
        });
        ApplyFilters = new RelayCommand(Apply);

        AppServices.Current.LibraryChanged += (_, _) => Load();
        Load();
    }

    public Gallery<ShipTile> Gallery { get; } = new();

    public ObservableCollection<Filter> Types { get; } = [];

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

    /// <summary>abyssal ships instead of shipgirls.</summary>
    public bool Abyssal
    {
        get => _abyssal;
        set
        {
            if (Set(ref _abyssal, value))
            {
                Load();
            }
        }
    }

    public ShipDetailViewModel? Detail
    {
        get => _detail;
        private set
        {
            _detail?.Dispose();
            Set(ref _detail, value);
        }
    }

    public string Summary => $"{Gallery.Count:N0} of {_all.Count:N0}";

    public ICommand Open { get; }

    public ICommand GoToSync { get; }

    public ICommand ClearFilters { get; }

    public ICommand ApplyFilters { get; }

    private void Load()
    {
        Raise(nameof(HasData));
        var library = AppServices.Current.Library;
        if (library is null)
        {
            _all = [];
            Gallery.SetItems([]);
            Raise(nameof(Summary));
            return;
        }

        var graphs = library.Start2.ShipGraphs.ToDictionary(g => g.Id);
        _all = library.Start2.Ships.Values
            .Where(s => s.Name.Length > 0 && graphs.ContainsKey(s.Id))
            .Where(s => _abyssal ? s.SortNo == 0 && s.Id > 1500 : s.SortNo > 0)
            .OrderBy(s => _abyssal ? s.Id : s.SortNo)
            .Select(s => new ShipTile(
                s,
                library.Names.Ship(s).English,
                library.Start2.ShipTypes.GetValueOrDefault(s.ShipType, ""),
                library.Find("ship", s.Id, _abyssal ? "full" : "card")))
            .ToList();

        // two type ids share the name 戦艦 (fast and slow battleships), so chips go by name
        Types.Clear();
        foreach (var type in _all.GroupBy(t => t.ShipType).OrderBy(g => g.Min(t => t.Ship.ShipType)))
        {
            Types.Add(new Filter(type.Key, type.Min(t => t.Ship.ShipType)));
        }

        Apply();
    }

    private void Apply()
    {
        var types = Types.Where(t => t.On).Select(t => t.Label).ToHashSet();
        var search = _search.Trim();
        Gallery.SetItems(_all
            .Where(t => types.Count == 0 || types.Contains(t.ShipType))
            .Where(t => search.Length == 0
                        || t.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || t.Ship.Name.Contains(search, StringComparison.Ordinal)
                        || t.Ship.Yomi.Contains(search, StringComparison.Ordinal)
                        || t.Id.ToString() == search)
            .ToList());
        Raise(nameof(Summary));
    }
}

/// <summary>one picture on a ship page, chosen from the strip of thumbnails.</summary>
public sealed class ImageAsset(AssetEntry entry, string label) : Tile
{
    public override int Id => Picture.EntityId;

    public override string Title { get; } = label;

    public override AssetEntry Picture { get; } = entry;
}

public sealed class VoiceLine(AssetEntry entry, string label, string? quote) : Observable
{
    private bool _playing;

    public AssetEntry Entry { get; } = entry;

    public string Label { get; } = label;

    // the translation files carry the game's own line breaks as html
    public string Quote { get; } = (quote ?? "").Replace("<br />", "\n").Replace("<br/>", "\n").Replace("<br>", "\n");

    public bool HasQuote => Quote.Length > 0;

    public bool Playing
    {
        get => _playing;
        set => Set(ref _playing, value);
    }
}

public sealed class ShipDetailViewModel : Observable, IDisposable
{
    // the order a reader wants: the big art first, then cards, then the strips
    private static readonly string[] ImageOrder =
    [
        "full", "full_dmg", "character_full", "character_full_dmg", "character_up", "character_up_dmg",
        "card", "card_dmg", "special", "album_status", "remodel", "remodel_dmg", "supply_character",
        "supply_character_dmg", "banner", "banner_dmg", "banner_g_dmg", "banner2", "banner2_dmg", "banner2_g_dmg",
        "banner3", "banner3_g_dmg",
    ];

    private readonly System.Windows.Media.MediaPlayer _player = new();
    private ImageAsset? _selected;
    private VoiceLine? _playing;
    private string _status = "";
    private bool _busy;

    public ShipDetailViewModel(ShipTile tile, Action close)
    {
        Tile = tile;
        var library = AppServices.Current.Library!;
        var ship = tile.Ship;
        var resolved = library.Names.Ship(ship);

        English = resolved.English;
        Japanese = ship.Name;
        Yomi = ship.Yomi is "-" ? "" : ship.Yomi;
        Facts = string.Join("  ·  ", new[]
        {
            tile.ShipType,
            ship.SortNo > 0 ? $"Library No.{ship.SortNo}" : "",
            $"Ship ID {ship.Id}",
            ship.Rarity > 0 ? $"Rarity {ship.Rarity}" : "",
        }.Where(s => s.Length > 0));

        Images = new ObservableCollection<ImageAsset>(library.ShipImages(ship.Id)
            .OrderBy(e => Array.IndexOf(ImageOrder, e.AssetType) is var i && i < 0 ? 99 : i)
            .Select(e => new ImageAsset(e, e.AssetType.Replace('_', ' '))));

        var quotes = library.Translations.QuotesFor(ship.Id);
        Voices = new ObservableCollection<VoiceLine>(library.ShipVoices(ship.Id)
            .Select(e => new VoiceLine(e, e.AssetType, quotes.GetValueOrDefault(e.AssetType))));

        Close = new RelayCommand(close);
        Select = new RelayCommand(p => Selected = p as ImageAsset);
        Play = new AsyncCommand(p => PlayAsync((VoiceLine)p!));
        FetchAll = new AsyncCommand(FetchAllAsync, () => !_busy);
        OpenFolder = new RelayCommand(() =>
        {
            var path = Selected?.ImagePath ?? Images.Select(i => i.ImagePath).FirstOrDefault(p => p is not null);
            if (path is not null)
            {
                SyncViewModel.OpenFolder(Path.GetDirectoryName(path)!);
            }
        });

        _player.MediaEnded += (_, _) => StopPlaying();

        foreach (var image in Images)
        {
            image.Refresh();
        }

        Selected = Images.FirstOrDefault();
        _ = FetchAllAsync();
    }

    public ShipTile Tile { get; }

    public string English { get; }

    public string Japanese { get; }

    public string Yomi { get; }

    public string Facts { get; }

    public ObservableCollection<ImageAsset> Images { get; }

    public ObservableCollection<VoiceLine> Voices { get; }

    public bool HasVoices => Voices.Count > 0;

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

    public bool Busy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand Close { get; }

    public ICommand Select { get; }

    public ICommand Play { get; }

    public ICommand FetchAll { get; }

    public ICommand OpenFolder { get; }

    /// <summary>opening a ship fetches its pictures and voices, nothing else.</summary>
    private async Task FetchAllAsync()
    {
        var library = AppServices.Current.Library;
        if (library is null)
        {
            return;
        }

        var entries = Images.Select(i => i.Picture!).Concat(Voices.Select(v => v.Entry)).ToList();
        var missing = entries.Count(e => !library.IsDone(e));
        if (missing == 0)
        {
            Status = "Everything for this ship is downloaded";
            return;
        }

        Busy = true;
        Status = $"Downloading {missing} files…";
        try
        {
            var stats = await AppServices.Current.Fetcher.FetchNowAsync(entries);
            Status = stats.Failed == 0
                ? $"Downloaded {stats.Downloaded} files ({stats.Bytes / 1048576.0:N1} MiB)"
                : $"{stats.Failed} files could not be downloaded";
        }
        catch (Exception ex)
        {
            Status = "Download failed: " + ex.Message;
        }
        finally
        {
            Busy = false;
            foreach (var image in Images)
            {
                image.Refresh();
            }

            Raise(nameof(Selected));
        }
    }

    private async Task PlayAsync(VoiceLine line)
    {
        var library = AppServices.Current.Library;
        if (library is null)
        {
            return;
        }

        if (line.Playing)
        {
            _player.Stop();
            StopPlaying();
            return;
        }

        var path = library.LocalPath(line.Entry);
        if (path is null)
        {
            await AppServices.Current.Fetcher.FetchNowAsync([line.Entry]);
            path = library.LocalPath(line.Entry);
        }

        if (path is null)
        {
            AppServices.Current.Notify("No audio", $"{line.Label} is not available for this ship.", ToastKind.Error);
            return;
        }

        StopPlaying();
        _player.Open(new Uri(path));
        _player.Play();
        _playing = line;
        line.Playing = true;
    }

    private void StopPlaying()
    {
        if (_playing is not null)
        {
            _playing.Playing = false;
            _playing = null;
        }
    }

    public void Dispose()
    {
        _player.Stop();
        _player.Close();
    }
}
