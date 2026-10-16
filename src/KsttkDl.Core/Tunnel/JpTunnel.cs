using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace KsttkDl.Core.Tunnel;

/// <summary> keeps one tunnel to a Japanese VPN Gate relay up on demand and opens tcp connections through it. the relay that worked last is saved and tried first next time, so the CDN keeps seeing the same exit address; when a tunnel drops, the next connection brings up a new one </summary>
public sealed class JpTunnel(string stateDirectory, Action<string> log) : IAsyncDisposable
{
    private const string Country = "JP";
    private const int MaxAttempts = 8;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(25);

    private static readonly HttpClient ListClient = new() { Timeout = TimeSpan.FromSeconds(90) };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _avoided = [];
    private Link? _current;

    private sealed record Link(VpnGateServer Relay, OpenVpnSession Session, TcpStack Stack);

    private sealed record SavedRelay(string HostName, string Config);

    private string StatePath => Path.Combine(stateDirectory, "relay.json");

    /// <summary>the relay in use, or null while no tunnel is up.</summary>
    public VpnGateServer? Relay => _current?.Relay;

    /// <summary> opens a tcp connection to <paramref name="endPoint"/> through the tunnel. names resolve with the system resolver: the CDN hosts answer the same everywhere, only the connection has to come from Japan </summary>
    public async Task<Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken cancellationToken)
    {
        var link = await EnsureConnectedAsync(cancellationToken);

        var addresses = await Dns.GetHostAddressesAsync(endPoint.Host, AddressFamily.InterNetwork, cancellationToken);
        if (addresses.Length == 0)
        {
            throw new IOException($"{endPoint.Host} has no IPv4 address.");
        }

        return await link.Stack.ConnectAsync(addresses[0], endPoint.Port, cancellationToken);
    }

    /// <summary>brings a tunnel up if there is none, and returns the relay it uses.</summary>
    public async Task<VpnGateServer> ConnectRelayAsync(CancellationToken cancellationToken) =>
        (await EnsureConnectedAsync(cancellationToken)).Relay;

    /// <summary>drops the current relay and the saved preference, so the next connection uses the next best one.</summary>
    public async Task SwitchRelayAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_current is { } link)
            {
                _avoided.Add(link.Relay.HostName);
                _current = null;
                await link.Session.DisposeAsync();
            }

            ForgetRelay();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ForgetRelay()
    {
        try
        {
            File.Delete(StatePath);
        }
        catch (IOException)
        {
        }
    }

    private async Task<Link> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_current is { } live && !live.Session.Closed.IsCompleted)
        {
            return live;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current is { } raced && !raced.Session.Closed.IsCompleted)
            {
                return raced;
            }

            _current = null;
            var errors = new List<string>();

            foreach (var relay in (await CandidatesAsync(cancellationToken)).Take(MaxAttempts))
            {
                cancellationToken.ThrowIfCancellationRequested();
                log($"Connecting to relay {relay.HostName} ({relay.Ip})");

                try
                {
                    var link = await OpenAsync(relay, cancellationToken);
                    log($"Tunnel up via {relay.HostName}, tunnel address {link.Session.LocalAddress}");
                    SaveRelay(relay);
                    _current = link;
                    return link;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    log($"  {relay.HostName} failed: {ex.Message}");
                    errors.Add($"{relay.HostName}: {ex.Message}");
                    _avoided.Add(relay.HostName);
                }
            }

            throw new IOException(errors.Count == 0
                ? "No Japanese VPN Gate relays are available."
                : "Every relay tried failed: " + string.Join("; ", errors));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Link> OpenAsync(VpnGateServer relay, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(AttemptTimeout);

        // packets can arrive before the stack exists, so the callback looks it up when they do
        TcpStack? stack = null;
        var session = await OpenVpnSession.ConnectAsync(
            OvpnConfig.Parse(relay.Config), packet => stack?.Receive(packet), log, attempt.Token);

        stack = new TcpStack(session.LocalAddress, packet => session.Send(packet));

        _ = session.Closed.ContinueWith(t =>
        {
            var reason = t.Exception?.InnerException ?? new IOException("The tunnel closed.");
            stack.Fail(reason);
            stack.Dispose();
            log($"Tunnel via {relay.HostName} closed: {reason.Message}");
        }, TaskScheduler.Default);

        return new Link(relay, session, stack);
    }

    private async Task<IReadOnlyList<VpnGateServer>> CandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<VpnGateServer>();
        var saved = LoadRelay();

        // VPN Gate relays sometimes refuse a login and accept the next, so the saved one gets a second try before a new exit address is used
        if (saved is not null && !_avoided.Contains(saved.HostName))
        {
            candidates.Add(saved);
            candidates.Add(saved);
        }

        try
        {
            var listed = VpnGate.TcpRelaysIn(await VpnGate.FetchAsync(ListClient, cancellationToken), Country);
            log($"VPN Gate lists {listed.Count} Japanese TCP relays");
            candidates.AddRange(listed.Where(r => !_avoided.Contains(r.HostName) && r.HostName != saved?.HostName));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (candidates.Count == 0)
            {
                throw new IOException($"Could not fetch the VPN Gate relay list: {ex.Message}", ex);
            }

            log($"Could not fetch the VPN Gate relay list ({ex.Message}); trying the saved relay only");
        }

        return candidates;
    }

    private VpnGateServer? LoadRelay()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<SavedRelay>(File.ReadAllText(StatePath));
            return saved is { HostName.Length: > 0, Config.Length: > 0 } && OvpnConfig.TryParse(saved.Config, out var config)
                ? new VpnGateServer(saved.HostName, config.Host, 0, 0, 0, Country, saved.Config)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveRelay(VpnGateServer relay)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new SavedRelay(relay.HostName, relay.Config)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Could not save the relay choice: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_current is { } link)
        {
            _current = null;
            await link.Session.DisposeAsync();
        }
    }
}
