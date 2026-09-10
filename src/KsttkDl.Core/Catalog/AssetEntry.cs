namespace KsttkDl.Core.Catalog;

public enum AssetCategory
{
    Ships,
    Equipment,
    Maps,
    Audio,
    Ui,
    Furniture,
}

/// <summary> one expected asset, carrying enough master-data context for extract to name it without reverse-parsing the hashed filename. both download and extract regenerate the catalog deterministically, which is what keeps the two steps decoupled </summary>
public sealed record AssetEntry
{
    /// <summary>cdn path with no query string. doubles as the path under output/raw/.</summary>
    public required string Path { get; init; }

    /// <summary> the <c>?version=...</c> the game appends. part of the URL but not the cached path, so it doubles as the staleness marker in the download index </summary>
    public string VersionQuery { get; init; } = "";

    public required AssetCategory Category { get; init; }

    /// <summary>sub-kind within the category, e.g. "ship", "voice", "bgm", "atlas".</summary>
    public required string Group { get; init; }

    /// <summary>ship id, equipment id, BGM id, map area ' whatever the group keys on.</summary>
    public int EntityId { get; init; }

    /// <summary>second key where one id is not enough, e.g. the map number within an area.</summary>
    public int SecondaryId { get; init; }

    /// <summary>asset type as the game names it ("full", "banner_dmg", "Library").</summary>
    public string AssetType { get; init; } = "";

    public string Url(string host) => $"https://{host}/{Path}{VersionQuery}";
}
