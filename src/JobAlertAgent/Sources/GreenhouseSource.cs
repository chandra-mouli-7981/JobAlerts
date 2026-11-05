using System.Globalization;
using System.Text.Json;
using JobAlertAgent.Infrastructure;
using JobAlertAgent.Models;

namespace JobAlertAgent.Sources;

/// <summary>Greenhouse public job-board API: GET /v1/boards/{slug}/jobs?content=true</summary>
public sealed class GreenhouseSource : IJobSource
{
    private readonly ResilientHttp _http;

    public GreenhouseSource(ResilientHttp http) => _http = http;

    public string Provider => ProviderNames.Greenhouse;

    public async Task<IReadOnlyList<Job>> FetchAsync(
        SourceConfig source, IReadOnlyList<string> searchTerms, CancellationToken ct)
    {
        var url = $"https://boards-api.greenhouse.io/v1/boards/{Uri.EscapeDataString(source.Slug!)}/jobs?content=true";
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return Parse(source, json);
    }

    internal static IReadOnlyList<Job> Parse(SourceConfig source, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var jobs = new List<Job>();

        foreach (var item in doc.RootElement.Prop("jobs").Items())
        {
            var id = item.Str("id");
            var title = item.Str("title");
            var link = item.Str("absolute_url");
            if (id is null || title is null || link is null) continue;

            var posted = item.Str("first_published") ?? item.Str("updated_at");

            jobs.Add(new Job
            {
                SourceName = source.Name,
                Id = id,
                Title = title.Trim(),
                Location = item.Prop("location")?.Str("name") ?? "",
                Url = link,
                Description = TextUtil.HtmlToText(item.Str("content")),
                PostedAt = DateTimeOffset.TryParse(posted, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt) ? dt : null,
            });
        }

        return jobs;
    }
}
