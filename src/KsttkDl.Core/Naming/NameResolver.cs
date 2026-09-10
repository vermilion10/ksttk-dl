using System.Text;
using System.Text.RegularExpressions;
using KsttkDl.Core.MasterData;

namespace KsttkDl.Core.Naming;

/// <summary>a translated entity name. <see cref="English"/> names the output folder; <see cref="Translated"/> distinguishes a real translation from a fallback, which the  sidecar records so a gap is visible rather than silently papered over. </summary>
public sealed record ResolvedName(string Japanese, string English, string Yomi, bool Translated);

/// <summary> Turns Japanese master-data names into English folder names. kc3-translations ships only  base ship names ("睦月"), not every remodel and seasonal variant ("正月睦月改二"), so those are handled as KC3Kai does: peel the known prefixes and suffixes off, translate the  base, reassemble. Against current data this resolves every ship and equipment entry.
/// </summary>
public sealed partial class NameResolver(Translations translations)
{
    /// <summary>Strips ship_affix.json's optional-separator tokens ("{ -}? Late Model").</summary>
    [GeneratedRegex(@"\{[^}]*\}\??")]
    private static partial Regex AffixPlaceholder();

    private readonly List<string> _suffixesByLength =
        translations.ShipSuffixes.Keys.OrderByDescending(k => k.Length).ToList();

    public ResolvedName Ship(MstShip ship)
    {
        var (english, translated) = TranslateShipName(ship.Name);
        return new ResolvedName(ship.Name, english, ship.Yomi, translated);
    }

    public ResolvedName Item(SlotItem item)
    {
        var translated = translations.Items.TryGetValue(item.Name, out var english);
        return new ResolvedName(item.Name, translated ? english! : item.Name, "", translated);
    }

    /// <summary>No translation source exists, so the Japanese title is the name.</summary>
    public ResolvedName Furniture(MasterData.Furniture furniture) =>
        new(furniture.Title, furniture.Title, "", false);

    private (string English, bool Translated) TranslateShipName(string name)
    {
        if (name.Length == 0)
        {
            return ("", false);
        }

        if (translations.Ships.TryGetValue(name, out var direct))
        {
            return (direct, true);
        }

        var stem = name;
        var prefix = "";
        foreach (var (jp, en) in translations.ShipPrefixes)
        {
            if (stem.StartsWith(jp, StringComparison.Ordinal) && stem.Length > jp.Length)
            {
                prefix = Clean(en);
                stem = stem[jp.Length..];
                break;
            }
        }

        // Suffixes stack ("改二" over a seasonal marker), so peel repeatedly, longest first.
        var suffix = new StringBuilder();
        for (var peeled = true; peeled;)
        {
            peeled = false;
            foreach (var jp in _suffixesByLength)
            {
                if (!stem.EndsWith(jp, StringComparison.Ordinal) || stem.Length <= jp.Length)
                {
                    continue;
                }

                suffix.Insert(0, Clean(translations.ShipSuffixes[jp]));
                stem = stem[..^jp.Length];
                peeled = true;
                break;
            }
        }

        if (translations.Ships.TryGetValue(stem, out var baseName))
        {
            return ((prefix + baseName + suffix).Trim(), true);
        }

        return (name, false);
    }

    private static string Clean(string affix) => AffixPlaceholder().Replace(affix, "");
}
