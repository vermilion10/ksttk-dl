using KsttkDl.Core.Catalog;
using KsttkDl.Core.Download;
using KsttkDl.Core.MasterData;

namespace KsttkDl.Core;

/// <summary> Ties the on-disk layout together: the <c>data/</c> cache, the <c>output/raw/</c> mirror hat download writes and extract reads, and the organized tree beside it. Both commands go through here so they agree on where things live and build the catalog the same way. </summary>
public sealed class Workspace(string dataDirectory, string outputDirectory)
{
    /// <summary>Where <c>kcs2/version.json</c> lands in the raw mirror.</summary>
    private const string VersionPath = "kcs2/version.json";

    public DataStore Data { get; } = new(dataDirectory);

    public string OutputRoot { get; } = outputDirectory;

    public string RawRoot { get; } = Path.Combine(outputDirectory, "raw");

    /// <summary>older copies of ship CGs that a newer version replaced, laid out like the raw mirror.</summary>
    public string HistoryRoot { get; } = Path.Combine(outputDirectory, "history");

    /// <summary> Resolves a world number or explicit hostname against servers.json. An unknown hostname passes through with empty metadata rather than being rejected, in case a new world appears before the translation data catches up.  </summary>
    public WorldServer ResolveServer(Translations translations, int? world, string? host)
    {
        if (host is { Length: > 0 })
        {
            return translations.Servers.FirstOrDefault(
                       s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase))
                   ?? new WorldServer(0, host, host, "", "");
        }

        if (world is null)
        {
            throw new InvalidOperationException("Specify a world with --world, or a hostname with --host.");
        }

        return translations.Servers.FirstOrDefault(s => s.Num == world)
               ?? throw new InvalidOperationException(
                   $"No world {world} in servers.json. Known worlds: " +
                   string.Join(", ", translations.Servers.Select(s => s.Num)));
    }

    /// <summary> Fetches the live version manifest and mirrors it into the raw tree, so a later extract can rebuild the same catalog offline. </summary>
    public async Task<VersionInfo> FetchVersionsAsync(string host, CancellationToken cancellationToken)
    {
        var url = $"https://{host}/{VersionPath}";
        var json = await CdnHttpClient.Instance.GetStringAsync(url, cancellationToken);

        var destination = Path.Combine(RawRoot, VersionPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, json, cancellationToken);

        return VersionInfo.Parse(json);
    }

    /// <summary> The manifest the last download saved. Without one, versions fall back to empty, which only affects the <c>?version=</c> query - every downloaded file is still found by path. </summary>
    public VersionInfo LoadCachedVersions()
    {
        var path = Path.Combine(RawRoot, VersionPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? VersionInfo.Parse(File.ReadAllText(path)) : VersionInfo.Empty;
    }

    /// <summary> Loads master data and translations, then enumerates the catalog. Reports a readable error rather than a parse failure when refresh-data has not been run. </summary>
    public (Start2 Start2, Translations Translations, IReadOnlyList<AssetEntry> Catalog) BuildCatalog(
        IReadOnlySet<AssetCategory> categories,
        VersionInfo versions,
        bool includeRare)
    {
        if (!Data.IsPopulated)
        {
            throw new InvalidOperationException(
                $"Missing data files in {Data.Root}: {string.Join(", ", Data.MissingFiles)}. " +
                "Run 'ksttk-dl refresh-data' first.");
        }

        var start2 = Data.LoadStart2();
        var translations = Data.LoadTranslations();
        ShipGraphHistory.Load(Data).Remember(start2);
        StaticAssets.Load(Data);

        var catalog = new CatalogBuilder(start2, translations, versions, includeRare).Build(categories);
        return (start2, translations, catalog);
    }
}
