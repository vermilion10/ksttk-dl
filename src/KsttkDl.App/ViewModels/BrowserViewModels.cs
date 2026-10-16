using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KsttkDl.App.Infrastructure;
using KsttkDl.Core.Catalog;
using KsttkDl.Core.Room;

namespace KsttkDl.App.ViewModels;

/// <summary>one section of a raw browser, e.g. an atlas category or a kind of sound.</summary>
public sealed record BrowserSection(string Key, string Title, string Kanji);

/// <summary>a file in the raw mirror, shown by its cdn path.</summary>
public abstract class RawItem(AssetEntry entry, string title) : Observable
{
    public AssetEntry Entry { get; } = entry;

    public string Title { get; } = title;

    /// <summary>the path on the cdn, which is also its path under output/raw.</summary>
    public string RawPath => Entry.Path;

    public string FileName => Path.GetFileName(Entry.Path);

    public bool IsLocal => AppServices.Current.Library?.LocalPath(Entry) is not null;

    public bool IsMissing => AppServices.Current.Library?.IsMissing(Entry) == true;

    /// <summary>済 once downloaded, 欠 when the cdn has no such file, 未 otherwise.</summary>
    public string Stamp => IsLocal ? "済" : IsMissing ? "欠" : "未";

    public void Refresh()
    {
        Raise(nameof(IsLocal));
        Raise(nameof(IsMissing));
        Raise(nameof(Stamp));
    }
}

/// <summary>shared parts of the UI and Audio pages: sections, a path filter, and the file actions.</summary>
public abstract class RawBrowserViewModel<T> : Observable where T : RawItem
{
    private BrowserSection? _section;
    private string _search = "";
    private string _status = "";
    private List<T> _all = [];

    protected RawBrowserViewModel()
    {
        OpenFolder = new RelayCommand(p => Reveal(p as RawItem), p => p is RawItem item && item.IsLocal);
        CopyPath = new RelayCommand(p =>
        {
            if (p is RawItem item)
            {
                Clipboard.SetText(item.RawPath);
                AppServices.Current.Notify("Path copied", item.RawPath);
            }
        }, p => p is RawItem);
        DownloadSection = new AsyncCommand(DownloadSectionAsync, () => _all.Count > 0);
        AppServices.Current.LibraryChanged += (_, _) => Reload();
    }

    public ObservableCollection<BrowserSection> Sections { get; } = [];

    public ObservableCollection<T> Items { get; } = [];

    public bool HasData => AppServices.Current.Library is not null;

    public BrowserSection? Section
    {
        get => _section;
        set
        {
            if (Set(ref _section, value))
            {
                _all = value is null ? [] : Build(value);
                Filter();
            }
        }
    }

    /// <summary>matches the title or the raw path.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                Filter();
            }
        }
    }

    public string Status
    {
        get => _status;
        protected set => Set(ref _status, value);
    }

    public ICommand OpenFolder { get; }

    public ICommand CopyPath { get; }

    public ICommand DownloadSection { get; }

    protected abstract IEnumerable<BrowserSection> BuildSections();

    protected abstract List<T> Build(BrowserSection section);

    protected void Reload()
    {
        Raise(nameof(HasData));
        var key = _section?.Key;
        Sections.Clear();
        if (AppServices.Current.Library is null)
        {
            Items.Clear();
            return;
        }

        foreach (var section in BuildSections())
        {
            Sections.Add(section);
        }

        _section = null;
        Section = Sections.FirstOrDefault(s => s.Key == key) ?? Sections.FirstOrDefault();
    }

    private void Filter()
    {
        var search = _search.Trim();
        Items.Clear();
        foreach (var item in _all.Where(i => search.Length == 0
                                             || i.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                                             || i.RawPath.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            Items.Add(item);
        }

        var local = _all.Count(i => i.IsLocal);
        Status = $"{Items.Count} shown · {local} of {_all.Count} downloaded";
    }

    private async Task DownloadSectionAsync()
    {
        var items = _all.ToList();
        Status = $"Downloading {items.Count(i => !i.IsLocal)} files…";
        try
        {
            await AppServices.Current.Fetcher.FetchNowAsync(items.Select(i => i.Entry).ToList());
        }
        catch (Exception ex)
        {
            AppServices.Current.Notify("Download failed", ex.Message, ToastKind.Error);
        }

        foreach (var item in items)
        {
            item.Refresh();
        }

        Filter();
    }

    protected static void Reveal(RawItem? item)
    {
        if (item is not null && AppServices.Current.Library?.LocalPath(item.Entry) is { } path)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
    }
}

/// <summary>a sprite sheet (json and png) or a single picture.</summary>
public sealed class UiItem(AssetEntry entry, string title, AssetEntry? frames) : RawItem(entry, title)
{
    /// <summary>the frame table of a sprite sheet; null for a single picture.</summary>
    public AssetEntry? Frames { get; } = frames;

    public string Kind => Frames is null ? "image" : "sheet";
}

/// <summary>one named frame cut from a sprite sheet.</summary>
public sealed record FrameTile(string Name, ImageSource Image, int Width, int Height)
{
    public string Size => $"{Width} × {Height}";
}

/// <summary>
/// the game's interface pictures by screen: every sprite sheet and fixed image the client loads, with their raw
/// paths. picking a sheet downloads it and cuts it into its named frames
/// </summary>
public sealed class UiViewModel : RawBrowserViewModel<UiItem>
{
    private UiItem? _selected;
    private string? _imagePath;

    public UiViewModel() => Reload();

    public UiItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                _ = ShowAsync(value);
            }
        }
    }

    /// <summary>the whole sheet or picture, once downloaded.</summary>
    public string? ImagePath
    {
        get => _imagePath;
        private set => Set(ref _imagePath, value);
    }

    public ObservableCollection<FrameTile> Frames { get; } = [];

    protected override IEnumerable<BrowserSection> BuildSections()
    {
        var catalog = AppServices.Current.Library!.Catalog;
        foreach (var category in catalog.Where(e => e.Group == "atlas").Select(e => e.AssetType).Distinct())
        {
            yield return new BrowserSection(category, category, "");
        }

        yield return new BrowserSection("static", "fixed images", "");
        yield return new BrowserSection("useitem", "use items", "");
    }

    protected override List<UiItem> Build(BrowserSection section)
    {
        var catalog = AppServices.Current.Library!.Catalog;
        if (section.Key is "static" or "useitem")
        {
            return catalog.Where(e => e.Group == section.Key && e.Path.EndsWith(".png", StringComparison.Ordinal))
                .Select(e => new UiItem(e, Path.GetFileNameWithoutExtension(e.Path), null))
                .ToList();
        }

        // a sheet is a png with a json of the same name beside it
        var files = catalog.Where(e => e.Group == "atlas" && e.AssetType == section.Key).ToList();
        var json = files.Where(e => e.Path.EndsWith(".json", StringComparison.Ordinal))
            .ToDictionary(e => e.Path[..^5], StringComparer.Ordinal);
        return files.Where(e => e.Path.EndsWith(".png", StringComparison.Ordinal))
            .Select(e => new UiItem(e, Path.GetFileNameWithoutExtension(e.Path), json.GetValueOrDefault(e.Path[..^4])))
            .ToList();
    }

    private async Task ShowAsync(UiItem? item)
    {
        Frames.Clear();
        ImagePath = null;
        var library = AppServices.Current.Library;
        if (item is null || library is null)
        {
            return;
        }

        var entries = item.Frames is null ? new[] { item.Entry } : [item.Entry, item.Frames];
        if (entries.Any(e => library.LocalPath(e) is null && !library.IsMissing(e)))
        {
            Status = "Downloading " + item.FileName + "…";
            try
            {
                await AppServices.Current.Fetcher.FetchNowAsync(entries);
            }
            catch (Exception ex)
            {
                Status = "Download failed: " + ex.Message;
                return;
            }

            item.Refresh();
        }

        if (!ReferenceEquals(item, _selected))
        {
            return;
        }

        var png = library.LocalPath(item.Entry);
        ImagePath = png;
        Status = png is null ? $"{item.RawPath} is not on the server" : item.RawPath;
        if (png is null || item.Frames is null || library.LocalPath(item.Frames) is not { } jsonPath)
        {
            return;
        }

        var tiles = await Task.Run(() => CutFrames(jsonPath, png));
        if (ReferenceEquals(item, _selected))
        {
            foreach (var tile in tiles)
            {
                Frames.Add(tile);
            }
        }
    }

    private static List<FrameTile> CutFrames(string jsonPath, string pngPath)
    {
        if (Atlas.Load(jsonPath, pngPath) is not { } atlas || Controls.ImageLoader.Decode(pngPath, 0) is not { } sheet)
        {
            return [];
        }

        var tiles = new List<FrameTile>();

        // frames sort by their trailing number, as the client indexes them
        foreach (var frame in atlas.Frames.Values.OrderBy(f => f.Name, NaturalOrder.Instance))
        {
            var rect = new Int32Rect(frame.Frame.X, frame.Frame.Y,
                Math.Min(frame.Frame.Width, sheet.PixelWidth - frame.Frame.X),
                Math.Min(frame.Frame.Height, sheet.PixelHeight - frame.Frame.Y));
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            var cropped = new CroppedBitmap(sheet, rect);
            cropped.Freeze();
            tiles.Add(new FrameTile(frame.Name, cropped, frame.SourceWidth, frame.SourceHeight));
        }

        return tiles;
    }

    /// <summary>"port_main_10" after "port_main_9".</summary>
    private sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var (xs, xn) = Split(x ?? "");
            var (ys, yn) = Split(y ?? "");
            var c = string.CompareOrdinal(xs, ys);
            return c != 0 ? c : xn.CompareTo(yn);
        }

        private static (string, long) Split(string s)
        {
            var i = s.Length;
            while (i > 0 && char.IsAsciiDigit(s[i - 1]))
            {
                i--;
            }

            return i < s.Length && long.TryParse(s[i..], out var n) ? (s[..i], n) : (s, -1);
        }
    }
}

/// <summary>a piece of music, a sound effect or a line, playable in place.</summary>
public sealed class AudioItem(AssetEntry entry, string title, string detail) : RawItem(entry, title)
{
    private bool _playing;

    public string Detail { get; } = detail;

    public bool Playing
    {
        get => _playing;
        set => Set(ref _playing, value);
    }
}

/// <summary>
/// the game's sound: port and battle music, fanfares, sound effects, title calls and the non-ship voices, each
/// with its raw path. playing a file downloads it first
/// </summary>
public sealed class AudioViewModel : RawBrowserViewModel<AudioItem>, IDisposable
{
    private readonly MediaPlayer _player = new();
    private AudioItem? _playing;

    public AudioViewModel()
    {
        Play = new AsyncCommand(p => PlayAsync((AudioItem)p!));
        _player.MediaEnded += (_, _) => StopPlaying();
        Reload();
    }

    public ICommand Play { get; }

    protected override IEnumerable<BrowserSection> BuildSections() =>
    [
        new("bgm:port", "Port music", "母港"),
        new("bgm:battle", "Battle music", "戦闘"),
        new("bgm:fanfare", "Fanfares", "凱歌"),
        new("se", "Sound effects", "効果"),
        new("titlecall", "Title calls", "題名"),
        new("npc", "NPC lines", "任務"),
        new("event", "Event lines", "行事"),
        new("abyssal", "Abyssal lines", "深海"),
    ];

    protected override List<AudioItem> Build(BrowserSection section)
    {
        var library = AppServices.Current.Library!;
        var parts = section.Key.Split(':');
        var group = parts[0];
        var kind = parts.ElementAtOrDefault(1);
        var portNames = library.Start2.Bgms.ToDictionary(b => b.Id, b => b.Name);
        var quotes = library.Translations.Quotes.GetValueOrDefault(group);

        return library.Catalog
            .Where(e => e.Group == group && (kind is null || e.AssetType == kind))
            .OrderBy(e => e.SecondaryId).ThenBy(e => e.EntityId).ThenBy(e => e.AssetType, StringComparer.Ordinal)
            .Select(e => group switch
            {
                "bgm" when kind == "port" && portNames.TryGetValue(e.EntityId, out var name) && name.Length > 0 =>
                    new AudioItem(e, name, $"No. {e.EntityId}"),
                "bgm" => new AudioItem(e, $"{Capitalized(kind)} {e.EntityId}", $"No. {e.EntityId}"),
                "se" => new AudioItem(e, $"Sound effect {e.EntityId}", ""),
                "titlecall" => new AudioItem(e, $"Title call {e.SecondaryId}-{e.EntityId:D3}", ""),
                _ => new AudioItem(e, e.AssetType, Clean(quotes?.GetValueOrDefault(e.AssetType))),
            })
            .ToList();
    }

    private static string Capitalized(string? s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Clean(string? quote) =>
        (quote ?? "").Replace("<br />", " ").Replace("<br/>", " ").Replace("<br>", " ");

    private async Task PlayAsync(AudioItem item)
    {
        var library = AppServices.Current.Library;
        if (library is null)
        {
            return;
        }

        if (item.Playing)
        {
            _player.Stop();
            StopPlaying();
            return;
        }

        var path = library.LocalPath(item.Entry);
        if (path is null)
        {
            await AppServices.Current.Fetcher.FetchNowAsync([item.Entry]);
            item.Refresh();
            path = library.LocalPath(item.Entry);
        }

        if (path is null)
        {
            AppServices.Current.Notify("No audio", $"{item.RawPath} is not on the server.", ToastKind.Error);
            return;
        }

        StopPlaying();
        _player.Open(new Uri(path));
        _player.Play();
        _playing = item;
        item.Playing = true;
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
