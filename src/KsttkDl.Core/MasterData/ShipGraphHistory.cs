using System.Text.Json;

namespace KsttkDl.Core.MasterData;

/// <summary>a ship CG's port offsets (api_boko_n / api_boko_d) as they were at one graph version.</summary>
public sealed record GraphOffsets(int[]? Normal, int[]? Damaged);

/// <summary>
/// the port offsets of every ship CG version seen so far, kept in <c>data/shipgraph-history.json</c>. a seasonal
/// CG replaces a ship's art and offsets in place, so an older copy of the art can only be placed again if its
/// offsets were written down while they were current
/// </summary>
public sealed class ShipGraphHistory
{
    public const string FileName = "shipgraph-history.json";

    private readonly string _path;
    private readonly Dictionary<int, Dictionary<string, GraphOffsets>> _graphs;

    private ShipGraphHistory(string path, Dictionary<int, Dictionary<string, GraphOffsets>> graphs)
    {
        _path = path;
        _graphs = graphs;
    }

    public static ShipGraphHistory Load(DataStore store)
    {
        var path = store.PathFor(FileName);
        try
        {
            if (File.Exists(path))
            {
                var graphs = JsonSerializer.Deserialize<Dictionary<int, Dictionary<string, GraphOffsets>>>(File.ReadAllText(path));
                return new ShipGraphHistory(path, graphs ?? []);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new ShipGraphHistory(path, []);
    }

    /// <summary>the offsets <paramref name="shipId"/>'s CG had at <paramref name="version"/>, if they were recorded.</summary>
    public GraphOffsets? Get(int shipId, string version) =>
        _graphs.TryGetValue(shipId, out var versions) ? versions.GetValueOrDefault(version) : null;

    /// <summary>notes the current offsets of every ship CG, saving only when something new turned up.</summary>
    public void Remember(Start2 start2)
    {
        var changed = false;
        foreach (var graph in start2.ShipGraphs)
        {
            if (graph.BokoN is null || graph.Version.Length == 0)
            {
                continue;
            }

            if (!_graphs.TryGetValue(graph.Id, out var versions))
            {
                versions = [];
                _graphs[graph.Id] = versions;
            }

            if (versions.TryAdd(graph.Version[0], new GraphOffsets(graph.BokoN, graph.BokoD)))
            {
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_graphs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a read-only data folder only costs the history, not the run
        }
    }
}
