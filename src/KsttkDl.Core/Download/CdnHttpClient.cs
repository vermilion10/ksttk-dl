using System.Net;

namespace KsttkDl.Core.Download;

/// <summary> one pooled <see cref="HttpClient"/> for the process. the cdn is plain https; its japan-only ip restriction is assumed to be handled by the machine's own network path, so there is no proxy configuration here </summary>
public static class CdnHttpClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private static readonly Lazy<HttpClient> LazyClient = new(Create);

    public static HttpClient Instance => LazyClient.Value;

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
