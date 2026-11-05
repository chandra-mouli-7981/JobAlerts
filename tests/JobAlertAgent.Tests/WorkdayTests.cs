using System.Net;
using System.Text.Json;
using JobAlertAgent.Infrastructure;
using JobAlertAgent.Models;
using JobAlertAgent.Sources;

namespace JobAlertAgent.Tests;

public class WorkdayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    // ---- links -------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://nvidia.wd5.myworkdayjobs.com/NVIDIAExternalCareerSite", "nvidia/wd5/NVIDIAExternalCareerSite")]
    [InlineData("https://nvidia.wd5.myworkdayjobs.com/en-US/NVIDIAExternalCareerSite", "nvidia/wd5/NVIDIAExternalCareerSite")]
    [InlineData("https://salesforce.wd12.myworkdayjobs.com/External_Career_Site/job/India-Hyderabad/Dev_JR1?x=1", "salesforce/wd12/External_Career_Site")]
    [InlineData("salesforce.wd12.myworkdayjobs.com/en-GB/External_Career_Site", "salesforce/wd12/External_Career_Site")]
    [InlineData("https://jj.wd5.myworkdayjobs.com/jj", "jj/wd5/jj")]           // a site name that looks like a language code
    [InlineData("https://citi.wd5.myworkdayjobs.com/2", "citi/wd5/2")]
    [InlineData("https://jj.wd5.myworkdayjobs.com/en-US/jj", "jj/wd5/jj")]
    public void Workday_links_are_recognised(string input, string slug)
    {
        Assert.True(SourceUrlParser.TryParse(input, out var provider, out var s, out var error), error);
        Assert.Equal("workday", provider);
        Assert.Equal(slug, s);
    }

    [Theory]
    [InlineData("https://nvidia.wd5.myworkdayjobs.com/")]
    [InlineData("https://nvidia.wd5.myworkdayjobs.com/en-US")]
    public void Workday_link_without_site_name_is_rejected(string input)
    {
        Assert.False(SourceUrlParser.TryParse(input, out _, out _, out var error));
        Assert.Contains("site name", error);
    }

    [Fact]
    public void Workday_source_in_config_resolves_and_is_named_after_the_tenant()
    {
        var config = AppConfig.Parse("""{ "keywords": ["x"], "sources": [ "https://nvidia.wd5.myworkdayjobs.com/en-US/NVIDIAExternalCareerSite" ] }""");
        var s = config.Sources.Single();
        Assert.Null(s.ConfigError);
        Assert.Equal("workday", s.Provider);
        Assert.Equal("Nvidia", s.Name);
    }

    // ---- India facet discovery ---------------------------------------------------------------

    [Fact]
    public void Finds_an_exact_India_value_even_under_a_oddly_named_country_facet()
    {
        const string json = """
        { "facets": [
          { "facetParameter": "jobFamilyGroup", "values": [ { "descriptor": "India Ops", "id": "x" } ] },
          { "facetParameter": "CF_-_Country_from_Job_Posting_Location_Extended", "values": [
              { "descriptor": "Spain", "id": "s", "count": 3 },
              { "descriptor": "India", "id": "c4f7", "count": 9 } ] },
          { "facetParameter": "locationMainGroup", "values": [
              { "facetParameter": "locations", "values": [ { "descriptor": "India - Bangalore", "id": "b" } ] } ] }
        ] }
        """;

        var facets = WorkdaySource.FindIndiaFacets(json)!;

        var only = Assert.Single(facets);
        Assert.Equal("CF_-_Country_from_Job_Posting_Location_Extended", only.Key);
        Assert.Equal(new[] { "c4f7" }, only.Value);
    }

    [Fact]
    public void Finds_nested_hierarchy_value()
    {
        const string json = """
        { "facets": [ { "facetParameter": "locationMainGroup", "values": [
            { "facetParameter": "locationHierarchy1", "values": [ { "descriptor": "India", "id": "i1" }, { "descriptor": "Israel", "id": "i2" } ] },
            { "facetParameter": "locations", "values": [ { "descriptor": "India, Pune", "id": "p" } ] } ] } ] }
        """;

        var only = Assert.Single(WorkdaySource.FindIndiaFacets(json)!);
        Assert.Equal("locationHierarchy1", only.Key);
        Assert.Equal(new[] { "i1" }, only.Value);
    }

    [Fact]
    public void Without_an_India_country_value_all_india_cities_and_ind_codes_are_selected_but_not_indiana()
    {
        const string json = """
        { "facets": [ { "facetParameter": "locationMainGroup", "values": [
            { "facetParameter": "locations", "values": [
              { "descriptor": "Austin, Texas", "id": "a" },
              { "descriptor": "Indiana, United States of America (Virtual)", "id": "in" },
              { "descriptor": "Bangalore, Karnataka, India", "id": "b" },
              { "descriptor": "IND PUNE 5", "id": "p" },
              { "descriptor": "Hyderabad", "id": "h" } ] } ] } ] }
        """;

        var only = Assert.Single(WorkdaySource.FindIndiaFacets(json)!);
        Assert.Equal("locations", only.Key);
        Assert.Equal(new[] { "b", "p", "h" }, only.Value);
    }

    [Fact]
    public void No_india_values_means_no_facet() =>
        Assert.Null(WorkdaySource.FindIndiaFacets("""{ "facets": [ { "facetParameter": "locations", "values": [ { "descriptor": "Paris", "id": "p" } ] } ] }"""));

    // ---- parsing -----------------------------------------------------------------------------

    private const string Page = """
    { "total": 3, "jobPostings": [
      { "title": " Senior .NET Engineer ", "externalPath": "/job/India-Pune/Senior-NET-Engineer_JR100",
        "locationsText": "India, Pune", "postedOn": "Posted 3 Days Ago", "bulletFields": ["JR100"] },
      { "title": "Python Developer", "externalPath": "/job/Multi/Python-Developer_JR101",
        "locationsText": "2 Locations", "postedOn": "Posted Today", "bulletFields": [] },
      { "title": "No path" }
    ] }
    """;

    [Fact]
    public void Parses_jobs_and_adds_india_label_when_the_server_filtered_by_india()
    {
        var jobs = WorkdaySource.Parse(new SourceConfig { Name = "Nvidia" }, "https://nvidia.wd5.myworkdayjobs.com",
            "NVIDIAExternalCareerSite", Page, indiaApplied: true, Now, out var raw, out var total);

        Assert.Equal(3, raw);
        Assert.Equal(3, total);
        Assert.Equal(2, jobs.Count);
        Assert.Equal("Senior .NET Engineer", jobs[0].Title);
        Assert.Equal("JR100", jobs[0].Id);
        Assert.Equal("India, Pune", jobs[0].Location);
        Assert.Equal("https://nvidia.wd5.myworkdayjobs.com/NVIDIAExternalCareerSite/job/India-Pune/Senior-NET-Engineer_JR100", jobs[0].Url);
        Assert.Equal(Now.Date.AddDays(-3), jobs[0].PostedAt);
        Assert.Equal("/job/Multi/Python-Developer_JR101", jobs[1].Id);
        Assert.Equal("2 Locations, India", jobs[1].Location);
        Assert.Equal(Now.Date, jobs[1].PostedAt);
    }

    [Fact]
    public void Location_is_left_alone_when_no_server_side_filter_was_applied()
    {
        var jobs = WorkdaySource.Parse(new SourceConfig { Name = "X" }, "https://x.wd1.myworkdayjobs.com", "S", Page,
            indiaApplied: false, Now, out _, out _);

        Assert.Equal("2 Locations", jobs[1].Location);
    }

    [Theory]
    [InlineData("Posted Today", 0)]
    [InlineData("Posted Yesterday", 1)]
    [InlineData("Posted 5 Days Ago", 5)]
    [InlineData("Posted 30+ Days Ago", 30)]
    public void Posted_text_is_converted_to_a_date(string text, int daysAgo) =>
        Assert.Equal(Now.Date.AddDays(-daysAgo), WorkdaySource.ParsePosted(text, Now));

    [Fact]
    public void Unknown_posted_text_gives_null() => Assert.Null(WorkdaySource.ParsePosted("Sometime", Now));

    [Fact]
    public void Request_body_carries_facets_search_and_paging()
    {
        var body = WorkdaySource.BuildBody(new Dictionary<string, string[]> { ["locations"] = new[] { "a", "b" } }, "", 40);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(40, root.GetProperty("offset").GetInt32());
        Assert.Equal(20, root.GetProperty("limit").GetInt32());
        Assert.Equal("", root.GetProperty("searchText").GetString());
        Assert.Equal(2, root.GetProperty("appliedFacets").GetProperty("locations").GetArrayLength());
    }

    // ---- fetch (paging, India facet, total only on first page) -------------------------------

    private static string Posting(int n) =>
        $$"""{ "title": "Dev {{n}}", "externalPath": "/job/I/Dev-{{n}}_JR{{n}}", "locationsText": "India, Pune", "postedOn": "Posted Today", "bulletFields": ["JR{{n}}"] }""";

    [Fact]
    public async Task Fetch_applies_the_india_facet_and_pages_through_the_whole_list()
    {
        var bodies = new List<string>();
        var handler = new RecordingHandler(body =>
        {
            bodies.Add(body);
            using var doc = JsonDocument.Parse(body);
            var offset = doc.RootElement.GetProperty("offset").GetInt32();
            var limit = doc.RootElement.GetProperty("limit").GetInt32();

            if (limit == 1) // facet discovery request
                return """{ "total": 25, "jobPostings": [], "facets": [ { "facetParameter": "locationMainGroup", "values": [ { "facetParameter": "locationHierarchy1", "values": [ { "descriptor": "India", "id": "IN1" } ] } ] } ] }""";

            // 25 jobs: 20 on page one (with total), 5 on page two (Workday omits total there)
            var count = offset == 0 ? 20 : 5;
            var items = string.Join(",", Enumerable.Range(offset, count).Select(Posting));
            return $$"""{ "total": {{(offset == 0 ? 25 : 0)}}, "jobPostings": [ {{items}} ] }""";
        });
        var source = new WorkdaySource(new ResilientHttp(new HttpClient(handler))) { PageDelay = TimeSpan.Zero };
        var config = new SourceConfig { Name = "Acme", Provider = "workday", Slug = "acme/wd5/Careers", Locations = { "India" } };

        var jobs = await source.FetchAsync(config, new[] { "ignored" }, default);

        Assert.Equal(25, jobs.Count);
        Assert.Equal(25, jobs.Select(j => j.Id).Distinct().Count());
        Assert.All(jobs, j => Assert.Null(j.ServerMatchedKeywords)); // Workday search is not trusted: title-only matching
        Assert.Equal(3, bodies.Count);                                // 1 discovery + 2 pages
        Assert.Contains("IN1", bodies[1]);
        Assert.Contains("https://acme.wd5.myworkdayjobs.com/wday/cxs/acme/Careers/jobs", handler.Urls[0]);
    }

    [Fact]
    public async Task Fetch_without_locations_makes_no_discovery_request()
    {
        var handler = new RecordingHandler(_ => """{ "total": 1, "jobPostings": [ """ + Posting(1) + " ] }");
        var source = new WorkdaySource(new ResilientHttp(new HttpClient(handler))) { PageDelay = TimeSpan.Zero };

        var jobs = await source.FetchAsync(
            new SourceConfig { Name = "Acme", Provider = "workday", Slug = "acme/wd5/Careers" }, Array.Empty<string>(), default);

        Assert.Single(jobs);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Fetch_with_a_bad_slug_explains_what_is_needed()
    {
        var source = new WorkdaySource(new ResilientHttp(new HttpClient(new RecordingHandler(_ => "{}"))));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.FetchAsync(new SourceConfig { Name = "Acme", Slug = "acme" }, Array.Empty<string>(), default));

        Assert.Contains("myworkdayjobs.com", ex.Message);
    }

    [Fact]
    public async Task Post_json_retries_transient_failures()
    {
        var calls = 0;
        var handler = new RecordingHandler(_ =>
        {
            calls++;
            return calls == 1 ? null : """{ "ok": true }""";
        });
        var http = new ResilientHttp(new HttpClient(handler)) { Delay = (_, _) => Task.CompletedTask };

        var text = await http.PostJsonAsync("https://x.test/api", """{ "a": 1 }""");

        Assert.Equal(2, calls);
        Assert.Contains("ok", text);
        Assert.Equal(HttpMethod.Post, handler.Methods[0]);
        Assert.Equal("""{ "a": 1 }""", handler.Bodies[0]);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<string, string?> _respond;
        public List<string> Urls { get; } = new();
        public List<HttpMethod> Methods { get; } = new();
        public List<string> Bodies { get; } = new();

        /// <param name="respond">Returns the JSON body; returning null produces an HTTP 503.</param>
        public RecordingHandler(Func<string, string?> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Urls.Add(request.RequestUri!.ToString());
            Methods.Add(request.Method);
            Bodies.Add(body);

            var reply = _respond(body);
            return reply is null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) };
        }
    }
}
