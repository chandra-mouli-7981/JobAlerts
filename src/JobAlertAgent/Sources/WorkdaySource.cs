using System.Text.Json;
using System.Text.RegularExpressions;
using JobAlertAgent.Infrastructure;
using JobAlertAgent.Matching;
using JobAlertAgent.Models;

namespace JobAlertAgent.Sources;

/// <summary>
/// Workday-hosted career sites: POST https://{tenant}.{wdN}.myworkdayjobs.com/wday/cxs/{tenant}/{site}/jobs.
/// The slug is "tenant/wdN/site". The list carries no description, and Workday's own keyword search is far too
/// loose to trust (it matches ".NET" against unrelated postings), so the whole list is read and keywords are
/// matched against the job TITLE only. When the source asks for India, the tenant's own India location facet
/// is looked up and applied server-side, so that multi-location postings ("2 Locations") are not lost.
/// </summary>
public sealed partial class WorkdaySource : IJobSource
{
    private const int PageSize = 20; // Workday rejects larger values

    private readonly ResilientHttp _http;

    public WorkdaySource(ResilientHttp http) => _http = http;

    /// <summary>Safety cap on pages fetched per source (PageSize results each).</summary>
    public int MaxPages { get; init; } = 30;

    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public string Provider => ProviderNames.Workday;

    public async Task<IReadOnlyList<Job>> FetchAsync(
        SourceConfig source, IReadOnlyList<string> searchTerms, CancellationToken ct)
    {
        var parts = (source.Slug ?? "").Split('/');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException(
                $"Workday source '{source.Name}' needs a link like https://<company>.wd5.myworkdayjobs.com/<SiteName>.");

        var (tenant, wd, site) = (parts[0], parts[1], parts[2]);
        var origin = $"https://{tenant}.{wd}.myworkdayjobs.com";
        var api = $"{origin}/wday/cxs/{Uri.EscapeDataString(tenant)}/{Uri.EscapeDataString(site)}/jobs";

        // Narrow to India server-side when requested and the tenant offers an India facet.
        IReadOnlyDictionary<string, string[]>? indiaFacet = null;
        if (source.Locations.Any(l => l.Trim().Equals("India", StringComparison.OrdinalIgnoreCase)))
        {
            var probe = await _http.PostJsonAsync(api, BuildBody(null, "", 0, 1), ct).ConfigureAwait(false);
            indiaFacet = FindIndiaFacets(probe);
        }

        var byId = new Dictionary<string, Job>(StringComparer.Ordinal);

        var knownTotal = 0; // Workday reports "total" on the first page only
        for (var page = 0; page < MaxPages; page++)
        {
            if (page > 0 && PageDelay > TimeSpan.Zero) await Task.Delay(PageDelay, ct).ConfigureAwait(false);

            var json = await _http.PostJsonAsync(api, BuildBody(indiaFacet, "", page * PageSize, PageSize), ct)
                .ConfigureAwait(false);
            var jobs = Parse(source, origin, site, json, indiaFacet is not null, Clock(), out var rawCount, out var total);

            foreach (var job in jobs) byId.TryAdd(job.Id, job);

            if (page == 0) knownTotal = total;
            if (rawCount < PageSize || (page + 1) * PageSize >= knownTotal) break;
        }

        return byId.Values.ToList();
    }

    internal static string BuildBody(IReadOnlyDictionary<string, string[]>? facets, string term, int offset, int limit = PageSize)
        => JsonSerializer.Serialize(new
        {
            appliedFacets = facets ?? new Dictionary<string, string[]>(),
            limit,
            offset,
            searchText = term,
        });

    [GeneratedRegex(@"(?<![A-Za-z0-9])IND(?![A-Za-z0-9])")]
    private static partial Regex IndCode();

    /// <summary>
    /// Works out which location filter selects India jobs. The facet's name and shape differ per tenant:
    /// some list a single "India" country value, others only cities ("Bangalore, Karnataka, India") or
    /// codes ("IND PUNE"). An exact "India" value wins; otherwise every India-looking value of the best
    /// location facet is selected (values of one facet are OR-ed by Workday). Null when nothing is found.
    /// </summary>
    internal static IReadOnlyDictionary<string, string[]>? FindIndiaFacets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var indiaPlace = LocationFilter.BuildRegex(new[] { "India" })!;

        var exact = new List<(string Parameter, string Id)>();
        var byParameter = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Walk(JsonElement node, string? parameter)
        {
            parameter = node.Str("facetParameter") ?? parameter;
            foreach (var value in node.Prop("values").Items())
            {
                if (value.Prop("values") is { ValueKind: JsonValueKind.Array })
                {
                    Walk(value, parameter);
                    continue;
                }

                var descriptor = value.Str("descriptor")?.Trim();
                var id = value.Str("id");
                if (parameter is null || string.IsNullOrEmpty(descriptor) || string.IsNullOrEmpty(id)) continue;
                if (!parameter.Contains("ocation", StringComparison.Ordinal)
                    && !parameter.Contains("ountry", StringComparison.Ordinal)) continue;

                if (descriptor.Equals("India", StringComparison.OrdinalIgnoreCase))
                    exact.Add((parameter, id));
                else if (indiaPlace.IsMatch(descriptor) || IndCode().IsMatch(descriptor))
                {
                    if (!byParameter.TryGetValue(parameter, out var list)) byParameter[parameter] = list = new();
                    list.Add(id);
                }
            }
        }

        foreach (var facet in doc.RootElement.Prop("facets").Items())
            Walk(facet, facet.Str("facetParameter"));

        if (exact.Count > 0)
            return new Dictionary<string, string[]> { [exact[0].Parameter] = new[] { exact[0].Id } };

        var best = byParameter.OrderByDescending(kv => kv.Value.Count).FirstOrDefault();
        return best.Value is null
            ? null
            : new Dictionary<string, string[]> { [best.Key] = best.Value.Distinct().ToArray() };
    }

    internal static IReadOnlyList<Job> Parse(
        SourceConfig source, string origin, string site, string json, bool indiaApplied, DateTimeOffset now,
        out int rawCount, out int total)
    {
        using var doc = JsonDocument.Parse(json);
        var jobs = new List<Job>();
        rawCount = 0;
        total = (int)(doc.RootElement.Long("total") ?? 0);
        var india = LocationFilter.BuildRegex(new[] { "India" })!;

        foreach (var item in doc.RootElement.Prop("jobPostings").Items())
        {
            rawCount++;

            var title = item.Str("title");
            var path = item.Str("externalPath");
            if (title is null || path is null) continue;

            var requisition = item.Prop("bulletFields").Items()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

            var location = item.Str("locationsText") ?? "";
            if (indiaApplied && !india.IsMatch(location))
                location = string.IsNullOrEmpty(location) ? "India" : $"{location}, India";

            jobs.Add(new Job
            {
                SourceName = source.Name,
                Id = requisition ?? path,
                Title = title.Trim(),
                Location = location,
                Url = $"{origin}/{Uri.EscapeDataString(site)}{path}",
                PostedAt = ParsePosted(item.Str("postedOn"), now),
            });
        }

        return jobs;
    }

    [GeneratedRegex(@"(\d+)\+?\s+Days?\s+Ago", RegexOptions.IgnoreCase)]
    private static partial Regex DaysAgo();

    /// <summary>"Posted Today", "Posted Yesterday", "Posted 3 Days Ago", "Posted 30+ Days Ago".</summary>
    internal static DateTimeOffset? ParsePosted(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("Today", StringComparison.OrdinalIgnoreCase)) return now.Date;
        if (text.Contains("Yesterday", StringComparison.OrdinalIgnoreCase)) return now.Date.AddDays(-1);
        var m = DaysAgo().Match(text);
        return m.Success ? now.Date.AddDays(-int.Parse(m.Groups[1].Value)) : null;
    }
}
