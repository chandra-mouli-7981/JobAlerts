using System.Net;

namespace JobAlertAgent.Infrastructure;

/// <summary>
/// Thin wrapper over <see cref="HttpClient"/> that retries transient failures
/// (429, 5xx, 408, network errors and timeouts) with exponential backoff.
/// </summary>
public sealed class ResilientHttp
{
    private readonly HttpClient _client;

    public ResilientHttp(HttpClient client) => _client = client;

    public int MaxAttempts { get; init; } = 4;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: replaced in unit tests so retries do not actually sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (d, ct) => Task.Delay(d, ct);

    public Task<string> GetStringAsync(string url, CancellationToken ct = default) =>
        SendAsync(url, () => new HttpRequestMessage(HttpMethod.Get, url), ct);

    /// <summary>POSTs a JSON body (same retry behaviour as <see cref="GetStringAsync"/>).</summary>
    public Task<string> PostJsonAsync(string url, string json, CancellationToken ct = default) =>
        SendAsync(url, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        }, ct);

    private async Task<string> SendAsync(string url, Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            TimeSpan? retryAfter = null;
            try
            {
                using var request = createRequest();
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                var message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {url}";
                if (!IsTransient(response.StatusCode))
                    throw new HttpRequestException(message, null, response.StatusCode);

                lastError = new HttpRequestException(message, null, response.StatusCode);
                retryAfter = response.Headers.RetryAfter?.Delta;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } code && !IsTransient(code))
            {
                throw; // permanent failure such as 404: do not retry
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException($"Request timed out for {url}", ex);
            }

            if (attempt < MaxAttempts)
            {
                var backoff = TimeSpan.FromTicks(BaseDelay.Ticks * (1L << (attempt - 1)));
                var wait = retryAfter ?? backoff;
                if (wait > MaxDelay) wait = MaxDelay;
                await Delay(wait, ct).ConfigureAwait(false);
            }
        }

        throw new HttpRequestException(
            $"Giving up after {MaxAttempts} attempts: {lastError?.Message}", lastError);
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests
        || code == HttpStatusCode.RequestTimeout
        || (int)code >= 500;
}
