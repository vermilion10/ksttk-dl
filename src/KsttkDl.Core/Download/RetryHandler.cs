using System.Net;

namespace KsttkDl.Core.Download;

/// <summary> retries transiently-failing GETs with exponential backoff. a 404 is never retried: an (id, type) pair that does not exist for an entity is a normal outcome of enumerating the catalog </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private const int MaxRetries = 3;
    private const double BackoffFactorSeconds = 0.5;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0)
            {
                // 0.5s, 1s, 2s
                var delay = BackoffFactorSeconds * Math.Pow(2, attempt - 1);
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
            }

            response?.Dispose();

            // an HttpRequestMessage cannot be sent twice, so retries need their own clone
            using var attemptRequest = attempt == 0 ? request : Clone(request);

            try
            {
                response = await base.SendAsync(attemptRequest, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                continue;
            }
            catch (TaskCanceledException) when (attempt < MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                // per-request timeout rather than caller cancellation.
                continue;
            }

            var shouldRetry = request.Method == HttpMethod.Get
                && RetryableStatusCodes.Contains(response.StatusCode)
                && attempt < MaxRetries;

            if (!shouldRetry)
            {
                return response;
            }
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
        };

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
