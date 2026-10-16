using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KsttkDl.App.Infrastructure;
using KsttkDl.Core.Fonts;
using KsttkDl.Core.Room;
using Microsoft.Win32;

namespace KsttkDl.App.ViewModels;

/// <summary>one picture on the room canvas, already cut from its atlas.</summary>
public sealed record SpriteImage(ImageSource Image, double X, double Y, double Width, double Height, double Opacity = 1);

/// <summary>a layer of the room: a furniture slot, the view outside, the secretary or the port interface.</summary>
public sealed class RoomLayer(string key, string kanji, string label) : Observable
{
    private IReadOnlyList<SpriteImage> _sprites = [];
    private int _offsetX;
    private int _offsetY;
    private double _opacity = 1;
    private double _scale = 1;
    private bool _visible = true;
    private string _caption = "";

    public string Key { get; } = key;

    public string Kanji { get; } = kanji;

    public string Label { get; } = label;

    public IReadOnlyList<SpriteImage> Sprites
    {
        get => _sprites;
        set
        {
            if (Set(ref _sprites, value))
            {
                Raise(nameof(CentreX));
                Raise(nameof(CentreY));
            }
        }
    }

    /// <summary>the middle of the layer's pictures, which <see cref="Scale"/> sizes around.</summary>
    public double CentreX => _sprites.Count == 0 ? 600 : (_sprites.Min(s => s.X) + _sprites.Max(s => s.X + s.Width)) / 2;

    public double CentreY => _sprites.Count == 0 ? 360 : (_sprites.Min(s => s.Y) + _sprites.Max(s => s.Y + s.Height)) / 2;

    public double Scale
    {
        get => _scale;
        set
        {
            if (Set(ref _scale, Math.Round(Math.Clamp(value, 0.25, 3), 2)))
            {
                Raise(nameof(IsMoved));
            }
        }
    }

    /// <summary>what is placed on this layer, e.g. a furniture title.</summary>
    public string Caption
    {
        get => _caption;
        set => Set(ref _caption, value);
    }

    public int OffsetX
    {
        get => _offsetX;
        set
        {
            if (Set(ref _offsetX, value))
            {
                Raise(nameof(IsMoved));
            }
        }
    }

    public int OffsetY
    {
        get => _offsetY;
        set
        {
            if (Set(ref _offsetY, value))
            {
                Raise(nameof(IsMoved));
            }
        }
    }

    public bool IsMoved => _offsetX != 0 || _offsetY != 0 || _scale != 1;

    public double Opacity
    {
        get => _opacity;
        set => Set(ref _opacity, Math.Clamp(value, 0, 1));
    }

    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    public LayerTweak Tweak => new() { OffsetX = _offsetX, OffsetY = _offsetY, Opacity = _opacity, Scale = _scale, Hidden = !_visible };

    public void Apply(LayerTweak? tweak)
    {
        OffsetX = tweak?.OffsetX ?? 0;
        OffsetY = tweak?.OffsetY ?? 0;
        Opacity = tweak?.Opacity ?? 1;
        Scale = tweak?.Scale ?? 1;
        Visible = !(tweak?.Hidden ?? false);
    }
}

/// <summary>
/// the 模様替え room builder: pick furniture per slot, a secretary and the view outside, then nudge, fade or hide
/// any layer and export the room as a png. placement follows the game client through RoomComposer, so an
/// untouched room matches the port screen
/// </summary>
public sealed class RoomViewModel : Observable
{
    private static readonly string PresetPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ksttk-dl", "rooms.json");

    private static readonly ConcurrentDictionary<string, BitmapSource> Bitmaps = new();

    private readonly Dictionary<FurnitureSlot, int> _furniture = [];
    private readonly RoomPresetStore _presets = new(PresetPath);
    private readonly Dictionary<FurnitureSlot, List<EntityTile>> _slotTiles = [];
    private List<ShipTile> _ships = [];
    private List<ShipTile> _seasonal = [];
    private bool _seasonalArchive;
    private RoomLayer? _selected;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    private int _secretaryId;
    private string _art = RoomComposer.CurrentArt;
    private bool _leftOfCentre;
    private string _shownTime = "";
    private int _secretaryVersion;
    private int _portUiVersion;
    private readonly HashSet<int> _hasSpecialArt = [];
    private TimeOfDay _time = RoomComposer.TimeAt(DateTime.Now.Hour);
    private string _search = "";
    private string _presetName = "";
    private RoomPreset? _preset;
    private string _status = "";
    private int _pending;

    public RoomViewModel()
    {
        Layers =
        [
            new("Floor", "床", "Floor"),
            new("Wall", "壁", "Wall"),
            new("Outside", "景", "View outside"),
            new("Window", "窓", "Window"),
            new("Object", "飾", "Wall object"),
            new("Desk", "机", "Desk"),
            new("Chest", "棚", "Chest"),
            new("Secretary", "艦", "Secretary"),
            new("PortUi", "枠", "Port UI"),
        ];

        PickFurniture = new RelayCommand(p =>
        {
            if (p is EntityTile tile && SelectedSlot is { } slot)
            {
                _ = SetFurnitureAsync(slot, tile.Id);
            }
        });
        PickShip = new RelayCommand(p =>
        {
            if (p is ShipTile tile)
            {
                _ = SetSecretaryAsync(tile.Id);
            }
        });
        Nudge = new RelayCommand(p =>
        {
            if (_selected is null || p is not string direction)
            {
                return;
            }

            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            switch (direction)
            {
                case "Left": _selected.OffsetX -= step; break;
                case "Right": _selected.OffsetX += step; break;
                case "Up": _selected.OffsetY -= step; break;
                case "Down": _selected.OffsetY += step; break;
            }
        });
        ResetLayer = new RelayCommand(() => _selected?.Apply(null), () => _selected is not null);
        ResetAll = new RelayCommand(() =>
        {
            foreach (var layer in Layers)
            {
                layer.Apply(null);
            }
        });
        Randomize = new AsyncCommand(RandomizeAsync, () => AppServices.Current.Library is not null);
        Export = new AsyncCommand(p => ExportAsync(p as string == "2" ? 2 : 1), _ => AppServices.Current.Library is not null);
        SavePreset = new RelayCommand(SaveCurrent, () => _presetName.Trim().Length > 0);
        DeletePreset = new RelayCommand(() =>
        {
            if (_preset is null)
            {
                return;
            }

            _presets.Delete(_preset.Name);
            ReloadPresets();
            AppServices.Current.Notify("Room deleted", _preset?.Name ?? "");
        }, () => _preset is not null);

        PickArt = new RelayCommand(p =>
        {
            if (p is SecretaryArt art)
            {
                Art = art.Key;
            }
        });

        PortUi.Changed += (_, _) => _ = RefreshPortUiAsync();

        // a live clock only redraws when the minute it shows has moved on
        _clock.Tick += (_, _) =>
        {
            if (PortUi.LiveClock && Layer("PortUi").Visible && DateTime.Now.ToString("HH:mm") != _shownTime)
            {
                _ = RefreshPortUiAsync();
            }
        };
        _clock.Start();

        Gallery.SetColumns(3);
        AppServices.Current.LibraryChanged += (_, _) => _ = LoadAsync();
        ReloadPresets();
        _ = LoadAsync();
    }

    public ObservableCollection<RoomLayer> Layers { get; }

    public ObservableCollection<RoomPreset> Presets { get; } = [];

    /// <summary>thumbnails for the selected furniture slot.</summary>
    public Gallery<EntityTile> Gallery { get; } = new();

    /// <summary>secretary candidates, filtered by the search box.</summary>
    public Gallery<ShipTile> ShipGallery { get; } = new();

    public bool HasData => AppServices.Current.Library is not null;

    public RoomLayer? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
            {
                return;
            }

            Raise(nameof(SelectedSlot));
            Raise(nameof(PickerMode));
            Raise(nameof(PickerTitle));
            ShowPicker();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public FurnitureSlot? SelectedSlot =>
        _selected is not null && Enum.TryParse<FurnitureSlot>(_selected.Key, out var slot) ? slot : null;

    /// <summary>the port interface's skin and text.</summary>
    public PortUiEditor PortUi { get; } = new();

    public static IReadOnlyList<PortSkin> Skins { get; } = Enum.GetValues<PortSkin>();

    /// <summary>the pictures the chosen secretary can use.</summary>
    public ObservableCollection<SecretaryArt> Arts { get; } = [];

    /// <summary>Furniture, Ship, Outside or PortUi: which picker the side panel shows.</summary>
    public string PickerMode => _selected?.Key switch
    {
        "Secretary" => "Ship",
        "Outside" => "Outside",
        "PortUi" => "PortUi",
        null => "",
        _ => "Furniture",
    };

    public string PickerTitle => _selected is null ? "" : $"{_selected.Kanji}  {_selected.Label}";

    /// <summary>the chosen entry of <see cref="Arts"/>, for the list to highlight.</summary>
    public SecretaryArt? SelectedArt
    {
        get => Arts.FirstOrDefault(a => a.Key == _art);
        set
        {
            if (value is not null)
            {
                Art = value.Key;
            }
        }
    }

    /// <summary>the key of the secretary's picture, one of <see cref="Arts"/>.</summary>
    public string Art
    {
        get => _art;
        set
        {
            // the list clears its selection while it is refilled; that is not a choice
            if (!string.IsNullOrEmpty(value) && Set(ref _art, value))
            {
                Raise(nameof(SelectedArt));
                _ = SetSecretaryAsync(_secretaryId);
            }
        }
    }

    public bool LeftOfCentre
    {
        get => _leftOfCentre;
        set
        {
            if (Set(ref _leftOfCentre, value))
            {
                _ = SetSecretaryAsync(_secretaryId);
            }
        }
    }

    public TimeOfDay Time
    {
        get => _time;
        set
        {
            if (Set(ref _time, value))
            {
                _ = RefreshOutsideAsync();
            }
        }
    }

    /// <summary>pick from past seasonal CGs instead of ships.</summary>
    public bool SeasonalArchive
    {
        get => _seasonalArchive;
        set
        {
            if (Set(ref _seasonalArchive, value))
            {
                FilterShips();
            }
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                FilterShips();
            }
        }
    }

    public string PresetName
    {
        get => _presetName;
        set => Set(ref _presetName, value);
    }

    /// <summary>choosing a saved room loads it.</summary>
    public RoomPreset? Preset
    {
        get => _preset;
        set
        {
            if (Set(ref _preset, value) && value is not null)
            {
                PresetName = value.Name;
                _ = ApplyPresetAsync(value);
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool Busy => _pending > 0;

    public ICommand PickFurniture { get; }

    public ICommand PickShip { get; }

    public ICommand PickArt { get; }

    public ICommand Nudge { get; }

    public ICommand ResetLayer { get; }

    public ICommand ResetAll { get; }

    public ICommand Randomize { get; }

    public ICommand Export { get; }

    public ICommand SavePreset { get; }

    public ICommand DeletePreset { get; }

    private RoomComposer? Composer => AppServices.Current.Library is { } library ? new RoomComposer(library) : null;

    private RoomLayer Layer(string key) => Layers.First(l => l.Key == key);

    /// <summary>moves the selected layer by a drag on the canvas, in room pixels.</summary>
    public void DragBy(double dx, double dy)
    {
        if (_selected is null)
        {
            return;
        }

        _selected.OffsetX += (int)Math.Round(dx);
        _selected.OffsetY += (int)Math.Round(dy);
    }

    private async Task LoadAsync()
    {
        Raise(nameof(HasData));
        var library = AppServices.Current.Library;
        _slotTiles.Clear();
        if (library is null)
        {
            return;
        }

        foreach (var group in library.Start2.Furniture.Where(f => f.Title.Length > 0).GroupBy(f => (FurnitureSlot)f.Type))
        {
            // furniture with no pictures on the server never appears in game, so it is not offered
            _slotTiles[group.Key] = group.OrderBy(f => f.No)
                .Select(f => new EntityTile(f.Id, f.Title, $"No.{f.No}", f.Type,
                    library.Find("furniture", f.Id, "thumbnail") ?? library.Find("furniture", f.Id, "normal")))
                .Where(t => t.Picture is not null)
                .ToList();
        }

        _ships = library.Start2.Ships.Values
            .Where(s => s.SortNo > 0 && s.Name.Length > 0)
            .OrderBy(s => s.SortNo)
            .Select(s => new ShipTile(s, library.Names.Ship(s).English,
                library.Start2.ShipTypes.GetValueOrDefault(s.ShipType, ""), library.Find("ship", s.Id, "card")))
            .ToList();
        _seasonal = library.Catalog
            .Where(e => e.Group == "ship-seasonal" && e.AssetType == "character_full")
            .OrderBy(e => e.EntityId)
            .Select(e => new ShipTile(new KsttkDl.Core.MasterData.MstShip(e.EntityId, $"季節 {e.EntityId}", "-", 0),
                $"Seasonal CG {e.EntityId}", "", e))
            .ToList();
        FilterShips();

        // the starting room: each slot's first item, as a new admiral's office has
        if (_furniture.Count == 0)
        {
            var starter = new RoomPreset
            {
                Furniture = _slotTiles.ToDictionary(kv => kv.Key, kv => kv.Value.First().Id),
                SecretaryId = _ships.FirstOrDefault()?.Id ?? 0,
                Time = _time,
            };
            await ApplyPresetAsync(starter);
        }

        Selected ??= Layers[0];
    }

    private void ShowPicker()
    {
        if (SelectedSlot is { } slot && _slotTiles.TryGetValue(slot, out var tiles))
        {
            Gallery.SetItems(tiles);
        }
    }

    private void FilterShips()
    {
        var search = _search.Trim();
        ShipGallery.SetItems((_seasonalArchive ? _seasonal : _ships)
            .Where(t => search.Length == 0
                        || t.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || t.Ship.Name.Contains(search, StringComparison.Ordinal)
                        || t.Id.ToString() == search)
            .ToList());
    }

    private async Task SetFurnitureAsync(FurnitureSlot slot, int furnitureId)
    {
        var composer = Composer;
        if (composer is null)
        {
            return;
        }

        _furniture[slot] = furnitureId;
        var layer = Layer(slot.ToString());
        layer.Caption = AppServices.Current.Library!.Start2.Furniture.FirstOrDefault(f => f.Id == furnitureId)?.Title ?? "";

        await WithFetchAsync(composer.FurnitureEntries(furnitureId));
        layer.Sprites = await LoadSpritesAsync(composer.Furniture(furnitureId, _secretaryId));

        if (slot == FurnitureSlot.Window)
        {
            await RefreshOutsideAsync();
        }
    }

    private async Task SetSecretaryAsync(int shipId)
    {
        var composer = Composer;
        if (composer is null || shipId == 0)
        {
            return;
        }

        var library = AppServices.Current.Library!;
        var version = ++_secretaryVersion;
        _secretaryId = shipId;
        var layer = Layer("Secretary");
        layer.Caption = composer.IsSeasonalArchive(shipId) ? $"Seasonal CG {shipId}" : library.ShipName(shipId);

        // a choice the new ship does not have falls back to its first one before anything is fetched
        var choices = composer.SecretaryArts(shipId, _hasSpecialArt.Contains(shipId));
        if (choices.All(a => a.Key != _art))
        {
            _art = choices[0].Key;
            Raise(nameof(Art));
            Raise(nameof(SelectedArt));
        }

        if (composer.SecretaryEntry(shipId, _art) is { } entry)
        {
            await WithFetchAsync([entry]);

            // only some ships have special-remodel art; the 404 is remembered, so the choice disappears for them
            if (_art == RoomComposer.SpecialArt && library.IsMissing(entry))
            {
                AppServices.Current.Notify("No special-remodel art", $"{layer.Caption} has no special-remodel art, so the current CG is shown.");
                _art = RoomComposer.CurrentArt;
                Raise(nameof(Art));
                Raise(nameof(SelectedArt));
                if (composer.SecretaryEntry(shipId, _art) is { } current)
                {
                    await WithFetchAsync([current]);
                }
            }
        }

        // a later pick has taken over while this one was downloading
        if (version != _secretaryVersion)
        {
            return;
        }

        ReloadArts(composer, shipId);
        _ = CheckSpecialArtAsync(composer, shipId);

        // the umbrella stand shows the secretary's umbrellas
        foreach (var (slot, furnitureId) in _furniture.Where(kv => composer.DependsOnSecretary(kv.Value)).ToList())
        {
            Layer(slot.ToString()).Sprites = await LoadSpritesAsync(composer.Furniture(furnitureId, shipId));
        }

        var sprite = composer.Secretary(shipId, _art, _leftOfCentre);
        var sprites = await LoadSpritesAsync(sprite is null ? [] : [sprite]);
        if (version == _secretaryVersion)
        {
            layer.Sprites = sprites;
        }
    }

    /// <summary>asks the server whether the ship has special-remodel art, and lists it if so.</summary>
    private async Task CheckSpecialArtAsync(RoomComposer composer, int shipId)
    {
        var library = AppServices.Current.Library;
        if (library is null || AppServices.Current.Host is not { } host || _hasSpecialArt.Contains(shipId))
        {
            return;
        }

        if (await library.ExistsAsync(host, composer.SpecialEntry(shipId)) == true)
        {
            _hasSpecialArt.Add(shipId);
            if (shipId == _secretaryId)
            {
                ReloadArts(composer, shipId);
            }
        }
    }

    private void ReloadArts(RoomComposer composer, int shipId)
    {
        var arts = composer.SecretaryArts(shipId, _hasSpecialArt.Contains(shipId));
        if (arts.All(a => a.Key != _art))
        {
            _art = arts[0].Key;
            Raise(nameof(Art));
            Raise(nameof(SelectedArt));
        }

        if (!arts.SequenceEqual(Arts))
        {
            Arts.Clear();
            foreach (var art in arts)
            {
                Arts.Add(art);
            }

            // refilling cleared the list's selection
            Raise(nameof(Art));
            Raise(nameof(SelectedArt));
        }
    }

    private async Task RefreshPortUiAsync()
    {
        var library = AppServices.Current.Library;
        var layer = Layer("PortUi");
        if (library is null)
        {
            return;
        }

        var version = ++_portUiVersion;
        var ui = new PortUi(library);
        var fonts = new GameFonts(library);
        var first = !GameText.HasGameFont;
        await WithFetchAsync([.. ui.Entries(PortUi.Skin), .. fonts.Entries()]);
        if (first)
        {
            GameText.Load(fonts);
        }

        var now = DateTime.Now;
        _shownTime = now.ToString("HH:mm");
        var (sprites, texts) = ui.Compose(PortUi.Skin, PortUi.Values, now);
        var images = await LoadSpritesAsync(sprites);
        if (version != _portUiVersion)
        {
            return;
        }

        layer.Caption = PortUi.Skin switch
        {
            PortSkin.Classic => "Skin 1, classic",
            PortSkin.Skin1 => "Skin 1",
            PortSkin.Skin2 => "Skin 2",
            PortSkin.Skin3 => "Skin 3",
            _ => "Skin 3 改",
        };
        layer.Sprites = [.. images, .. texts.Select(GameText.Render).OfType<SpriteImage>()];
    }

    private async Task RefreshOutsideAsync()
    {
        var composer = Composer;
        var layer = Layer("Outside");
        if (composer is null || !_furniture.TryGetValue(FurnitureSlot.Window, out var windowId))
        {
            layer.Sprites = [];
            return;
        }

        var window = AppServices.Current.Library!.Start2.Furniture.FirstOrDefault(f => f.Id == windowId);
        if (window is not null && composer.OutsideEntry(window.OutsideId, _time) is { } entry)
        {
            await WithFetchAsync([entry]);
        }

        layer.Caption = _time.ToString();
        var sprite = composer.Outside(windowId, _time);
        layer.Sprites = await LoadSpritesAsync(sprite is null ? [] : [sprite]);
    }

    private async Task WithFetchAsync(IReadOnlyCollection<KsttkDl.Core.Catalog.AssetEntry> entries)
    {
        _pending++;
        Raise(nameof(Busy));
        Status = "Downloading…";
        try
        {
            await AppServices.Current.Fetcher.FetchNowAsync(entries);
            Status = "";
        }
        catch (Exception ex)
        {
            Status = "Download failed: " + ex.Message;
        }
        finally
        {
            _pending--;
            Raise(nameof(Busy));
        }
    }

    private static Task<IReadOnlyList<SpriteImage>> LoadSpritesAsync(IReadOnlyList<RoomSprite> sprites) =>
        Task.Run<IReadOnlyList<SpriteImage>>(() => sprites.Select(ToImage).OfType<SpriteImage>().ToList());

    private static SpriteImage? ToImage(RoomSprite sprite)
    {
        if (!Bitmaps.TryGetValue(sprite.Path, out var bitmap))
        {
            if (Controls.ImageLoader.Decode(sprite.Path, 0) is not { } decoded)
            {
                return null;
            }

            bitmap = decoded;
            Bitmaps[sprite.Path] = bitmap;
        }

        BitmapSource image = bitmap;
        if (sprite.Source is { } rect)
        {
            var clipped = new Int32Rect(rect.X, rect.Y,
                Math.Min(rect.Width, bitmap.PixelWidth - rect.X), Math.Min(rect.Height, bitmap.PixelHeight - rect.Y));
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                return null;
            }

            var cropped = new CroppedBitmap(bitmap, clipped);
            cropped.Freeze();
            image = cropped;
        }

        // atlas pngs carry no dpi to trust, so sizes are taken in pixels
        return new SpriteImage(image, sprite.X, sprite.Y, image.PixelWidth * sprite.Scale, image.PixelHeight * sprite.Scale,
            sprite.Opacity);
    }

    private async Task ApplyPresetAsync(RoomPreset preset)
    {
        _time = preset.Time;
        Raise(nameof(Time));
        _art = preset.SecretaryArt ?? (preset.Damaged ? RoomComposer.DamagedArt : RoomComposer.CurrentArt);
        _leftOfCentre = preset.LeftOfCentre;
        Raise(nameof(Art));
        Raise(nameof(SelectedArt));
        Raise(nameof(LeftOfCentre));

        foreach (var layer in Layers)
        {
            layer.Apply(preset.Layers.GetValueOrDefault(layer.Key));
        }

        _furniture.Clear();
        foreach (var slot in Enum.GetValues<FurnitureSlot>())
        {
            if (preset.Furniture.TryGetValue(slot, out var id))
            {
                await SetFurnitureAsync(slot, id);
            }
            else
            {
                Layer(slot.ToString()).Sprites = [];
            }
        }

        await RefreshOutsideAsync();
        await SetSecretaryAsync(preset.SecretaryId);

        // applying redraws the interface through its Changed event
        PortUi.Apply(preset.PortUi);
    }

    private RoomPreset Current(string name) => new()
    {
        Name = name,
        Furniture = new Dictionary<FurnitureSlot, int>(_furniture),
        SecretaryId = _secretaryId,
        Damaged = _art == RoomComposer.DamagedArt,
        SecretaryArt = _art,
        LeftOfCentre = _leftOfCentre,
        PortUi = PortUi.Settings,
        Time = _time,
        Layers = Layers.Where(l => l.IsMoved || !l.Visible || l.Opacity < 1).ToDictionary(l => l.Key, l => l.Tweak),
    };

    private void SaveCurrent()
    {
        var name = _presetName.Trim();
        _presets.Save(Current(name));
        ReloadPresets();
        _preset = Presets.FirstOrDefault(p => p.Name == name);
        Raise(nameof(Preset));
        AppServices.Current.Notify("Room saved", name, ToastKind.Success);
    }

    private void ReloadPresets()
    {
        Presets.Clear();
        foreach (var preset in _presets.Load())
        {
            Presets.Add(preset);
        }
    }

    private async Task RandomizeAsync()
    {
        var random = Random.Shared;
        var preset = new RoomPreset
        {
            Furniture = _slotTiles.ToDictionary(kv => kv.Key, kv => kv.Value[random.Next(kv.Value.Count)].Id),
            SecretaryId = _ships.Count == 0 ? 0 : _ships[random.Next(_ships.Count)].Id,
            Time = (TimeOfDay)random.Next(1, 6),
            LeftOfCentre = _leftOfCentre,
            PortUi = PortUi.Settings,
            Layers = Layers.Where(l => !l.Visible).ToDictionary(l => l.Key, _ => new LayerTweak { Hidden = true }),
        };
        await ApplyPresetAsync(preset);
    }

    /// <summary>renders the visible layers at 1200×720 times <paramref name="scale"/> and saves a png.</summary>
    private async Task ExportAsync(int scale)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image|*.png",
            FileName = $"room-{DateTime.Now:yyyyMMdd-HHmmss}{(scale > 1 ? "@2x" : "")}.png",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(scale, scale));
            context.PushClip(new RectangleGeometry(new Rect(0, 0, RoomComposer.Width, RoomComposer.Height)));
            foreach (var layer in Layers.Where(l => l.Visible && l.Opacity > 0))
            {
                // as on the canvas: sized around the layer's centre, then moved
                context.PushOpacity(layer.Opacity);
                context.PushTransform(new TranslateTransform(layer.OffsetX, layer.OffsetY));
                context.PushTransform(new ScaleTransform(layer.Scale, layer.Scale, layer.CentreX, layer.CentreY));
                foreach (var sprite in layer.Sprites)
                {
                    context.PushOpacity(sprite.Opacity);
                    context.DrawImage(sprite.Image, new Rect(sprite.X, sprite.Y, sprite.Width, sprite.Height));
                    context.Pop();
                }

                context.Pop();
                context.Pop();
                context.Pop();
            }

            context.Pop();
            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(RoomComposer.Width * scale, RoomComposer.Height * scale, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        // the encoder belongs to the ui thread, so it writes to memory there and only the file write moves off it
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        var path = dialog.FileName;
        var bytes = buffer.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        AppServices.Current.Notify("Room exported", path, ToastKind.Success);
    }
}
