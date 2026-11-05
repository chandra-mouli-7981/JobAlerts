using System.Text.Json;
using JobAlertAgent.Infrastructure;
using JobAlertAgent.Models;

namespace JobAlertAgent.Sources;

/// <summary>
/// Microsoft careers (Eightfold PCSX): GET /api/pcsx/search. The endpoint rejects requests that have
/// no session cookie (HTTP 429), so the careers home page is visited first on the same
/// cookie-enabled <see cref="HttpClient"/>. Results carry no description, so the provider's own
/// keyword search decides what matches (<see cref="Job.ServerMatchedKeywords"/>).
/// </summary>
public sealed class MicrosoftSource : IJobSource
{
    private const string Host = "https://apply.careers.microsoft.com";
    private const int PageSize = 10; // the API silently caps 'num' at 10

    private readonly ResilientHttp _http;

    public MicrosoftSource(ResilientHttp http) => _http = http;

    /// <summary>Safety cap on pages fetched per keyword (PageSize results each).</summary>
    public int MaxPagesPerTerm { get; init; } = 15;

    /// <summary>Pause between page requests to stay under the rate limit.</summary>
    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    public string Provider => ProviderNames.Microsoft;

    public async Task<IReadOnlyList<Job>> FetchAsync(
        SourceConfig source, IReadOnlyList<string> searchTerms, CancellationToken ct)
    {
        // Establish the session cookie.
        await _http.GetStringAsync(Host + "/", ct).ConfigureAwait(false);

        var byId = new Dictionary<string, (Job Job, List<string> Keywords)>(StringComparer.Ordinal);

        foreach (var term in searchTerms)
        {
            for (var page = 0; page < MaxPagesPerTerm; page++)
            {
                if (PageDelay > TimeSpan.Zero) await Task.Delay(PageDelay, ct).ConfigureAwait(false);

                var url = BuildUrl(source, term, page * PageSize);
                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                var jobs = Parse(source, json, out var rawCount, out var total);

                foreach (var job in jobs)
                {
                    if (byId.TryGetValue(job.Id, out var existing))
                    {
                        if (!existing.Keywords.Contains(term, StringComparer.OrdinalIgnoreCase))
                            existing.Keywords.Add(term);
                    }
                    else
                    {
                        byId[job.Id] = (job, new List<string> { term });
                    }
                }

                if (rawCount < PageSize || (page + 1) * PageSize >= total) break;
            }
        }

        // Rebuild jobs so each carries every keyword the server matched it on.
        return byId.Values
            .Select(e => new Job
            {
                SourceName = e.Job.SourceName,
                Id = e.Job.Id,
                Title = e.Job.Title,
                Location = e.Job.Location,
                Url = e.Job.Url,
                Description = e.Job.Description,
                PostedAt = e.Job.PostedAt,
                ServerMatchedKeywords = e.Keywords,
            })
            .ToList();
    }

    internal static string BuildUrl(SourceConfig source, string term, int start) =>
        $"{Host}/api/pcsx/search?domain=microsoft.com&query={Uri.EscapeDataString(term)}"
        + $"&location={Uri.EscapeDataString(source.Location ?? "")}&start={start}&num={PageSize}";

    private static List<string> StringList(JsonElement? array) =>
        array.Items()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

    internal static IReadOnlyList<Job> Parse(SourceConfig source, string json, out int rawCount, out int total)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.Prop("data");
        var jobs = new List<Job>();
        rawCount = 0;
        total = (int)(data?.Long("count") ?? 0);

        foreach (var item in data?.Prop("positions").Items() ?? Enumerable.Empty<JsonElement>())
        {
            rawCount++;

            var id = item.Str("id");
            var title = item.Str("name");
            var path = item.Str("positionUrl");
            if (id is null || title is null || path is null) continue;

            var locations = StringList(item.Prop("standardizedLocations"));
            if (locations.Count == 0)
                locations = StringList(item.Prop("locations"));

            var posted = item.Long("postedTs");

            jobs.Add(new Job
            {
                SourceName = source.Name,
                Id = id,
                Title = title.Trim(),
                Location = string.Join(" | ", locations),
                Url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : Host + path,
                PostedAt = posted is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null,
            });
        }

        return jobs;
    }
}
