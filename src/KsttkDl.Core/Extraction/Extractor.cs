using KsttkDl.Core.Catalog;
using KsttkDl.Core.MasterData;
using KsttkDl.Core.Naming;

namespace KsttkDl.Core.Extraction;

/// <summary>turns the raw mirror into the organized output tree. runs entirely offline against files already on disk, so fixing a naming bug and re-extracting never means re-downloading. </summary>
public sealed class Extractor(
    ExtractionContext context,
    Start2 start2,
    Translations translations)
{
    public ExtractStats Extract(IReadOnlyList<AssetEntry> catalog, IReadOnlySet<AssetCategory> categories)
    {
        var names = new NameResolver(translations);
        var entities = new EntityExtractor(context, start2, names);
        var stats = new ExtractStats();

        if (categories.Contains(AssetCategory.Ships))
        {
            context.Log("Extracting ships...");
            stats += entities.ExtractShips(catalog);
        }

        if (categories.Contains(AssetCategory.Equipment))
        {
            context.Log("Extracting equipment...");
            stats += entities.ExtractEquipment(catalog);
        }

        if (categories.Contains(AssetCategory.Furniture))
        {
            context.Log("Extracting furniture...");
            stats += entities.ExtractFurniture(catalog);
        }

        if (categories.Contains(AssetCategory.Maps))
        {
            context.Log("Extracting maps...");
            stats += new MapExtractor(context, start2).Extract(catalog);
        }

        if (categories.Contains(AssetCategory.Audio))
        {
            context.Log("Extracting audio...");
            stats += new AudioExtractor(context, start2, translations, names).Extract(catalog);
        }

        if (categories.Contains(AssetCategory.Ui))
        {
            context.Log("Extracting UI...");
            stats += new UiExtractor(context, start2).Extract(catalog);
        }

        return stats;
    }
}
