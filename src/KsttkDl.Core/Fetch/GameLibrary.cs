using KsttkDl.Core.Catalog;
using KsttkDl.Core.Download;
using KsttkDl.Core.MasterData;
using KsttkDl.Core.Naming;

namespace KsttkDl.Core.Fetch;

/// <summary> the whole catalog, loaded once and looked up per entity, so a single ship, item or map can be fetched on its own. files land in the same raw mirror and download index that a full <c>download</c> uses, which keeps the two interchangeable: anything fetched here is skipped by a later bulk run, and <c>extract</c> organizes it like everything else </summary>
public sealed class GameLibrary
{
    private readonly ILookup<(string Group, int Id), AssetEntry> _byEntity;
    private readonly DownloadIndex _index;

    private GameLibrary(Workspace workspace, Start2 start2, Translations translations, IReadOnlyList<AssetEntry> catalog)
    {
        Workspace = workspace;
        Start2 = start2;
        Translations = translations;
        Names = new NameResolver(translations);
        Catalog = catalog;
        _byEntity = catalog.ToLookup(e => (e.Group, e.EntityId));
        _index = DownloadIndex.Load(workspace.RawRoot);
    }

    public Workspace Workspace { get; }

    public Start2 Start2 { get; }

    public Translations Translations { get; }

    public NameResolver Names { get; }

    public IReadOnlyList<AssetEntry> Catalog { get; }

    /// <summary> builds the full catalog from the cached master data and the version manifest the last download saved. without a saved manifest the <c>?version=</c> queries are empty, which only matters for staleness checks </summary>
    public static GameLibrary Load(Workspace workspace, bool includeRare, VersionInfo? versions = null)
    {
        var all = Enum.GetValues<AssetCategory>().ToHashSet();
        var (start2, translations, catalog) = workspace.BuildCatalog(all, versions ?? workspace.LoadCachedVersions(), includeRare);
        return new GameLibrary(workspace, start2, translations, catalog);
    }

    public IReadOnlyList<AssetEntry> EntriesFor(string group, int entityId) => _byEntity[(group, entityId)].ToList();

    /// <summary>a ship's pictures; seasonal cgs live in their own group.</summary>
    public IReadOnlyList<AssetEntry> ShipImages(int shipId) =>
        _byEntity[("ship", shipId)].Concat(_byEntity[("ship-seasonal", shipId)]).ToList();

    public IReadOnlyList<AssetEntry> ShipVoices(int shipId) =>
        _byEntity[("voice", shipId)].OrderBy(e => e.SecondaryId).ToList();

    public IReadOnlyList<AssetEntry> MapEntries(int area, int no) =>
        _byEntity[("map", area)].Concat(_byEntity[("gauge", area)]).Where(e => e.SecondaryId == no).ToList();

    public AssetEntry? Find(string group, int entityId, string assetType) =>
        _byEntity[(group, entityId)].FirstOrDefault(e => e.AssetType == assetType);

    /// <summary>where the entry sits in the raw mirror, or null when it has not been downloaded.</summary>
    public string? LocalPath(AssetEntry entry)
    {
        var path = Path.Combine(Workspace.RawRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    /// <summary>true when the cdn answered 404 for this entry: the asset does not exist for this entity.</summary>
    public bool IsMissing(AssetEntry entry) => _index.TryGet(entry.Path, out var record) && record.Missing;

    public bool IsDone(AssetEntry entry) => LocalPath(entry) is not null || IsMissing(entry);

    /// <summary>downloads whichever of <paramref name="entries"/> are not in the mirror yet.</summary>
    public async Task<DownloadStats> FetchAsync(
        string host,
        IReadOnlyCollection<AssetEntry> entries,
        Action<string> log,
        IProgress<DownloadStats>? progress = null,
        int concurrency = 6,
        CancellationToken cancellationToken = default)
    {
        var pending = entries.Where(e => !IsDone(e)).ToList();
        if (pending.Count == 0)
        {
            return new DownloadStats { Skipped = entries.Count };
        }

        Directory.CreateDirectory(Workspace.RawRoot);
        var downloader = new Downloader(new DownloadOptions(host, Workspace.RawRoot, concurrency, HistoryRoot: Workspace.HistoryRoot), _index, log);
        var stats = await downloader.RunAsync(pending, progress, cancellationToken);
        return stats with { Skipped = stats.Skipped + entries.Count - pending.Count };
    }

    /// <summary>
    /// whether the cdn has the file, asked with a HEAD request so nothing is downloaded. a 404 is recorded like
    /// one found while downloading; null when the server could not be asked
    /// </summary>
    public async Task<bool?> ExistsAsync(string host, AssetEntry entry, CancellationToken cancellationToken = default)
    {
        if (LocalPath(entry) is not null)
        {
            return true;
        }

        if (IsMissing(entry))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, entry.Url(host));
            using var response = await CdnHttpClient.Instance.SendAsync(request, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                _index.Record(entry.Path, new IndexRecord { Version = entry.VersionQuery, Missing = true });
                _index.Save();
                return false;
            }

            return response.IsSuccessStatusCode ? true : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>English name when there is one, the Japanese name otherwise.</summary>
    public string ShipName(int shipId) =>
        Start2.Ships.TryGetValue(shipId, out var ship) ? Names.Ship(ship).English : "";
}
