namespace KsttkDl.Core.Naming;

/// <summary>d eterministic per-entity asset paths, ported from KCCacheProxy's <c>src/proxy/preload.js</c>. The "hash" in  <c>kcs2/resources/ship/card/0001_5145.png</c> is a pure function of the id and the  <c>"{eors}_{type}"</c> string, so the catalog is enumerable offline rather than scraped. </summary>
public static class AssetPath
{
    /// <summary>Lookup table from preload.js. Order matters - the index derives from the type string.</summary>
    private static readonly int[] Resource =
    [
        6657, 5699, 3371, 8909, 7719, 6229, 5449, 8561, 2987, 5501,
        3127, 9319, 4365, 9811, 9927, 2423, 3439, 1865, 5925, 4409,
        5509, 1517, 9695, 9255, 5325, 3691, 5519, 6949, 5607, 9539,
        4133, 7795, 5465, 2659, 6381, 6875, 4019, 9195, 5645, 2887,
        1213, 1815, 8671, 3015, 3147, 2991, 7977, 7045, 1619, 7909,
        4451, 6573, 4545, 8251, 5983, 2849, 7249, 7449, 9477, 5963,
        2711, 9019, 7375, 2201, 5631, 4893, 7653, 3719, 8819, 5839,
        1853, 9843, 9119, 7023, 5681, 2345, 9873, 6349, 9315, 3795,
        9737, 4633, 4173, 7549, 7171, 6147, 4723, 5039, 2723, 7815,
        6201, 5999, 5339, 4431, 2911, 4435, 3611, 4423, 9517, 3243,
    ];

    /// <summary>Sum of UTF-16 code units, matching JS <c>charCodeAt</c>. Type strings are ASCII.</summary>
    private static int Key(string s)
    {
        var sum = 0;
        foreach (var c in s)
        {
            sum += c;
        }

        return sum;
    }

    /// <summary>  preload.js <c>create</c>, widened to <see cref="long"/>: the intermediate product  reaches ~1.7e9, exact in a JS double but an overflow in a 32-bit int.  </summary>
    public static string Create(int id, string type)
    {
        var index = (int)((Key(type) + (long)id * type.Length) % Resource.Length);
        return (17L * (id + 7) * Resource[index] % 8973 + 1000).ToString();
    }

    /// <summary>Ships and equipment pad to 4 digits, everything else to 3.</summary>
    public static string Pad(int id, string eors) =>
        eors is "ship" or "slot" ? id.ToString("D4") : id.ToString("D3");

    /// <summary>  preload.js <c>getPath</c>. The <c>_d</c> handling is quirky in the original and reproduced: a type containing "_d" but not "_dmg" ("banner_d", "full_d")  moves the "_d" onto the padded id, while "full_d_dmg" and "banner_g_dmg" hit the "_dmg" guard and are left alone. JS <c>String.replace</c> with a string pattern replaces only the first occurrence, hence Remove-at-index.  </summary>
    public static string GetPath(int id, string eors, string type, string ext, string? filename = null)
    {
        var suffix = "";
        var dIndex = type.IndexOf("_d", StringComparison.Ordinal);
        if (dIndex > 0 && type.IndexOf("_dmg", StringComparison.Ordinal) < 0)
        {
            suffix = "_d";
            type = type.Remove(dIndex, "_d".Length);
        }

        var uniqueKey = filename is null ? "" : "_" + filename;
        return $"kcs2/resources/{eors}/{type}/{Pad(id, eors)}{suffix}_{Create(id, $"{eors}_{type}")}{uniqueKey}.{ext}";
    }
}
