using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using KsttkDl.Core.Catalog;

namespace KsttkDl.Core.Extraction;

/// <summary>shared plumbing for the extractors: locating a catalog entry in the raw mirror, copying t to its organized home, and writing the json sidecars.</summary>
public sealed class ExtractionContext(string rawRoot, string outputRoot, bool overwrite, Action<string> log)
{
    private static readonly JsonSerializerOptions SidecarOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // omit rather than emit "yomi": null on entities that have no reading.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // read by people, not embedded in a web page: the relaxed encoder leaves jp names and the apostrophes in quote text as themselves, not \uXXXX escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string RawRoot { get; } = rawRoot;

    public string OutputRoot { get; } = outputRoot;

    public Action<string> Log { get; } = log;

    /// <summary>path inside the raw mirror, or null if the entry was never downloaded.</summary>
    public string? RawFile(AssetEntry entry)
    {
        var path = Path.Combine(RawRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    public string OutputPath(params string[] segments) =>
        Path.Combine(new[] { OutputRoot }.Concat(segments).ToArray());

    /// <summary> copies a raw file to its organized home, leaving existing files alone unless --overwrite was given, so re-running over a large output is cheap.  </summary>
    public bool Copy(string source, string destination)
    {
        if (!overwrite && File.Exists(destination))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        return true;
    }

    public bool ShouldWrite(string destination) => overwrite || !File.Exists(destination);

    public void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, value, SidecarOptions);
    }
}

public sealed record ExtractStats
{
    public int Files { get; init; }
    public int Frames { get; init; }
    public int Entities { get; init; }

    public static ExtractStats operator +(ExtractStats a, ExtractStats b) => new()
    {
        Files = a.Files + b.Files,
        Frames = a.Frames + b.Frames,
        Entities = a.Entities + b.Entities,
    };
}
