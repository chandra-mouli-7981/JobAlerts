using JobAlertAgent.Models;
using JobAlertAgent.Sources;

namespace JobAlertAgent.Tests.Sources;

public class SourceParsingTests
{
    // Field names below mirror real responses captured from each provider on 2026-10-24.

    [Fact]
    public void Greenhouse_parses_jobs_and_decodes_escaped_html_content()
    {
        const string json = """
        { "jobs": [
          { "id": 5456619008, "title": "  Applied AI Engineer, Startups ",
            "absolute_url": "https://job-boards.greenhouse.io/anthropic/jobs/5456619008",
            "location": { "name": "London, UK" },
            "first_published": "2026-10-19T11:17:24-04:00",
            "content": "&lt;p&gt;Build with &lt;strong&gt;.NET&lt;/strong&gt; &amp;amp; Azure&lt;/p&gt;" },
          { "id": 2, "title": "No url" }
        ], "meta": { "total": 2 } }
        """;

        var jobs = GreenhouseSource.Parse(new SourceConfig { Name = "Anthropic" }, json);

        var job = Assert.Single(jobs);
        Assert.Equal("5456619008", job.Id);
        Assert.Equal("Applied AI Engineer, Startups", job.Title);
        Assert.Equal("London, UK", job.Location);
        Assert.Equal("Build with .NET & Azure", job.Description);
        Assert.Equal("Anthropic:5456619008", job.Key);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 15, 17, 24, TimeSpan.Zero), job.PostedAt!.Value.ToUniversalTime());
    }

    [Fact]
    public void Ashby_skips_unlisted_jobs_and_falls_back_to_html_description()
    {
        const string json = """
        { "apiVersion": "1", "jobs": [
          { "id": "a1", "title": "Research Engineer", "location": "San Francisco", "isListed": true,
            "isRemote": true, "jobUrl": "https://jobs.ashbyhq.com/openai/a1",
            "descriptionPlain": "Plain text with React",
            "publishedAt": "2025-04-05T00:03:20.653+00:00" },
          { "id": "a2", "title": "Hidden", "isListed": false, "jobUrl": "https://x/a2" },
          { "id": "a3", "title": "Html only", "location": "Remote", "isListed": true,
            "jobUrl": "https://x/a3", "descriptionPlain": "", "descriptionHtml": "<p>Angular <b>dev</b></p>" }
        ] }
        """;

        var jobs = AshbySource.Parse(new SourceConfig { Name = "OpenAI" }, json);

        Assert.Equal(new[] { "a1", "a3" }, jobs.Select(j => j.Id));
        Assert.Equal("San Francisco (Remote)", jobs[0].Location);
        Assert.Equal("Plain text with React", jobs[0].Description);
        Assert.Equal("Remote", jobs[1].Location);
        Assert.Equal("Angular dev", jobs[1].Description);
    }

    [Fact]
    public void Lever_builds_description_from_lists_when_description_is_empty()
    {
        const string json = """
        [ { "id": "l1", "text": "Finance Manager", "hostedUrl": "https://jobs.lever.co/cred/l1",
            "categories": { "location": "hyderabad", "allLocations": ["hyderabad", "bengaluru"] },
            "createdAt": 1790573982021, "description": "", "descriptionPlain": "",
            "lists": [ { "text": "Requirements", "content": "<ul><li>Azure</li><li>.NET</li></ul>" } ],
            "additionalPlain": "Perks" } ]
        """;

        var job = Assert.Single(LeverSource.Parse(new SourceConfig { Name = "CRED" }, json));

        Assert.Equal("hyderabad | bengaluru", job.Location);
        Assert.Contains("Requirements", job.Description);
        Assert.Contains("Azure", job.Description);
        Assert.Contains(".NET", job.Description);
        Assert.Contains("Perks", job.Description);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790573982021), job.PostedAt);
    }

    [Fact]
    public void Amazon_parses_fields_builds_absolute_url_and_applies_country_filter()
    {
        const string json = """
        { "hits": 2, "jobs": [
          { "id_icims": "10578562", "title": "Support Engineer", "country_code": "IND",
            "normalized_location": "Bengaluru, Karnataka, IND",
            "job_path": "/en/jobs/10578562/support-engineer", "posted_date": "October 14, 2026",
            "description": "Work on <b>Azure</b><br/>migrations", "basic_qualifications": "- .NET",
            "preferred_qualifications": "- React" },
          { "id_icims": "10587064", "title": "Partner SA", "country_code": "USA",
            "normalized_location": "New York, New York, USA",
            "job_path": "/en/jobs/10587064/partner-sa", "posted_date": "October 22, 2026", "description": "x" }
        ] }
        """;
        var source = new SourceConfig { Name = "Amazon", CountryCodes = { "IND" } };

        var jobs = AmazonSource.Parse(source, json, out var rawCount);

        Assert.Equal(2, rawCount);
        var job = Assert.Single(jobs);
        Assert.Equal("10578562", job.Id);
        Assert.Equal("https://www.amazon.jobs/en/jobs/10578562/support-engineer", job.Url);
        Assert.Equal("Bengaluru, Karnataka, IND", job.Location);
        Assert.Contains("Azure", job.Description);
        Assert.Contains(".NET", job.Description);
        Assert.Contains("React", job.Description);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), job.PostedAt);
    }

    [Fact]
    public void Amazon_url_encodes_term_and_country_codes()
    {
        var url = AmazonSource.BuildUrl(
            new SourceConfig { Name = "Amazon", CountryCodes = { "IND" } }, "Applied AI Engineer", 100);

        Assert.Contains("base_query=Applied%20AI%20Engineer", url);
        Assert.Contains("normalized_country_code%5B%5D=IND", url);
        Assert.Contains("offset=100", url);
    }

    [Fact]
    public void Microsoft_parses_positions_and_prefers_standardized_locations()
    {
        const string json = """
        { "status": 200, "data": { "count": 15, "positions": [
          { "id": 1970393556657253, "displayJobId": "200019877", "name": "Senior Consultant - Full Stack Apps A2",
            "locations": ["India, Telangana, Hyderabad"],
            "standardizedLocations": ["Hyderabad, TS, IN", "Bengaluru, KA, IN"],
            "postedTs": 1792824321, "positionUrl": "/careers/job/1970393556657253" },
          { "id": 2, "name": "Fallback loc", "locations": ["India, Pune"], "positionUrl": "/careers/job/2" }
        ] } }
        """;

        var jobs = MicrosoftSource.Parse(new SourceConfig { Name = "Microsoft" }, json, out var raw, out var total);

        Assert.Equal(2, raw);
        Assert.Equal(15, total);
        Assert.Equal("1970393556657253", jobs[0].Id);
        Assert.Equal("Hyderabad, TS, IN | Bengaluru, KA, IN", jobs[0].Location);
        Assert.Equal("https://apply.careers.microsoft.com/careers/job/1970393556657253", jobs[0].Url);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792824321), jobs[0].PostedAt);
        Assert.Equal("India, Pune", jobs[1].Location);
    }
}
