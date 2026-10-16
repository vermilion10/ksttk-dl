using System.CommandLine;
using KsttkDl.Core;
using KsttkDl.Core.Catalog;
using KsttkDl.Core.Download;
using KsttkDl.Core.Extraction;
using KsttkDl.Core.MasterData;

namespace KsttkDl.Cli;

/// <summary> targets System.CommandLine 3.0.0-preview.7, whose API differs from the 2.0.0-betas: SetAction over SetHandler, Options.Add over a collection initializer, and  Parse(args).InvokeAsync() rather than InvokeAsync on the command </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var dataOption = new Option<string>("--data")
        {
            Description = "Directory holding the cached master data and translations",
            DefaultValueFactory = _ => "data",
            Recursive = true,
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Output directory; the raw mirror lives in <output>/raw",
            DefaultValueFactory = _ => "output",
            Recursive = true,
        };

        var proxyOption = new Option<string?>("--proxy")
        {
            Description = "Send requests through a proxy, e.g. socks5://127.0.0.1:1080 or http://user:pass@host:8080",
            Recursive = true,
        };

        var root = new RootCommand("Download and organize game assets straight from the CDN.");
        root.Options.Add(dataOption);
        root.Options.Add(outputOption);
        root.Options.Add(proxyOption);

        root.Subcommands.Add(BuildRefreshCommand(dataOption));
        root.Subcommands.Add(BuildWorldsCommand(dataOption));
        root.Subcommands.Add(BuildDownloadCommand(dataOption, outputOption));
        root.Subcommands.Add(BuildExtractCommand(dataOption, outputOption));

        var parseResult = root.Parse(args);

        // the shared client is built on first use, so the proxy is applied before any command runs
        if (parseResult.Errors.Count == 0 && parseResult.GetValue(proxyOption) is { Length: > 0 } proxy)
        {
            try
            {
                CdnHttpClient.UseProxy(proxy);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        return await parseResult.InvokeAsync();
    }

    private static readonly AssetCategory[] AllCategories = Enum.GetValues<AssetCategory>();

    private static Option<string> CategoriesOption() => new("--categories")
    {
        Description = "Comma-separated: " + string.Join(", ", AllCategories.Select(c => c.ToString().ToLowerInvariant())),
        DefaultValueFactory = _ => "all",
    };

    private static HashSet<AssetCategory> ParseCategories(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return [.. AllCategories];
        }

        var result = new HashSet<AssetCategory>();
        foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<AssetCategory>(name, ignoreCase: true, out var category))
            {
                throw new InvalidOperationException(
                    $"Unknown category '{name}'. Valid: " +
                    string.Join(", ", AllCategories.Select(c => c.ToString().ToLowerInvariant())));
            }

            result.Add(category);
        }

        return result;
    }

    private static Command BuildRefreshCommand(Option<string> dataOption)
    {
        var command = new Command("refresh-data", "Fetch or update the master data and translation files");

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = new DataStore(parseResult.GetValue(dataOption)!);
            Console.WriteLine($"Refreshing data into {Path.GetFullPath(store.Root)}");

            var results = await DataRefresher.RefreshAsync(store, Console.WriteLine, cancellationToken);

            var failed = results.Where(r => r.Error is not null).ToList();
            foreach (var failure in failed)
            {
                Console.Error.WriteLine($"  failed: {failure.FileName}: {failure.Error}");
            }

            var bytes = results.Sum(r => r.Bytes);
            Console.WriteLine($"Updated {results.Count - failed.Count}/{results.Count} files ({Human(bytes)})");

            return failed.Count == 0 ? 0 : 1;
        });

        return command;
    }

    private static Command BuildWorldsCommand(Option<string> dataOption)
    {
        var command = new Command("worlds", "List the known worlds and their CDN hostnames");

        command.SetAction(parseResult =>
        {
            var store = new DataStore(parseResult.GetValue(dataOption)!);
            var servers = Translations.Load(store).Servers;

            if (servers.Count == 0)
            {
                Console.Error.WriteLine("No servers.json found. Run 'ksttk-dl refresh-data' first.");
                return 1;
            }

            foreach (var server in servers)
            {
                Console.WriteLine($"{server.Num,3}  {server.Host,-30}  {server.Name}");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildDownloadCommand(Option<string> dataOption, Option<string> outputOption)
    {
        var worldOption = new Option<int?>("--world") { Description = "World number to download from (see 'ksttk-dl worlds')" };
        var hostOption = new Option<string?>("--host") { Description = "CDN hostname, instead of --world" };
        var categoriesOption = CategoriesOption();
        var rareOption = new Option<bool>("--include-rare") { Description = "Also fetch the rarely-used asset variants" };
        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Simultaneous requests",
            DefaultValueFactory = _ => 6,
        };
        var retryMissingOption = new Option<bool>("--retry-missing")
        {
            Description = "Re-check assets previously recorded as 404",
        };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Enumerate the catalog without fetching anything" };

        var command = new Command("download", "Bulk download raw assets into the raw mirror");
        command.Options.Add(worldOption);
        command.Options.Add(hostOption);
        command.Options.Add(categoriesOption);
        command.Options.Add(rareOption);
        command.Options.Add(concurrencyOption);
        command.Options.Add(retryMissingOption);
        command.Options.Add(dryRunOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = new Workspace(parseResult.GetValue(dataOption)!, parseResult.GetValue(outputOption)!);
            var categories = ParseCategories(parseResult.GetValue(categoriesOption));
            var includeRare = parseResult.GetValue(rareOption);
            var dryRun = parseResult.GetValue(dryRunOption);

            var translations = workspace.Data.LoadTranslations();
            var server = workspace.ResolveServer(
                translations, parseResult.GetValue(worldOption), parseResult.GetValue(hostOption));

            Console.WriteLine($"World {server.Num}: {server.Name} ({server.Host})");

            // fetched live, not cached: it decides the ?version= on every static and map asset
            VersionInfo versions;
            try
            {
                versions = await workspace.FetchVersionsAsync(server.Host, cancellationToken);
                Console.WriteLine($"Asset versions: {versions.Categories.Count} categories");
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Could not reach {server.Host}: {ex.Message}");
                Console.Error.WriteLine("The CDN is Japan-only; use a Japanese network path or --proxy.");
                return 1;
            }

            var (_, _, catalog) = workspace.BuildCatalog(categories, versions, includeRare);

            var entries = catalog.ToList();
            if (categories.Contains(AssetCategory.Ui))
            {
                entries.AddRange(DerivedCatalog.ServerNameAssets(server.Ip));
            }

            Console.WriteLine($"Catalog: {entries.Count} assets across {categories.Count} categories"
                              + (includeRare ? " (including rare)" : ""));

            if (dryRun)
            {
                foreach (var group in entries.GroupBy(e => e.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    Console.WriteLine($"  {group.Key,-20} {group.Count(),7}");
                }

                return 0;
            }

            Directory.CreateDirectory(workspace.RawRoot);
            var index = DownloadIndex.Load(workspace.RawRoot);
            var options = new DownloadOptions(
                server.Host, workspace.RawRoot, parseResult.GetValue(concurrencyOption),
                parseResult.GetValue(retryMissingOption), dryRun);

            var downloader = new Downloader(options, index, Console.WriteLine);
            var progress = new ConsoleProgress(entries.Count);

            var stats = await downloader.RunAsync(entries, progress, cancellationToken);
            progress.Finish();

            // gauge sprites and furniture pictures are named inside files that only exist once the first pass has run
            var derived = new List<AssetEntry>();
            if (categories.Contains(AssetCategory.Maps))
            {
                derived.AddRange(DerivedCatalog.GaugeImages(
                    workspace.RawRoot, catalog.Where(e => e.Group == "gauge")));
            }

            if (categories.Contains(AssetCategory.Furniture))
            {
                derived.AddRange(DerivedCatalog.FurniturePictures(
                    workspace.RawRoot, catalog.Where(e => e.AssetType == "scripts_json")));
            }

            if (derived.Count > 0)
            {
                Console.WriteLine($"Second pass: {derived.Count} derived assets");
                var derivedProgress = new ConsoleProgress(derived.Count);
                stats += await downloader.RunAsync(derived, derivedProgress, cancellationToken);
                derivedProgress.Finish();
            }

            Console.WriteLine(
                $"Downloaded {stats.Downloaded} ({Human(stats.Bytes)}), skipped {stats.Skipped}, " +
                $"absent {stats.Missing}, failed {stats.Failed}");

            return stats.Failed == 0 ? 0 : 1;
        });

        return command;
    }

    private static Command BuildExtractCommand(Option<string> dataOption, Option<string> outputOption)
    {
        var categoriesOption = CategoriesOption();
        var rareOption = new Option<bool>("--include-rare")
        {
            Description = "Include the rare asset variants, matching the download that fetched them",
        };
        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Rewrite output files that already exist",
        };

        var command = new Command("extract", "Organize the raw mirror into the named output tree");
        command.Options.Add(categoriesOption);
        command.Options.Add(rareOption);
        command.Options.Add(overwriteOption);

        command.SetAction(parseResult =>
        {
            var workspace = new Workspace(parseResult.GetValue(dataOption)!, parseResult.GetValue(outputOption)!);
            var categories = ParseCategories(parseResult.GetValue(categoriesOption));

            if (!Directory.Exists(workspace.RawRoot))
            {
                Console.Error.WriteLine($"No raw mirror at {Path.GetFullPath(workspace.RawRoot)}. Run 'ksttk-dl download' first.");
                return 1;
            }

            // rebuilt from the same master data and the manifest the download saved, so the catalog matches what is on disk
            var versions = workspace.LoadCachedVersions();
            var (start2, translations, baseCatalog) = workspace.BuildCatalog(
                categories, versions, parseResult.GetValue(rareOption));

            // recomputed as download does, by reading the gauge and furniture-script files already in the mirror, they are not in the master-data catalog
            var catalog = baseCatalog.ToList();
            if (categories.Contains(AssetCategory.Maps))
            {
                catalog.AddRange(DerivedCatalog.GaugeImages(
                    workspace.RawRoot, baseCatalog.Where(e => e.Group == "gauge")));
            }

            if (categories.Contains(AssetCategory.Furniture))
            {
                catalog.AddRange(DerivedCatalog.FurniturePictures(
                    workspace.RawRoot, baseCatalog.Where(e => e.AssetType == "scripts_json")));
            }

            var context = new ExtractionContext(
                workspace.RawRoot, workspace.OutputRoot, parseResult.GetValue(overwriteOption), Console.WriteLine);

            var stats = new Extractor(context, start2, translations).Extract(catalog, categories);

            Console.WriteLine(
                $"Wrote {stats.Files} files and {stats.Frames} unpacked atlas frames " +
                $"for {stats.Entities} entities into {Path.GetFullPath(workspace.OutputRoot)}");

            return 0;
        });

        return command;
    }

    private static string Human(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KiB",
        _ => $"{bytes} B",
    };

    /// <summary> a single rewritten status line: a run is tens of thousands of requests, so per-file logging would bury the failures that matter </summary>
    private sealed class ConsoleProgress(int total) : IProgress<DownloadStats>
    {
        private readonly object _lock = new();
        private DateTime _lastDraw = DateTime.MinValue;

        public void Report(DownloadStats value)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var done = value.Downloaded + value.Skipped + value.Missing + value.Failed;

                if (done < total && now - _lastDraw < TimeSpan.FromMilliseconds(200))
                {
                    return;
                }

                _lastDraw = now;
                Console.Write(
                    $"\r  {done}/{total}  got {value.Downloaded}  cached {value.Skipped}  " +
                    $"absent {value.Missing}  failed {value.Failed}   ");
            }
        }

        public void Finish() => Console.WriteLine();
    }
}
