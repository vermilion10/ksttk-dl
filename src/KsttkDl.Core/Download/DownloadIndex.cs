using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsttkDl.Core.Download;

public sealed class IndexRecord
{
    /// <summary>the <c>?version=...</c> the file was fetched with. a change means refetch</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>server ETag or Last-Modified, whichever was offered</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    /// <summary>true when the cdn answered 404 ` the asset does not exist for this entity</summary>
    [JsonPropertyName("missing")]
    public bool Missing { get; set; }
}

/// <summary> tracks what is already in the raw mirror so a re-run skips unchanged files. known 404s are recorded too, so a resumed run does not spend itself re-confirming the same non-existent (id, type) pairs; <c>--retry-missing</c> clears them </summary>
public sealed class DownloadIndex
{
    private const string FileName = ".ksttk-dl-index.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly ConcurrentDictionary<string, IndexRecord> _records;
    private readonly string _path;
    private int _dirtyCount;

    private DownloadIndex(string path, ConcurrentDictionary<string, IndexRecord> records)
    {
        _path = path;
        _records = records;
    }

    public static DownloadIndex Load(string rawRoot)
    {
        var path = Path.Combine(rawRoot, FileName);
        var records = new ConcurrentDictionary<string, IndexRecord>(StringComparer.Ordinal);

        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, IndexRecord>>(stream);
                foreach (var (key, value) in loaded ?? [])
                {
                    records[key] = value;
                }
            }
            catch (JsonException)
            {
                // a truncated index from a killed run is not worth failing over ` the files are still on disk, they just get re-verified once.
            }
        }

        return new DownloadIndex(path, records);
    }

    public int Count => _records.Count;

    public int MissingCount => _records.Values.Count(r => r.Missing);

    public bool TryGet(string path, out IndexRecord record) => _records.TryGetValue(path, out record!);

    public void Record(string path, IndexRecord record)
    {
        _records[path] = record;

        // flush periodically so an interrupted run resumes near where it stopped. Each write rewrites the whole index ` a few MB on a full catalog ` so keep it rare.
        if (Interlocked.Increment(ref _dirtyCount) % 2000 == 0)
        {
            Save();
        }
    }

    public void ForgetMissing()
    {
        foreach (var (key, value) in _records)
        {
            if (value.Missing)
            {
                _records.TryRemove(key, out _);
            }
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // swap through a sibling so an interrupt cannot leave a half-written index
        var temporary = _path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, new SortedDictionary<string, IndexRecord>(
                _records.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                StringComparer.Ordinal), SerializerOptions);
        }

        File.Move(temporary, _path, overwrite: true);
    }
}
