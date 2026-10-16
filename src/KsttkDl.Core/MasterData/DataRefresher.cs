using KsttkDl.Core.Download;

namespace KsttkDl.Core.MasterData;

public sealed record RefreshResult(string FileName, bool Updated, long Bytes, string? Error);

/// <summary>pulls fresh copies of the upstream data into <c>data/</c>. updated on the game's schedule, so this is an explicit command rather than something implicit in every download.  </summary>
public static class DataRefresher
{
    public static async Task<IReadOnlyList<RefreshResult>> RefreshAsync(
        DataStore store,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(store.Root);

        var results = new List<RefreshResult>();

        foreach (var source in DataStore.Sources)
        {
            log($"  {source.FileName} <- {source.Description}");
            var isStart2 = source.FileName == DataStore.Start2File;
            if (isStart2)
            {
                RememberOffsets(store);
            }

            results.Add(await FetchAsync(store, source, cancellationToken));
            if (isStart2)
            {
                RememberOffsets(store);
            }
        }

        return results;
    }

    // seasonal CGs swap a ship's offsets in place; noting them on both sides of a refresh keeps the old ones usable
    private static void RememberOffsets(DataStore store)
    {
        if (!File.Exists(store.PathFor(DataStore.Start2File)))
        {
            return;
        }

        try
        {
            ShipGraphHistory.Load(store).Remember(store.LoadStart2());
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
        }
    }

    private static async Task<RefreshResult> FetchAsync(
        DataStore store,
        DataSource source,
        CancellationToken cancellationToken)
    {
        var destination = store.PathFor(source.FileName);

        try
        {
            using var response = await CdnHttpClient.Instance.GetAsync(
                source.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            // swap through a sibling so a failed refresh leaves the previous copy usable.
            var temporary = destination + ".tmp";
            long size;
            await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(temporary))
            {
                await content.CopyToAsync(target, cancellationToken);
                size = target.Length;
            }

            File.Move(temporary, destination, overwrite: true);
            return new RefreshResult(source.FileName, true, size, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return new RefreshResult(source.FileName, false, 0, ex.Message);
        }
    }
}
