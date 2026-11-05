using System.Net;
using JobAlertAgent.Infrastructure;
using JobAlertAgent.Models;
using JobAlertAgent.Sources;
using JobAlertAgent.Tests.Helpers;

namespace JobAlertAgent.Tests.Sources;

public class SourceFetchTests
{
    private static ResilientHttp Fast(HttpClient client) => new(client)
    {
        BaseDelay = TimeSpan.Zero,
        Delay = (_, _) => Task.CompletedTask,
    };

    [Fact]
    public async Task ResilientHttp_retries_transient_errors_then_succeeds()
    {
        var calls = 0;
        var (client, _) = FakeHandler.Create(_ =>
            ++calls < 3 ? FakeHandler.Status(HttpStatusCode.TooManyRequests) : FakeHandler.Json("{}"));

        var body = await Fast(client).GetStringAsync("https://example.test/x");

        Assert.Equal("{}", body);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ResilientHttp_does_not_retry_permanent_errors()
    {
        var (client, handler) = FakeHandler.Create(_ => FakeHandler.Status(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Fast(client).GetStringAsync("https://example.test/x"));

        Assert.Contains("404", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResilientHttp_gives_up_after_max_attempts()
    {
        var (client, handler) = FakeHandler.Create(_ => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HttpRequestException>(() => Fast(client).GetStringAsync("https://example.test/x"));

        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Greenhouse_requests_the_board_url_with_content()
    {
        var (client, handler) = FakeHandler.Create(_ => FakeHandler.Json("""{ "jobs": [] }"""));

        var jobs = await new GreenhouseSource(Fast(client)).FetchAsync(
            new SourceConfig { Name = "Databricks", Provider = "greenhouse", Slug = "databricks" },
            Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(jobs);
        Assert.Equal("https://boards-api.greenhouse.io/v1/boards/databricks/jobs?content=true", handler.Requests.Single());
    }

    [Fact]
    public async Task Amazon_pages_until_a_short_page_and_dedupes_across_terms()
    {
        static string Page(int count, int startId) =>
            "{ \"jobs\": [" + string.Join(",", Enumerable.Range(startId, count).Select(i =>
                $$"""{ "id_icims": "{{i}}", "title": "T{{i}}", "country_code": "IND", "job_path": "/en/jobs/{{i}}/t", "description": "d" }""")) + "] }";

        var (client, handler) = FakeHandler.Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("offset=0")) return FakeHandler.Json(Page(100, 1));    // full page -> keep going
            return FakeHandler.Json(Page(3, 101));                                   // short page -> stop
        });

        var jobs = await new AmazonSource(Fast(client)).FetchAsync(
            new SourceConfig { Name = "Amazon", Provider = "amazon", CountryCodes = { "IND" } },
            new[] { "Azure", ".NET" }, CancellationToken.None);

        // 2 requests per term; the second term returns identical ids which must be deduped.
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(103, jobs.Count);
    }

    [Fact]
    public async Task Microsoft_visits_home_first_paginates_and_merges_keywords_per_job()
    {
        string Position(int id) => $$"""{ "id": {{id}}, "name": "Job {{id}}", "locations": ["India"], "positionUrl": "/careers/job/{{id}}" }""";
        string Page(IEnumerable<int> ids, int count) =>
            "{ \"data\": { \"count\": " + count + ", \"positions\": [" + string.Join(",", ids.Select(Position)) + "] } }";

        var (client, handler) = FakeHandler.Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (!url.Contains("/api/pcsx/search")) return FakeHandler.Json("<html></html>");
            if (url.Contains("query=Azure") && url.Contains("start=0")) return FakeHandler.Json(Page(Enumerable.Range(1, 10), 12));
            if (url.Contains("query=Azure") && url.Contains("start=10")) return FakeHandler.Json(Page(new[] { 11, 12 }, 12));
            if (url.Contains("query=React")) return FakeHandler.Json(Page(new[] { 3, 99 }, 2));
            return FakeHandler.Json(Page(Array.Empty<int>(), 0));
        });

        var jobs = await new MicrosoftSource(Fast(client)) { PageDelay = TimeSpan.Zero }.FetchAsync(
            new SourceConfig { Name = "Microsoft", Provider = "microsoft", Location = "India" },
            new[] { "Azure", "React" }, CancellationToken.None);

        Assert.Equal("https://apply.careers.microsoft.com/", handler.Requests[0]);
        Assert.Equal(13, jobs.Count); // ids 1..12 plus 99; id 3 appears under both terms
        Assert.Equal(new[] { "Azure", "React" }, jobs.Single(j => j.Id == "3").ServerMatchedKeywords);
        Assert.Equal(new[] { "React" }, jobs.Single(j => j.Id == "99").ServerMatchedKeywords);
        Assert.Contains(handler.Requests, r => r.Contains("location=India") && r.Contains("num=10"));
    }
}
