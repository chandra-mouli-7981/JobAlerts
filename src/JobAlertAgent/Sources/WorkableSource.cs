using System.Globalization;
using System.Text.Json;
using JobAlertAgent.Infrastructure;
using JobAlertAgent.Models;

namespace JobAlertAgent.Sources;

/// <summary>Workable public widget API: GET /api/v1/widget/accounts/{slug}?details=true</summary>
public sealed class WorkableSource : IJobSource
{
    private readonly ResilientHttp _http;

    public WorkableSource(ResilientHttp http) => _http = http;

    public string Provider => ProviderNames.Workable;

    public async Task<IReadOnlyList<Job>> FetchAsync(
        SourceConfig source, IReadOnlyList<string> searchTerms, CancellationToken ct)
    {
        var url = $"https://apply.workable.com/api/v1/widget/accounts/{Uri.EscapeDataString(source.Slug!)}?details=true";
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return Parse(source, json);
    }

    internal static IReadOnlyList<Job> Parse(SourceConfig source, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var jobs = new List<Job>();

        foreach (var item in doc.RootElement.Prop("jobs").Items())
        {
            var id = item.Str("shortcode");
            var title = item.Str("title");
            var link = item.Str("url") ?? item.Str("shortlink");
            if (id is null || title is null || link is null) continue;

            var location = string.Join(", ", new[] { item.Str("city"), item.Str("state"), item.Str("country") }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            if (item.Str("telecommuting") == "true")
                location = string.IsNullOrEmpty(location) ? "Remote" : $"{location} (Remote)";

            jobs.Add(new Job
            {
                SourceName = source.Name,
                Id = id,
                Title = title.Trim(),
                Location = location,
                Url = link,
                Description = TextUtil.HtmlToText(item.Str("description")),
                PostedAt = DateTimeOffset.TryParse(item.Str("published_on"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var dt) ? dt : null,
            });
        }

        return jobs;
    }
}
