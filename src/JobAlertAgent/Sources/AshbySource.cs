using System.Globalization;
using System.Text.Json;
using JobAlertAgent.Infrastructure;
using JobAlertAgent.Models;

namespace JobAlertAgent.Sources;

/// <summary>Ashby public posting API: GET /posting-api/job-board/{slug}</summary>
public sealed class AshbySource : IJobSource
{
    private readonly ResilientHttp _http;

    public AshbySource(ResilientHttp http) => _http = http;

    public string Provider => ProviderNames.Ashby;

    public async Task<IReadOnlyList<Job>> FetchAsync(
        SourceConfig source, IReadOnlyList<string> searchTerms, CancellationToken ct)
    {
        var url = $"https://api.ashbyhq.com/posting-api/job-board/{Uri.EscapeDataString(source.Slug!)}";
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return Parse(source, json);
    }

    internal static IReadOnlyList<Job> Parse(SourceConfig source, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var jobs = new List<Job>();

        foreach (var item in doc.RootElement.Prop("jobs").Items())
        {
            // Unlisted postings are not publicly visible.
            if (item.Str("isListed") == "false") continue;

            var id = item.Str("id");
            var title = item.Str("title");
            var link = item.Str("jobUrl") ?? item.Str("applyUrl");
            if (id is null || title is null || link is null) continue;

            var description = item.Str("descriptionPlain");
            if (string.IsNullOrWhiteSpace(description))
                description = TextUtil.HtmlToText(item.Str("descriptionHtml"));

            var location = item.Str("location") ?? "";
            if (item.Str("isRemote") == "true" && !location.Contains("remote", StringComparison.OrdinalIgnoreCase))
                location = string.IsNullOrEmpty(location) ? "Remote" : $"{location} (Remote)";

            jobs.Add(new Job
            {
                SourceName = source.Name,
                Id = id,
                Title = title.Trim(),
                Location = location,
                Url = link,
                Description = description,
                PostedAt = DateTimeOffset.TryParse(item.Str("publishedAt"), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt) ? dt : null,
            });
        }

        return jobs;
    }
}
