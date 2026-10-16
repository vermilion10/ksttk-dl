using KsttkDl.Core.Catalog;
using KsttkDl.Core.Fetch;

namespace KsttkDl.Core.Fonts;

/// <summary>a game font unpacked to an OpenType file, ready for the desktop to load by family name.</summary>
public sealed record GameFont(string Path, string Family);

/// <summary>
/// the game's text face, font_j: A-OTF UD Shin Go Pro, Light for normal text and Regular for bold. the client
/// ships it as WOFF2, so the copies in the raw mirror are unpacked once into a cache beside it
/// </summary>
public sealed class GameFonts(GameLibrary library)
{
    public const string LightPath = "kcs2/resources/font/A-OTF-UDShinGoPro-Light.woff2";
    public const string RegularPath = "kcs2/resources/font/A-OTF-UDShinGoPro-Regular.woff2";

    private string CacheRoot => Path.Combine(library.Workspace.OutputRoot, ".cache", "fonts");

    /// <summary>the files to download before <see cref="Load"/> can succeed.</summary>
    public IReadOnlyList<AssetEntry> Entries() =>
        new[] { LightPath, RegularPath }.Select(EntryFor).ToList();

    /// <summary>the unpacked font, or null while its woff2 is not in the mirror or cannot be read.</summary>
    public GameFont? Load(bool bold)
    {
        var entry = EntryFor(bold ? RegularPath : LightPath);
        if (library.LocalPath(entry) is not { } source)
        {
            return null;
        }

        var target = Path.Combine(CacheRoot, Path.GetFileNameWithoutExtension(source) + ".otf");
        try
        {
            byte[] sfnt;
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source))
            {
                sfnt = File.ReadAllBytes(target);
            }
            else
            {
                sfnt = Woff2.ToSfnt(File.ReadAllBytes(source));
                Directory.CreateDirectory(CacheRoot);
                File.WriteAllBytes(target, sfnt);
            }

            return Woff2.FamilyName(sfnt) is { } family ? new GameFont(target, family) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // the fonts are fixed static urls, so they are in the catalog whenever the ui category is
    private AssetEntry EntryFor(string path) =>
        library.Catalog.FirstOrDefault(e => e.Path == path)
        ?? new AssetEntry { Path = path, Category = AssetCategory.Ui, Group = "static", AssetType = Path.GetFileNameWithoutExtension(path) };
}
