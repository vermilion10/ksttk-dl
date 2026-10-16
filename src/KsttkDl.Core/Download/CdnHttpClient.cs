using System.Net;
using KsttkDl.Core.Tunnel;

namespace KsttkDl.Core.Download;

/// <summary> one pooled <see cref="HttpClient"/> for the process. the cdn is plain https and japan-only; without <see cref="UseProxy"/> or <see cref="UseTunnel"/> the machine's own network path (or the HTTP_PROXY / HTTPS_PROXY / ALL_PROXY environment variables) has to get it there </summary>
public static class CdnHttpClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private static readonly string[] SupportedSchemes = ["http", "https", "socks4", "socks4a", "socks5"];

    private static readonly object Gate = new();

    private static HttpClient? _client;
    private static IWebProxy? _proxy;
    private static JpTunnel? _tunnel;

    public static HttpClient Instance
    {
        get
        {
            lock (Gate)
            {
                return _client ??= Create();
            }
        }
    }

    /// <summary>routes every later request through <paramref name="proxyUrl"/>, e.g. <c>socks5://127.0.0.1:1080</c> or <c>http://user:pass@host:8080</c>.</summary>
    public static void UseProxy(string proxyUrl) => Configure(ParseProxy(proxyUrl), null);

    /// <summary>opens every later connection through <paramref name="tunnel"/>, so traffic leaves from its Japanese relay.</summary>
    public static void UseTunnel(JpTunnel tunnel) => Configure(null, tunnel);

    /// <summary>goes back to the machine's own network path.</summary>
    public static void UseDirect() => Configure(null, null);

    // the pooled handler is fixed once built, so a change of route builds a new client; requests already running finish on the old one
    private static void Configure(IWebProxy? proxy, JpTunnel? tunnel)
    {
        lock (Gate)
        {
            _proxy = proxy;
            _tunnel = tunnel;
            _client = null;
        }
    }

    public static IWebProxy ParseProxy(string proxyUrl)
    {
        // a bare host:port reads as an http proxy, the usual meaning when no scheme is given
        var text = proxyUrl.Contains("://", StringComparison.Ordinal) ? proxyUrl : "http://" + proxyUrl;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException($"'{proxyUrl}' is not a valid proxy address.");
        }

        if (!SupportedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported proxy scheme '{uri.Scheme}'. Use one of: {string.Join(", ", SupportedSchemes)}.");
        }

        // socks schemes have no default port, so Uri reports -1 when none is written
        if (uri.Port <= 0)
        {
            throw new ArgumentException($"'{proxyUrl}' needs a port, e.g. {uri.Scheme}://{uri.Host}:1080.");
        }

        // WebProxy ignores user info in the address, so credentials are lifted out and passed separately
        var address = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        var proxy = new WebProxy(address);

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        return proxy;
    }

    private static HttpClient Create()
    {
        var socketHandler = new SocketsHttpHandler
        {
            // above the download concurrency limit, so that limit paces the cdn rather than the connection pool
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.All,
        };

        if (_tunnel is { } tunnel)
        {
            socketHandler.UseProxy = false;

            // the first connection also brings the tunnel up (relay list, handshakes), which the tunnel bounds itself
            socketHandler.ConnectTimeout = TimeSpan.FromMinutes(5);
            socketHandler.ConnectCallback = (context, token) => new ValueTask<Stream>(tunnel.ConnectAsync(context.DnsEndPoint, token));
        }
        else if (_proxy is not null)
        {
            socketHandler.Proxy = _proxy;
            socketHandler.UseProxy = true;
        }

        var client = new HttpClient(new RetryHandler { InnerHandler = socketHandler })
        {
            Timeout = RequestTimeout,
        };

        // the cdn serves anything, but a browser-ish UA keeps the request shape close to what the game client sends.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

        return client;
    }
}
