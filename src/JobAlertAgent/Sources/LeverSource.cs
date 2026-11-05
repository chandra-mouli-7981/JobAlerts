using System.Text.Json;
using JobAlertAgent.Infrastructure;
using JobAlertAgent.Models;

namespace JobAlertAgent.Sources;

/// <summary>Lever public postings API: GET /v0/postings/{slug}?mode=json</summary>
public sealed class LeverSource : IJobSource
{
    private readonly ResilientHttp _http;

    public LeverSource(ResilientHttp http) => _http = http;

    public string Provider => ProviderNames.Lever;

    public async Task<IReadOnlyList<Job>> FetchAsync(
        SourceConfig source, IReadOnlyList<string> searchTerms, CancellationToken ct)
    {
        var url = $"https://api.lever.co/v0/postings/{Uri.EscapeDataString(source.Slug!)}?mode=json";
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return Parse(source, json);
    }

    internal static IReadOnlyList<Job> Parse(SourceConfig source, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var jobs = new List<Job>();

        // Lever returns a bare array.
        IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

        foreach (var item in items)
        {
            var id = item.Str("id");
            var title = item.Str("text");
            var link = item.Str("hostedUrl");
            if (id is null || title is null || link is null) continue;

            // Real content is often in "lists" (responsibilities / requirements) rather than "description".
            var listText = string.Join("\n", item.Prop("lists").Items()
                .Select(l => TextUtil.Join(l.Str("text"), TextUtil.HtmlToText(l.Str("content")))));

            var description = TextUtil.Join(
                item.Str("openingPlain"),
                item.Str("descriptionPlain") ?? TextUtil.HtmlToText(item.Str("description")),
                listText,
                item.Str("additionalPlain"));

            var categories = item.Prop("categories");
            var locations = categories?.Prop("allLocations").Items()
                .Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var location = locations is { Count: > 0 }
                ? string.Join(" | ", locations)
                : categories?.Str("location") ?? "";

            var created = item.Long("createdAt");

            jobs.Add(new Job
            {
                SourceName = source.Name,
                Id = id,
                Title = title.Trim(),
                Location = location,
                Url = link,
                Description = description,
                PostedAt = created is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
            });
        }

        return jobs;
    }
}
