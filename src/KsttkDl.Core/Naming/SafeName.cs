using System.Text;

namespace KsttkDl.Core.Naming;

/// <summary> Makes a name usable as a single path segment. Entity names are legal in JSON but not always in filenames - some carry slashes and question marks - and untranslated names stay Japanese, which NTFS handles fine once the reserved characters are replaced. </summary>
public static class SafeName
{
    private static readonly HashSet<char> Invalid =
    [
        .. Path.GetInvalidFileNameChars(),
        // Not invalid everywhere, but normalising them keeps the output portable.
        '<', '>', ':', '"', '/', '\\', '|', '?', '*',
    ];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string name, string fallback = "unnamed")
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }

        // Windows strips trailing dots and spaces, which would collide two distinct names.
        var result = builder.ToString().Trim().TrimEnd('.', ' ');

        if (result.Length == 0)
        {
            return fallback;
        }

        var stem = result.Split('.')[0];
        return ReservedDeviceNames.Contains(stem) ? "_" + result : result;
    }

    /// <summary>Folder name for an entity: numeric id first so listings sort by id.</summary>
    public static string EntityFolder(int id, string name) => $"{id}_{Sanitize(name, id.ToString())}";
}
