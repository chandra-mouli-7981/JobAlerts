using JobAlertAgent.Matching;
using JobAlertAgent.Models;
using JobAlertAgent.Notifications;
using JobAlertAgent.Sources;
using JobAlertAgent.State;

namespace JobAlertAgent.Tests;

public sealed class WorkableAndLocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jobalert-loc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Job J(string id, string title, string? location) => new()
    {
        SourceName = "Acme", Id = id, Title = title, Location = location!, Url = $"https://jobs/{id}",
    };

    // ---- Workable parsing ----------------------------------------------------------------------

    [Fact]
    public void Workable_parses_jobs_location_remote_flag_and_description()
    {
        const string json = """
        { "name": "Exponent Energy", "jobs": [
          { "shortcode": "ABC123", "title": " Senior .NET Engineer ", "url": "https://apply.workable.com/j/ABC123",
            "city": "Bengaluru", "state": "Karnataka", "country": "India", "telecommuting": false,
            "published_on": "2026-10-20", "description": "<p>Build with <b>Angular</b> &amp; Azure</p>" },
          { "shortcode": "REM1", "title": "Python Dev", "shortlink": "https://apply.workable.com/j/REM1",
            "telecommuting": true, "description": "x" },
          { "shortcode": "NOURL", "title": "Broken" }
        ] }
        """;

        var jobs = WorkableSource.Parse(new SourceConfig { Name = "Exponent Energy" }, json);

        Assert.Equal(new[] { "ABC123", "REM1" }, jobs.Select(j => j.Id));
        Assert.Equal("Senior .NET Engineer", jobs[0].Title);
        Assert.Equal("Bengaluru, Karnataka, India", jobs[0].Location);
        Assert.Equal("Build with Angular & Azure", jobs[0].Description);
        Assert.NotNull(jobs[0].PostedAt);
        Assert.Equal("Remote", jobs[1].Location);
    }

    [Fact]
    public void Workable_with_no_jobs_array_returns_nothing() =>
        Assert.Empty(WorkableSource.Parse(new SourceConfig { Name = "X" }, """{ "name": "X" }"""));

    // ---- Workable links ------------------------------------------------------------------------

    [Theory]
    [InlineData("https://apply.workable.com/exponent-energy", "exponent-energy")]
    [InlineData("https://apply.workable.com/exponent-energy/", "exponent-energy")]
    [InlineData("https://apply.workable.com/exponent-energy/?lng=en", "exponent-energy")]
    [InlineData("https://exponent-energy.workable.com/", "exponent-energy")]
    [InlineData("workable:exponent-energy", "exponent-energy")]
    public void Workable_links_and_shorthand_are_recognised(string input, string slug)
    {
        Assert.True(SourceUrlParser.TryParse(input, out var provider, out var s, out var error), error);
        Assert.Equal("workable", provider);
        Assert.Equal(slug, s);
    }

    [Theory]
    [InlineData("https://apply.workable.com/j/ABC123")]
    [InlineData("https://apply.workable.com/")]
    public void Workable_single_job_links_and_empty_links_are_rejected(string input)
    {
        Assert.False(SourceUrlParser.TryParse(input, out _, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Workable_source_in_config_resolves()
    {
        var config = AppConfig.Parse("""{ "keywords": ["x"], "sources": [ "https://apply.workable.com/exponent-energy" ] }""");
        var s = config.Sources.Single();
        Assert.Null(s.ConfigError);
        Assert.Equal(("workable", "exponent-energy"), (s.Provider, s.Slug));
    }

    // ---- Location filter -----------------------------------------------------------------------

    [Theory]
    [InlineData("Bengaluru, India", true)]
    [InlineData("Bangalore", true)]
    [InlineData("Hyderabad, Telangana", true)]
    [InlineData("Remote - India", true)]
    [InlineData("Pune; London", true)]
    [InlineData("Indiana, USA", false)]
    [InlineData("Indianapolis, IN", false)]
    [InlineData("London, UK", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void India_filter_matches_india_and_its_cities_but_not_lookalikes(string? location, bool expected)
    {
        var source = new SourceConfig { Name = "Acme", Locations = { "India" } };

        var kept = LocationFilter.Apply(source, new[] { J("1", "Dev", location) });

        Assert.Equal(expected, kept.Count == 1);
    }

    [Fact]
    public void No_locations_means_no_filtering()
    {
        var source = new SourceConfig { Name = "Acme" };
        var jobs = new[] { J("1", "a", "London"), J("2", "b", null) };

        Assert.Equal(2, LocationFilter.Apply(source, jobs).Count);
    }

    [Fact]
    public void Custom_places_work_and_can_be_combined_with_india()
    {
        var source = new SourceConfig { Name = "Acme", Locations = { "London", "India" } };
        var jobs = new[] { J("1", "a", "London, UK"), J("2", "b", "Chennai"), J("3", "c", "Berlin") };

        Assert.Equal(new[] { "1", "2" }, LocationFilter.Apply(source, jobs).Select(j => j.Id));
    }

    [Fact]
    public void Locations_are_read_from_config()
    {
        var config = AppConfig.Parse("""
        { "keywords": ["x"], "sources": [ { "name": "Okta", "url": "https://job-boards.greenhouse.io/okta", "locations": ["India"] } ] }
        """);

        Assert.Equal(new[] { "India" }, config.Sources.Single().Locations);
    }

    // ---- Runner integration --------------------------------------------------------------------

    private sealed class FakeSource : IJobSource
    {
        public string Provider => "greenhouse";
        public Task<IReadOnlyList<Job>> FetchAsync(SourceConfig s, IReadOnlyList<string> t, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Job>>(new[]
            {
                J("1", "Senior .NET Engineer", "Bengaluru, India"),
                J("2", "Senior .NET Engineer", "Austin, TX"),
            });
    }

    private sealed class Capture : INotifier
    {
        public List<string> Messages { get; } = new();
        public Task SendAsync(string html, CancellationToken ct) { Messages.Add(html); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Runner_only_reports_jobs_in_the_selected_locations()
    {
        var config = new AppConfig
        {
            Keywords = { new KeywordConfig { Term = ".NET" } },
            Sources = { new SourceConfig { Name = "Acme", Provider = "greenhouse", Slug = "acme", Locations = { "India" } } },
        };
        var notifier = new Capture();
        var store = SeenJobsStore.Load(Path.Combine(_dir, "seen.json"));
        var runner = new JobAlertRunner(config, new SourceRegistry(new IJobSource[] { new FakeSource() }), store, notifier,
            TextWriter.Null, () => new DateTimeOffset(2026, 10, 25, 3, 0, 0, TimeSpan.Zero));

        var result = await runner.RunAsync(new RunOptions { InitialDigest = true }, default);

        Assert.Equal(1, result.Matched);
        var message = Assert.Single(notifier.Messages);
        Assert.Contains("Bengaluru", message);
        Assert.DoesNotContain("Austin", message);
    }
}
