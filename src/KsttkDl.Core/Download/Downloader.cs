using System.Net;
using KsttkDl.Core.Catalog;

namespace KsttkDl.Core.Download;

public sealed record DownloadOptions(
    string Host,
    string RawRoot,
    int Concurrency = 6,
    bool RetryMissing = false,
    bool DryRun = false);

public sealed record DownloadStats
{
    public int Downloaded { get; init; }
    public int Skipped { get; init; }
    public int Missing { get; init; }
    public int Failed { get; init; }
    public long Bytes { get; init; }

    public static DownloadStats operator +(DownloadStats a, DownloadStats b) => new()
    {
        Downloaded = a.Downloaded + b.Downloaded,
        Skipped = a.Skipped + b.Skipped,
        Missing = a.Missing + b.Missing,
        Failed = a.Failed + b.Failed,
        Bytes = a.Bytes + b.Bytes,
    };
}

/// <summary> fetches catalog entries into the raw mirror, preserving the original <c>kcs2/...</c> layout. the mirror doubles as the cache: extract reads from it, and a second download skips anything the index already accounts for </summary>
public sealed class Downloader(DownloadOptions options, DownloadIndex index, Action<string> log)
{
    public async Task<DownloadStats> RunAsync(
        IReadOnlyList<AssetEntry> entries,
        IProgress<DownloadStats>? progress,
        CancellationToken cancellationToken)
    {
        if (options.RetryMissing)
        {
            index.ForgetMissing();
        }

        var downloaded = 0;
        var skipped = 0;
        var missing = 0;
        var failed = 0;
        var bytes = 0L;

        // Parallel.ForEachAsync rather than a semaphore over Task.WhenAll: a full catalog is ~65,000 entries, and this keeps only --concurrency requests in flight instead of allocating a task per entry up front
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(entries, parallelOptions, async (entry, token) =>
        {
            var result = await FetchAsync(entry, token);

            switch (result.Outcome)
            {
                case FetchOutcome.Downloaded:
                    Interlocked.Increment(ref downloaded);
                    Interlocked.Add(ref bytes, result.Bytes);
                    break;
                case FetchOutcome.Skipped:
                    Interlocked.Increment(ref skipped);
                    break;
                case FetchOutcome.Missing:
                    Interlocked.Increment(ref missing);
                    break;
                default:
                    Interlocked.Increment(ref failed);
                    break;
            }

            progress?.Report(new DownloadStats
            {
                Downloaded = Volatile.Read(ref downloaded),
                Skipped = Volatile.Read(ref skipped),
                Missing = Volatile.Read(ref missing),
                Failed = Volatile.Read(ref failed),
                Bytes = Interlocked.Read(ref bytes),
            });
        });

        if (!options.DryRun)
        {
            index.Save();
        }

        return new DownloadStats
        {
            Downloaded = downloaded,
            Skipped = skipped,
            Missing = missing,
            Failed = failed,
            Bytes = bytes,
        };
    }

    private enum FetchOutcome
    {
        Downloaded,
        Skipped,
        Missing,
        Failed,
    }

    private readonly record struct FetchResult(FetchOutcome Outcome, long Bytes);

    private async Task<FetchResult> FetchAsync(AssetEntry entry, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(options.RawRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));

        if (index.TryGet(entry.Path, out var record) && record.Version == entry.VersionQuery)
        {
            // a known 404 stays known until --retry-missing clears it
            if (record.Missing)
            {
                return new FetchResult(FetchOutcome.Missing, 0);
            }

            // trust the index only as far as the file still existing ` a deleted file should come back on the next run
            if (File.Exists(destination) && new FileInfo(destination).Length == record.Size)
            {
                return new FetchResult(FetchOutcome.Skipped, 0);
            }
        }

        if (options.DryRun)
        {
            return new FetchResult(FetchOutcome.Downloaded, 0);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, entry.Url(options.Host));
            using var response = await CdnHttpClient.Instance.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                index.Record(entry.Path, new IndexRecord { Version = entry.VersionQuery, Missing = true });
                return new FetchResult(FetchOutcome.Missing, 0);
            }

            if (!response.IsSuccessStatusCode)
            {
                log($"  {(int)response.StatusCode} {entry.Path}");
                return new FetchResult(FetchOutcome.Failed, 0);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // stream to a temp file rather than buffering: CGs and BGM tracks run to several mb each, and a run can be tens of thousands of them
            var temporary = destination + ".part";
            long size;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(temporary))
            {
                await source.CopyToAsync(target, cancellationToken);
                size = target.Length;
            }

            File.Move(temporary, destination, overwrite: true);

            index.Record(entry.Path, new IndexRecord
            {
                Version = entry.VersionQuery,
                Size = size,
                Tag = response.Headers.ETag?.Tag ?? response.Content.Headers.LastModified?.ToString("R"),
            });

            return new FetchResult(FetchOutcome.Downloaded, size);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            log($"  error {entry.Path}: {ex.Message}");
            return new FetchResult(FetchOutcome.Failed, 0);
        }
    }
}
