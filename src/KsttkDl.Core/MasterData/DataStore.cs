namespace KsttkDl.Core.MasterData;

public sealed record DataSource(string FileName, string Url, string Description);

/// <summary>the local <c>data/</c> cache of upstream master data and translations. these are community-maintained and change as the game updates, so nothing is baked into the binary: <c>refresh-data</c> pulls fresh copies and everything else reads from here </summary>
public sealed class DataStore(string root)
{
    public const string Start2File = "start2.json";
    public const string ShipsFile = "ships.json";
    public const string ItemsFile = "items.json";
    public const string ShipAffixFile = "ship_affix.json";
    public const string QuotesFile = "quotes.json";
    public const string ServersFile = "servers.json";
    public const string StaticUrlsFile = "static_urls.json";
    public const string StaticAssetsFile = "static_assets.json";

    private const string Kc3 = "https://raw.githubusercontent.com/KC3Kai/kc3-translations/master/data/en";
    private const string KcCacheProxy = "https://raw.githubusercontent.com/Tibowl/KCCacheProxy/master/src/proxy/preloader";

    public static readonly IReadOnlyList<DataSource> Sources =
    [
        new(Start2File, "https://raw.githubusercontent.com/Tibowl/api_start2/master/start2.json",
            "game master data (ships, equipment, maps, BGM, furniture)"),
        new(ShipsFile, $"{Kc3}/ships.json", "Japanese -> English ship names"),
        new(ItemsFile, $"{Kc3}/items.json", "Japanese -> English equipment names"),
        new(ShipAffixFile, $"{Kc3}/ship_affix.json", "ship name prefixes/suffixes (Kai, seasonal, ...)"),
        new(QuotesFile, $"{Kc3}/quotes.json", "voice line translations"),
        new(ServersFile, $"{Kc3}/servers.json", "world number -> CDN hostname"),
        // static-asset catalogs from the reference implementation rather than game data
        new(StaticUrlsFile, $"{KcCacheProxy}/urls.json", "fixed-path static assets"),
        new(StaticAssetsFile, $"{KcCacheProxy}/assets.json", "UI texture atlases per category"),
    ];

    public string Root { get; } = root;

    public string PathFor(string fileName) => Path.Combine(Root, fileName);

    public bool IsPopulated => Sources.All(s => File.Exists(PathFor(s.FileName)));

    public IEnumerable<string> MissingFiles =>
        Sources.Where(s => !File.Exists(PathFor(s.FileName))).Select(s => s.FileName);

    public Start2 LoadStart2() => Start2.Load(PathFor(Start2File));

    public Translations LoadTranslations() => Translations.Load(this);
}
