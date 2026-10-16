using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using KsttkDl.App.Infrastructure;
using KsttkDl.Core.Catalog;
using KsttkDl.Core.Download;
using KsttkDl.Core.Extraction;
using KsttkDl.Core.MasterData;

namespace KsttkDl.App.ViewModels;

/// <summary>one asset category on the sync page, with its download state.</summary>
public sealed class CategoryRow : Observable
{
    private int _total;
    private int _have;
    private int _absent;
    private double _progress;
    private bool _busy;
    private string _status = "";

    public CategoryRow(AssetCategory category, string kanji, string name, string description)
    {
        Category = category;
        Kanji = kanji;
        Name = name;
        Description = description;
    }

    public AssetCategory Category { get; }

    public string Kanji { get; }

    public string Name { get; }

    public string Description { get; }

    public int Total
    {
        get => _total;
        set
        {
            if (Set(ref _total, value))
            {
                RaiseCounts();
            }
        }
    }

    public int Have
    {
        get => _have;
        set
        {
            if (Set(ref _have, value))
            {
                RaiseCounts();
            }
        }
    }

    public int Absent
    {
        get => _absent;
        set
        {
            if (Set(ref _absent, value))
            {
                RaiseCounts();
            }
        }
    }

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public bool Busy
    {
        get => _busy;
        set => Set(ref _busy, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string Counts => Total == 0 ? "" : $"{Have:N0} of {Total - Absent:N0} files";

    public double Fraction => Total - Absent <= 0 ? 0 : (double)Have / (Total - Absent);

    /// <summary>済 when everything that exists is here, 未 when nothing is.</summary>
    public string Stamp => Total == 0 ? "" : Have >= Total - Absent ? "済" : Have == 0 ? "未" : "";

    private void RaiseCounts()
    {
        Raise(nameof(Counts));
        Raise(nameof(Fraction));
        Raise(nameof(Stamp));
        if (!Busy)
        {
            Progress = Fraction;
        }
    }
}

public sealed class SyncViewModel : Observable
{
    private WorldServer? _world;
    private string _dataStatus = "";
    private string _networkResult = "";
    private bool _busy;
    private CancellationTokenSource? _cancel;

    public SyncViewModel()
    {
        Categories =
        [
            new(AssetCategory.Ships, "艦", "Ships", "Cards, banners and CGs of every ship"),
            new(AssetCategory.Audio, "声", "Audio", "Voice lines, BGM, sound effects and title calls"),
            new(AssetCategory.Equipment, "装", "Equipment", "Equipment cards, fairies and plane sprites"),
            new(AssetCategory.Furniture, "家", "Furniture", "Room furniture and window views"),
            new(AssetCategory.Maps, "海", "Maps", "Sortie map backgrounds and node sheets"),
            new(AssetCategory.Ui, "画", "Interface", "Interface atlases and item cards"),
        ];

        RefreshData = new AsyncCommand(RefreshDataAsync, () => !_busy);
        TestNetwork = new AsyncCommand(TestNetworkAsync, () => !_busy);
        SwitchRelay = new AsyncCommand(SwitchRelayAsync, () => !_busy && Services.Tunnel is not null);
        Download = new AsyncCommand(p => DownloadAsync((CategoryRow)p!), p => !_busy && Services.Library is not null);
        DownloadAll = new AsyncCommand(DownloadAllAsync, () => !_busy && Services.Library is not null);
        Extract = new AsyncCommand(ExtractAsync, () => !_busy && Services.Library is not null);
        Cancel = new RelayCommand(() => _cancel?.Cancel(), () => _busy);
        OpenOutput = new RelayCommand(() => OpenFolder(Services.Settings.OutputFolder));

        Services.LibraryChanged += (_, _) => Reload();
        Services.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppServices.Settings))
            {
                Raise(nameof(NetworkMode));
                Raise(nameof(NetworkHint));
            }
        };
        Reload();
    }

    public AppServices Services => AppServices.Current;

    public ObservableCollection<CategoryRow> Categories { get; }

    public ObservableCollection<WorldServer> Worlds { get; } = [];

    public WorldServer? World
    {
        get => _world;
        set
        {
            if (Set(ref _world, value) && value is not null && value.Num != Services.Settings.World)
            {
                Services.Settings.World = value.Num;
                Services.Settings.Save();
            }
        }
    }

    public string DataStatus
    {
        get => _dataStatus;
        private set => Set(ref _dataStatus, value);
    }

    public string NetworkResult
    {
        get => _networkResult;
        private set => Set(ref _networkResult, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasData => Services.Library is not null;

    public string NetworkMode => Services.Settings.Network switch
    {
        Infrastructure.NetworkMode.JpRelay => "Built-in Japanese relay (VPN Gate)",
        Infrastructure.NetworkMode.Proxy => "Proxy: " + Services.Settings.ProxyUrl,
        _ => "Direct connection",
    };

    public string NetworkHint => Services.Settings.Network switch
    {
        Infrastructure.NetworkMode.JpRelay =>
            "ksttk-dl connects to a volunteer relay in Japan on the first download. Change the route in Settings.",
        Infrastructure.NetworkMode.Proxy => "Every request goes through this proxy. Change the route in Settings.",
        _ => "Requests leave from this computer's own address. Outside Japan, pick the built-in relay in Settings.",
    };

    public ICommand RefreshData { get; }

    public ICommand TestNetwork { get; }

    public ICommand SwitchRelay { get; }

    public ICommand Download { get; }

    public ICommand DownloadAll { get; }

    public ICommand Extract { get; }

    public ICommand Cancel { get; }

    public ICommand OpenOutput { get; }

    private void Reload()
    {
        var library = Services.Library;
        Worlds.Clear();
        foreach (var server in library?.Translations.Servers ?? [])
        {
            Worlds.Add(server);
        }

        World = Worlds.FirstOrDefault(w => w.Num == Services.Settings.World) ?? Worlds.FirstOrDefault();

        var start2 = Path.Combine(Services.Settings.DataFolder, DataStore.Start2File);
        DataStatus = File.Exists(start2)
            ? $"Game data from {File.GetLastWriteTime(start2):d MMM yyyy, HH:mm}"
            : "No game data yet. Get it first; everything else reads from it.";

        Raise(nameof(HasData));
        Raise(nameof(NetworkMode));
        Raise(nameof(NetworkHint));
        _ = CountAsync();
    }

    /// <summary>counts what is already in the mirror, per category, off the ui thread.</summary>
    private async Task CountAsync()
    {
        var library = Services.Library;
        if (library is null)
        {
            foreach (var row in Categories)
            {
                row.Total = row.Have = row.Absent = 0;
            }

            return;
        }

        var counts = await Task.Run(() => library.Catalog
            .GroupBy(e => e.Category)
            .ToDictionary(g => g.Key, g => (
                Total: g.Count(),
                Have: g.Count(e => library.LocalPath(e) is not null),
                Absent: g.Count(library.IsMissing))));

        foreach (var row in Categories)
        {
            var (total, have, absent) = counts.GetValueOrDefault(row.Category);
            row.Total = total;
            row.Have = have;
            row.Absent = absent;
        }
    }

    private async Task RefreshDataAsync()
    {
        IsBusy = true;
        try
        {
            var workspace = Services.Workspace;
            Services.AppendLog($"Refreshing game data into {workspace.Data.Root}");
            var results = await DataRefresher.RefreshAsync(workspace.Data, Services.AppendLog, CancellationToken.None);
            var failed = results.Count(r => r.Error is not null);
            if (failed > 0)
            {
                Services.Notify("Some data files failed", $"{failed} of {results.Count} could not be fetched.", ToastKind.Error);
            }

            // a fresh manifest keeps the ?version= queries current; without the CDN the cached one is used
            VersionInfo? versions = null;
            if (Services.Library is { } library || workspace.Data.IsPopulated)
            {
                var host = Services.Host ?? workspace.Data.LoadTranslations().Servers
                    .FirstOrDefault(s => s.Num == Services.Settings.World)?.Host;
                if (host is not null)
                {
                    try
                    {
                        versions = await workspace.FetchVersionsAsync(host, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                    {
                        Services.AppendLog($"Could not reach {host} for the version manifest: {ex.Message}");
                    }
                }
            }

            await Services.LoadLibraryAsync(versions);
            Services.RefreshRelayStatus();
            if (failed == 0)
            {
                Services.Notify("Game data updated", "Ships, equipment, maps and translations are current.", ToastKind.Success);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestNetworkAsync()
    {
        IsBusy = true;
        NetworkResult = "Checking…";
        try
        {
            if (Services.Tunnel is { } tunnel)
            {
                await tunnel.ConnectRelayAsync(CancellationToken.None);
                Services.RefreshRelayStatus();
            }

            var trace = await CdnHttpClient.Instance.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace");
            var fields = trace.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            var ip = fields.GetValueOrDefault("ip", "?");
            var country = fields.GetValueOrDefault("loc", "?");

            var cdn = "not checked (no world)";
            if (Services.Host is { } host)
            {
                try
                {
                    using var response = await CdnHttpClient.Instance.GetAsync($"https://{host}/kcs2/version.json");
                    cdn = response.IsSuccessStatusCode ? "reachable" : $"refused ({(int)response.StatusCode})";
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    cdn = "unreachable";
                }
            }

            NetworkResult = $"Exit address {ip} ({country}) · CDN {cdn}";
            Services.Notify(cdn == "reachable" ? "Connection works" : "CDN not reachable", NetworkResult,
                cdn == "reachable" ? ToastKind.Success : ToastKind.Error);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            NetworkResult = "Failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SwitchRelayAsync()
    {
        if (Services.Tunnel is not { } tunnel)
        {
            return;
        }

        await tunnel.SwitchRelayAsync();
        Services.NetworkStatus = "JP relay · switching";
        await TestNetworkAsync();
    }

    private async Task DownloadAllAsync()
    {
        foreach (var row in Categories)
        {
            if (!await RunDownloadAsync(row))
            {
                break;
            }
        }
    }

    private Task DownloadAsync(CategoryRow row) => RunDownloadAsync(row);

    private async Task<bool> RunDownloadAsync(CategoryRow row)
    {
        var library = Services.Library;
        var host = Services.Host;
        if (library is null || host is null)
        {
            return false;
        }

        IsBusy = true;
        row.Busy = true;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        try
        {
            var entries = library.Catalog.Where(e => e.Category == row.Category).ToList();
            row.Status = "Downloading…";
            var done = 0;
            var progress = new Progress<DownloadStats>(s =>
            {
                done = s.Downloaded + s.Skipped + s.Missing + s.Failed;
                row.Progress = (double)done / Math.Max(1, entries.Count);
                row.Status = $"{done:N0} / {entries.Count:N0} · {s.Bytes / 1048576.0:N1} MiB";
            });

            var stats = await library.FetchAsync(host, entries, Services.AppendLog, progress,
                Services.Settings.Concurrency, token);

            // gauge sprites and furniture pictures are named inside files the first pass fetched
            var derived = Derived(row.Category, library.Workspace.RawRoot, entries);
            if (derived.Count > 0)
            {
                stats += await library.FetchAsync(host, derived, Services.AppendLog, null, Services.Settings.Concurrency, token);
            }

            row.Status = stats.Failed == 0 ? "" : $"{stats.Failed} failed";
            Services.RefreshRelayStatus();
            Services.Notify($"{row.Name} downloaded",
                $"{stats.Downloaded:N0} new files ({stats.Bytes / 1048576.0:N1} MiB), {stats.Failed} failed.",
                stats.Failed == 0 ? ToastKind.Success : ToastKind.Error);
            return true;
        }
        catch (OperationCanceledException)
        {
            row.Status = "Stopped";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            row.Status = "Failed";
            Services.Notify("Download failed", ex.Message, ToastKind.Error);
            return false;
        }
        finally
        {
            row.Busy = false;
            IsBusy = false;
            await CountAsync();
        }
    }

    private static List<AssetEntry> Derived(AssetCategory category, string rawRoot, IReadOnlyList<AssetEntry> entries) =>
        category switch
        {
            AssetCategory.Maps => DerivedCatalog.GaugeImages(rawRoot, entries.Where(e => e.Group == "gauge")).ToList(),
            AssetCategory.Furniture => DerivedCatalog.FurniturePictures(rawRoot, entries.Where(e => e.AssetType == "scripts_json")).ToList(),
            _ => [],
        };

    /// <summary>organizes everything downloaded so far into the output tree, as <c>ksttk-dl extract</c> does.</summary>
    private async Task ExtractAsync()
    {
        var library = Services.Library;
        if (library is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var workspace = library.Workspace;
            var stats = await Task.Run(() =>
            {
                var categories = Enum.GetValues<AssetCategory>().ToHashSet();
                var catalog = library.Catalog.ToList();
                catalog.AddRange(DerivedCatalog.GaugeImages(workspace.RawRoot, library.Catalog.Where(e => e.Group == "gauge")));
                catalog.AddRange(DerivedCatalog.FurniturePictures(workspace.RawRoot,
                    library.Catalog.Where(e => e.AssetType == "scripts_json")));
                var context = new ExtractionContext(workspace.RawRoot, workspace.OutputRoot, false, Services.AppendLog);
                return new Extractor(context, library.Start2, library.Translations).Extract(catalog, categories);
            });

            Services.Notify("Output organized",
                $"{stats.Files:N0} new files and {stats.Frames:N0} atlas frames in {workspace.OutputRoot}", ToastKind.Success);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
