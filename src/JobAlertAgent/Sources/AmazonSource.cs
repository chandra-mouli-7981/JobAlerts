using System.Globalization;
using System.Text.Json;
using JobAlertAgent.Infrastructure;
using JobAlertAgent.Models;

namespace JobAlertAgent.Sources;

/// <summary>
/// amazon.jobs search endpoint (GET /en/search.json). One query per keyword term, newest first,
/// optionally restricted to ISO-3 country codes.
/// </summary>
public sealed class AmazonSource : IJobSource
{
    private const string BaseUrl = "https://www.amazon.jobs";
    private const int PageSize = 100;

    private readonly ResilientHttp _http;

    public AmazonSource(ResilientHttp http) => _http = http;

    /// <summary>Safety cap on pages fetched per keyword (PageSize results each).</summary>
    public int MaxPagesPerTerm { get; init; } = 5;

    public string Provider => ProviderNames.Amazon;

    public async Task<IReadOnlyList<Job>> FetchAsync(
        SourceConfig source, IReadOnlyList<string> searchTerms, CancellationToken ct)
    {
        var byId = new Dictionary<string, Job>(StringComparer.Ordinal);

        foreach (var term in searchTerms)
        {
            for (var page = 0; page < MaxPagesPerTerm; page++)
            {
                var url = BuildUrl(source, term, page * PageSize);
                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                var jobs = Parse(source, json, out var rawCount);

                foreach (var job in jobs)
                    byId.TryAdd(job.Id, job);

                if (rawCount < PageSize) break;
            }
        }

        return byId.Values.ToList();
    }

    internal static string BuildUrl(SourceConfig source, string term, int offset)
    {
        var url = $"{BaseUrl}/en/search.json?base_query={Uri.EscapeDataString(term)}"
                  + $"&sort=recent&result_limit={PageSize}&offset={offset}";
        foreach (var code in source.CountryCodes)
            url += $"&normalized_country_code%5B%5D={Uri.EscapeDataString(code)}";
        return url;
    }

    /// <param name="rawCount">Number of jobs in the response before the client-side country filter.</param>
    internal static IReadOnlyList<Job> Parse(SourceConfig source, string json, out int rawCount)
    {
        using var doc = JsonDocument.Parse(json);
        var jobs = new List<Job>();
        rawCount = 0;

        foreach (var item in doc.RootElement.Prop("jobs").Items())
        {
            rawCount++;

            var id = item.Str("id_icims") ?? item.Str("id");
            var title = item.Str("title");
            var path = item.Str("job_path");
            if (id is null || title is null || path is null) continue;

            // The server-side country filter was observed to be reliable, but enforce it here too.
            if (source.CountryCodes.Count > 0)
            {
                var country = item.Str("country_code");
                if (country is null || !source.CountryCodes.Contains(country, StringComparer.OrdinalIgnoreCase))
                    continue;
            }

            var description = TextUtil.Join(
                TextUtil.HtmlToText(item.Str("description")),
                TextUtil.HtmlToText(item.Str("basic_qualifications")),
                TextUtil.HtmlToText(item.Str("preferred_qualifications")));

            jobs.Add(new Job
            {
                SourceName = source.Name,
                Id = id,
                Title = title.Trim(),
                Location = item.Str("normalized_location") ?? item.Str("location") ?? "",
                Url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : BaseUrl + path,
                Description = description,
                PostedAt = DateTimeOffset.TryParseExact(item.Str("posted_date"), "MMMM d, yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null,
            });
        }

        return jobs;
    }
}
