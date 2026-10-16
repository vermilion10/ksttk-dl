using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using KsttkDl.Core;
using KsttkDl.Core.Catalog;
using KsttkDl.Core.Download;
using KsttkDl.Core.Fetch;
using KsttkDl.Core.MasterData;
using KsttkDl.Core.Tunnel;

namespace KsttkDl.App.Infrastructure;

public enum ToastKind
{
    Info,
    Success,
    Error,
}

public sealed record Toast(string Title, string Message, ToastKind Kind);

/// <summary> the app's shared state: settings, the loaded library, the network route, the log and notifications. pages read from here instead of passing services around, since there is exactly one of each </summary>
public sealed class AppServices : Observable
{
    private const int LogLimit = 500;

    private GameLibrary? _library;
    private string _networkStatus = "";
    private bool _loading;
    private JpTunnel? _tunnel;

    private AppServices()
    {
        Fetcher = new FetchQueue(this);
    }

    public static AppServices Current { get; } = new();

    public AppSettings Settings { get; private set; } = AppSettings.Load();

    public Workspace Workspace => new(Settings.DataFolder, Settings.OutputFolder);

    /// <summary>null until master data has been downloaded and loaded.</summary>
    public GameLibrary? Library
    {
        get => _library;
        private set
        {
            if (Set(ref _library, value))
            {
                LibraryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? LibraryChanged;

    public bool IsLoading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    public FetchQueue Fetcher { get; }

    public ObservableCollection<string> Log { get; } = [];

    public ObservableCollection<Toast> Toasts { get; } = [];

    /// <summary>a short line for the title bar: the route requests take.</summary>
    public string NetworkStatus
    {
        get => _networkStatus;
        set => Set(ref _networkStatus, value);
    }

    public JpTunnel? Tunnel => _tunnel;

    public WorldServer? Server =>
        _library?.Translations.Servers.FirstOrDefault(s => s.Num == Settings.World)
        ?? _library?.Translations.Servers.FirstOrDefault();

    public string? Host => Server?.Host;

    public void UpdateSettings(AppSettings settings)
    {
        var reload = settings.DataFolder != Settings.DataFolder
                     || settings.OutputFolder != Settings.OutputFolder
                     || settings.IncludeRare != Settings.IncludeRare;
        Settings = settings;
        settings.Save();
        ApplyNetwork();
        Raise(nameof(Settings));
        Raise(nameof(Server));

        if (reload)
        {
            _ = LoadLibraryAsync();
        }
    }

    public void ApplyNetwork()
    {
        var old = _tunnel;
        _tunnel = null;
        if (old is not null)
        {
            _ = old.DisposeAsync().AsTask();
        }

        switch (Settings.Network)
        {
            case NetworkMode.JpRelay:
                _tunnel = new JpTunnel(Settings.DataFolder, AppendLog);
                CdnHttpClient.UseTunnel(_tunnel);
                NetworkStatus = "JP relay · not connected yet";
                break;
            case NetworkMode.Proxy when Settings.ProxyUrl.Length > 0:
                try
                {
                    CdnHttpClient.UseProxy(Settings.ProxyUrl);
                    NetworkStatus = "Proxy · " + Settings.ProxyUrl;
                }
                catch (ArgumentException ex)
                {
                    CdnHttpClient.UseDirect();
                    NetworkStatus = "Direct · proxy setting is invalid";
                    Notify("Proxy not used", ex.Message, ToastKind.Error);
                }

                break;
            default:
                CdnHttpClient.UseDirect();
                NetworkStatus = "Direct connection";
                break;
        }

        Raise(nameof(Tunnel));
    }

    /// <summary>reflects the relay in the title bar once a tunnel is up.</summary>
    public void RefreshRelayStatus()
    {
        if (_tunnel?.Relay is { } relay)
        {
            NetworkStatus = $"JP relay · {relay.HostName}";
        }
    }

    public async Task LoadLibraryAsync(VersionInfo? versions = null)
    {
        IsLoading = true;
        try
        {
            var workspace = Workspace;
            if (!workspace.Data.IsPopulated)
            {
                Library = null;
                return;
            }

            var includeRare = Settings.IncludeRare;
            Library = await Task.Run(() => GameLibrary.Load(workspace, includeRare, versions));
            Raise(nameof(Server));
        }
        catch (Exception ex)
        {
            Library = null;
            Notify("Could not load the game data", ex.Message, ToastKind.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void AppendLog(string line)
    {
        OnUi(() =>
        {
            Log.Add($"{DateTime.Now:HH:mm:ss}  {line.Trim()}");
            while (Log.Count > LogLimit)
            {
                Log.RemoveAt(0);
            }
        });
    }

    public void Notify(string title, string message, ToastKind kind = ToastKind.Info)
    {
        OnUi(() =>
        {
            var toast = new Toast(title, message, kind);
            Toasts.Add(toast);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 8 : 4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Toasts.Remove(toast);
            };
            timer.Start();
        });
    }

    public static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public async Task ShutdownAsync()
    {
        if (_tunnel is not null)
        {
            await _tunnel.DisposeAsync();
        }
    }
}

/// <summary> collects small fetch requests from the pages (the cards scrolled into view, the ship that was opened) and downloads them in batches, so scrolling a gallery turns into a few downloads rather than hundreds </summary>
public sealed class FetchQueue(AppServices services)
{
    private readonly object _lock = new();
    private readonly List<(AssetEntry Entry, Action Done)> _pending = [];
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private bool _scheduled;
    private DateTime _lastErrorToast = DateTime.MinValue;

    /// <summary>queues entries that are not on disk yet; <paramref name="done"/> runs on the ui thread once the batch holding them finishes.</summary>
    public void Request(IEnumerable<AssetEntry> entries, Action done)
    {
        var library = services.Library;
        if (library is null)
        {
            return;
        }

        var any = false;
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                if (library.IsDone(entry) || !_queued.Add(entry.Path))
                {
                    continue;
                }

                _pending.Add((entry, done));
                any = true;
            }

            if (!any || _scheduled)
            {
                return;
            }

            _scheduled = true;
        }

        _ = RunAsync();
    }

    /// <summary>fetches entries right away and waits for them.</summary>
    public async Task<DownloadStats> FetchNowAsync(IReadOnlyCollection<AssetEntry> entries, CancellationToken cancellationToken = default)
    {
        var library = services.Library ?? throw new InvalidOperationException("The game data is not loaded.");
        var host = services.Host ?? throw new InvalidOperationException("No world is selected.");
        try
        {
            return await library.FetchAsync(host, entries, services.AppendLog,
                concurrency: services.Settings.Concurrency, cancellationToken: cancellationToken);
        }
        finally
        {
            services.RefreshRelayStatus();
        }
    }

    private async Task RunAsync()
    {
        // let a burst of requests (a gallery row coming into view) gather first
        await Task.Delay(250);

        while (true)
        {
            List<(AssetEntry Entry, Action Done)> batch;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    _scheduled = false;
                    return;
                }

                batch = [.. _pending];
                _pending.Clear();
            }

            try
            {
                var stats = await FetchNowAsync(batch.Select(b => b.Entry).ToList());
                if (stats.Failed > 0)
                {
                    ReportFailure($"{stats.Failed} file(s) could not be downloaded.");
                }
            }
            catch (Exception ex)
            {
                ReportFailure(ex.Message);
            }
            finally
            {
                lock (_lock)
                {
                    foreach (var (entry, _) in batch)
                    {
                        _queued.Remove(entry.Path);
                    }
                }

                foreach (var done in batch.Select(b => b.Done).Distinct())
                {
                    AppServices.OnUi(done);
                }
            }
        }
    }

    private void ReportFailure(string message)
    {
        services.AppendLog(message);
        if (DateTime.UtcNow - _lastErrorToast < TimeSpan.FromSeconds(20))
        {
            return;
        }

        _lastErrorToast = DateTime.UtcNow;
        var hint = services.Settings.Network == NetworkMode.Direct
            ? " The CDN only answers Japanese addresses; try the JP relay in Settings."
            : "";
        services.Notify("Download failed", message + hint, ToastKind.Error);
    }
}
