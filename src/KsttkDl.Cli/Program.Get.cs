using System.CommandLine;
using KsttkDl.Core;
using KsttkDl.Core.Catalog;
using KsttkDl.Core.Extraction;
using KsttkDl.Core.Fetch;
using KsttkDl.Core.MasterData;

namespace KsttkDl.Cli;

/// <summary> <c>ksttk-dl get</c>: fetch one ship, item, piece of furniture or map instead of a whole category. the files go into the same raw mirror as <c>download</c>, and <c>--extract</c> then organizes the category as <c>extract</c> would </summary>
public static partial class Program
{
    private sealed record GetTarget(string Label, AssetCategory[] Categories, IReadOnlyList<AssetEntry> Entries);

    private static Command BuildGetCommand(Option<string> dataOption, Option<string> outputOption)
    {
        var command = new Command("get", "Download the assets of a single ship, equipment, furniture or map");

        command.Subcommands.Add(BuildGetSubcommand(dataOption, outputOption, "ship",
            "A ship by id or name, e.g. 131 or \"Yamato Kai Ni\"", includeVoicesOption: true,
            (library, query, voices) =>
            {
                var ship = FindShip(library, query);
                var entries = library.ShipImages(ship.Id).ToList();
                if (voices)
                {
                    entries.AddRange(library.ShipVoices(ship.Id));
                }

                return new GetTarget($"Ship {ship.Id}: {library.Names.Ship(ship).English}",
                    [AssetCategory.Ships, AssetCategory.Audio], entries);
            }));

        command.Subcommands.Add(BuildGetSubcommand(dataOption, outputOption, "equipment",
            "Equipment by id or name, e.g. 9 or \"46cm Triple Gun Mount\"", includeVoicesOption: false,
            (library, query, _) =>
            {
                var item = FindByIdOrName(library.Start2.SlotItems, query, i => i.Id,
                    i => [i.Name, library.Names.Item(i).English], "equipment");
                return new GetTarget($"Equipment {item.Id}: {library.Names.Item(item).English}",
                    [AssetCategory.Equipment], library.EntriesFor("equipment", item.Id));
            }));

        command.Subcommands.Add(BuildGetSubcommand(dataOption, outputOption, "furniture",
            "Furniture by id or Japanese title", includeVoicesOption: false,
            (library, query, _) =>
            {
                var furniture = FindByIdOrName(library.Start2.Furniture, query, f => f.Id, f => [f.Title], "furniture");
                return new GetTarget($"Furniture {furniture.Id}: {furniture.Title}",
                    [AssetCategory.Furniture], library.EntriesFor("furniture", furniture.Id));
            }));

        command.Subcommands.Add(BuildGetSubcommand(dataOption, outputOption, "map",
            "A map as area-number (1-1), or a whole area (7)", includeVoicesOption: false,
            (library, query, _) =>
            {
                var parts = query.Split('-', StringSplitOptions.TrimEntries);
                if (!int.TryParse(parts[0], out var area) || parts.Length > 2)
                {
                    throw new InvalidOperationException($"'{query}' is not a map; use a form like 1-1 or 7.");
                }

                var maps = library.Start2.MapInfos
                    .Where(m => m.MapAreaId == area && (parts.Length == 1 || parts[1] == m.No.ToString()))
                    .ToList();
                if (maps.Count == 0)
                {
                    throw new InvalidOperationException($"No map {query} in the master data.");
                }

                var entries = maps.SelectMany(m => library.MapEntries(m.MapAreaId, m.No)).ToList();
                var label = maps.Count == 1 ? $"Map {area}-{maps[0].No}: {maps[0].Name}" : $"Area {area}: {maps.Count} maps";
                return new GetTarget(label, [AssetCategory.Maps], entries);
            }));

        return command;
    }

    private static Command BuildGetSubcommand(
        Option<string> dataOption,
        Option<string> outputOption,
        string name,
        string description,
        bool includeVoicesOption,
        Func<GameLibrary, string, bool, GetTarget> resolve)
    {
        var queryArgument = new Argument<string>("target") { Description = description };
        var worldOption = new Option<int?>("--world") { Description = "World number to download from (see 'ksttk-dl worlds')" };
        var hostOption = new Option<string?>("--host") { Description = "CDN hostname, instead of --world" };
        var rareOption = new Option<bool>("--include-rare") { Description = "Also fetch the rarely-used asset variants" };
        var extractOption = new Option<bool>("--extract") { Description = "Organize the category into the output tree afterwards" };
        var noVoicesOption = new Option<bool>("--no-voices") { Description = "Skip the ship's voice lines" };

        var command = new Command(name, $"Download one {name}") { queryArgument };
        command.Options.Add(worldOption);
        command.Options.Add(hostOption);
        command.Options.Add(rareOption);
        command.Options.Add(extractOption);
        if (includeVoicesOption)
        {
            command.Options.Add(noVoicesOption);
        }

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = new Workspace(parseResult.GetValue(dataOption)!, parseResult.GetValue(outputOption)!);
            var includeRare = parseResult.GetValue(rareOption);

            try
            {
                var server = workspace.ResolveServer(
                    workspace.Data.LoadTranslations(), parseResult.GetValue(worldOption), parseResult.GetValue(hostOption));

                // a fresh manifest keeps the ?version= queries current, as for a bulk download
                VersionInfo? versions = null;
                try
                {
                    versions = await workspace.FetchVersionsAsync(server.Host, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                           && !cancellationToken.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"Could not reach {server.Host}: {ex.Message}");
                    Console.Error.WriteLine("The CDN is Japan-only; use --jp, --proxy, or a Japanese network path.");
                    return 1;
                }

                var library = GameLibrary.Load(workspace, includeRare, versions);
                var target = resolve(library, parseResult.GetValue(queryArgument)!, !parseResult.GetValue(noVoicesOption));

                Console.WriteLine($"{target.Label} ({target.Entries.Count} assets) from {server.Host}");
                var progress = new ConsoleProgress(target.Entries.Count);
                var stats = await library.FetchAsync(server.Host, target.Entries, Console.WriteLine, progress,
                    cancellationToken: cancellationToken);
                progress.Finish();

                Console.WriteLine(
                    $"Downloaded {stats.Downloaded} ({Human(stats.Bytes)}), already had {stats.Skipped}, " +
                    $"absent {stats.Missing}, failed {stats.Failed}");

                if (parseResult.GetValue(extractOption))
                {
                    ExtractCategories(workspace, library, target.Categories, includeRare);
                }

                return stats.Failed == 0 ? 0 : 1;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        });

        return command;
    }

    private static void ExtractCategories(Workspace workspace, GameLibrary library, AssetCategory[] categories, bool includeRare)
    {
        // the whole catalog of each category, not just this target, so the sidecars still list everything already downloaded
        var set = categories.ToHashSet();
        var (start2, translations, catalog) = workspace.BuildCatalog(set, workspace.LoadCachedVersions(), includeRare);
        var context = new ExtractionContext(workspace.RawRoot, workspace.OutputRoot, overwrite: false, Console.WriteLine);
        var stats = new Extractor(context, start2, translations).Extract(catalog, set);
        Console.WriteLine($"Wrote {stats.Files} files into {Path.GetFullPath(workspace.OutputRoot)}");
    }

    private static MstShip FindShip(GameLibrary library, string query) =>
        FindByIdOrName(library.Start2.Ships.Values, query, s => s.Id,
            s => [s.Name, library.Names.Ship(s).English], "ship");

    /// <summary>an id, an exact name (English or Japanese, any case), or a unique partial name.</summary>
    private static T FindByIdOrName<T>(
        IEnumerable<T> items, string query, Func<T, int> id, Func<T, string[]> names, string kind)
    {
        var list = items.ToList();
        if (int.TryParse(query, out var number))
        {
            return list.FirstOrDefault(i => id(i) == number)
                   ?? throw new InvalidOperationException($"No {kind} with id {number}.");
        }

        var exact = list.Where(i => names(i).Any(n => n.Equals(query, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var partial = exact.Count > 1
            ? exact
            : list.Where(i => names(i).Any(n => n.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();

        return partial.Count switch
        {
            1 => partial[0],
            0 => throw new InvalidOperationException($"No {kind} matches '{query}'."),
            _ => throw new InvalidOperationException(
                $"'{query}' matches {partial.Count} {kind} entries; use an id:\n" +
                string.Join('\n', partial.Take(15).Select(i => $"  {id(i),5}  {string.Join(" / ", names(i).Distinct())}"))),
        };
    }
}
