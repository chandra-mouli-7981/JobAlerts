using JobAlertAgent.Models;

namespace JobAlertAgent.Tests;

public class AppConfigTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JobAlertAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    [Fact]
    public void Shipped_config_is_valid_and_has_the_agreed_keywords_and_companies()
    {
        var config = AppConfig.Load(RepoFile("config", "jobalert.config.json"));

        Assert.Equal(
            new[]
            {
                "Applied AI Engineer", "Applied Engineer", ".NET", "React", "Angular", "Azure",
                "Python", "Gen AI", "GenAI", "Generative AI", "SQL", "AWS", "SRE", "DevOps", "Site Reliability",
            },
            config.Keywords.Select(k => k.Term));
        // Terms that appear in boilerplate of nearly every posting must stay title-only.
        Assert.All(new[] { "Azure", "Python", "Gen AI", "GenAI", "Generative AI", "SQL", "AWS", "SRE", "DevOps", "Site Reliability" },
            term => Assert.Equal(MatchScope.Title, config.Keywords.Single(k => k.Term == term).Scope));

        var names = config.Sources.Select(s => s.Name).ToList();
        foreach (var expected in new[]
        {
            "Anthropic", "OpenAI", "Databricks", "Razorpay", "CRED", "Groww", "Meesho", "Paytm", "Zeta",
            "Sarvam AI", "Druva", "Amazon", "Microsoft",
            "Okta", "Zscaler", "Stripe", "MongoDB", "UiPath", "Scale AI", "Cognition", "Atlan", "Mindtickle",
            "Exponent Energy", "Nvidia", "Salesforce", "Mastercard", "PayPal", "Twilio", "Fivetran",
        })
            Assert.Contains(expected, names);

        Assert.True(names.Count >= 90, $"expected 90+ sources, found {names.Count}");
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(config.Sources, s => Assert.Null(s.ConfigError));
        Assert.All(config.Sources, s => Assert.True(s.Enabled));
    }

    [Fact]
    public void Shipped_global_companies_are_limited_to_india_and_ai_labs_are_not()
    {
        var byName = AppConfig.Load(RepoFile("config", "jobalert.config.json")).Sources.ToDictionary(s => s.Name);

        Assert.Equal(new[] { "India" }, byName["Okta"].Locations);
        Assert.Equal(new[] { "India" }, byName["Stripe"].Locations);
        Assert.Equal(("workday", "nvidia/wd5/NVIDIAExternalCareerSite"), (byName["Nvidia"].Provider, byName["Nvidia"].Slug));
        Assert.Equal(new[] { "India" }, byName["Nvidia"].Locations);
        Assert.Empty(byName["Anthropic"].Locations);
        Assert.Empty(byName["Scale AI"].Locations);
        Assert.Equal(("workable", "exponent-energy"), (byName["Exponent Energy"].Provider, byName["Exponent Energy"].Slug));
    }

    [Fact]
    public void Shipped_config_resolves_to_the_providers_and_boards_that_were_verified()
    {
        var byName = AppConfig.Load(RepoFile("config", "jobalert.config.json")).Sources.ToDictionary(s => s.Name);

        Assert.Equal(("greenhouse", "razorpaysoftwareprivatelimited"), (byName["Razorpay"].Provider, byName["Razorpay"].Slug));
        Assert.Equal(("ashby", "openai"), (byName["OpenAI"].Provider, byName["OpenAI"].Slug));
        Assert.Equal(("lever", "meesho"), (byName["Meesho"].Provider, byName["Meesho"].Slug));
        Assert.Equal(new[] { "IND" }, byName["Amazon"].CountryCodes);
        Assert.Equal("India", byName["Microsoft"].Location);
    }

    [Fact]
    public void Keywords_and_sources_can_be_plain_strings()
    {
        var config = AppConfig.Parse("""
        {
          "keywords": [ "React", { "term": "Azure", "scope": "title" } ],
          "sources": [ "https://jobs.lever.co/meesho", "ashby:openai", { "url": "https://www.amazon.jobs", "countryCodes": ["IND"] } ]
        }
        """);

        Assert.Equal(new[] { "React", "Azure" }, config.Keywords.Select(k => k.Term));
        Assert.Equal(MatchScope.TitleAndDescription, config.Keywords[0].Scope);
        Assert.Equal(MatchScope.Title, config.Keywords[1].Scope);

        Assert.Equal(new[] { "Meesho", "Openai", "Amazon" }, config.Sources.Select(s => s.Name));
        Assert.Equal(new[] { "lever", "ashby", "amazon" }, config.Sources.Select(s => s.Provider));
        Assert.Equal("meesho", config.Sources[0].Slug);
        Assert.All(config.Sources, s => Assert.Null(s.ConfigError));
        Assert.Equal(new[] { "IND" }, config.Sources[2].CountryCodes);
    }

    [Fact]
    public void Explicit_name_wins_and_comments_and_trailing_commas_are_allowed()
    {
        var config = AppConfig.Parse("""
        {
          // my keywords
          "Keywords": [ "React", ],
          "sources": [
            { "name": "Sarvam AI", "url": "https://jobs.ashbyhq.com/sarvam" }, // India AI
          ],
        }
        """);

        Assert.Equal("Sarvam AI", config.Sources.Single().Name);
        Assert.Equal("sarvam", config.Sources.Single().Slug);
    }

    [Fact]
    public void Old_style_provider_and_slug_entries_still_work()
    {
        var config = AppConfig.Parse("""
        { "keywords": ["x"], "sources": [ { "name": "A", "provider": "Greenhouse", "slug": "a" } ] }
        """);

        var s = config.Sources.Single();
        Assert.Null(s.ConfigError);
        Assert.Equal("greenhouse", s.Provider);
        Assert.Equal("a", s.Slug);
    }

    [Theory]
    [InlineData("""{ "url": "https://example.com/careers" }""", "Don't recognise")]
    [InlineData("""{ "name": "X", "provider": "taleo", "slug": "x" }""", "unknown provider")]
    [InlineData("""{ "name": "X", "provider": "lever" }""", "needs a \"slug\"")]
    [InlineData("""{ "name": "X" }""", "needs a \"url\"")]
    [InlineData("""{ "name": "X", "provider": "greenhouse", "url": "https://jobs.lever.co/x" }""", "does not match")]
    [InlineData("\"https://jobs.lever.co/\"", "Could not find the company name")]
    public void Problem_with_one_source_is_reported_not_thrown(string sourceJson, string expectedFragment)
    {
        var config = AppConfig.Parse($$"""{ "keywords": ["x"], "sources": [ {{sourceJson}}, "lever:good" ] }""");

        var bad = config.Sources[0];
        Assert.NotNull(bad.ConfigError);
        Assert.Contains(expectedFragment, bad.ConfigError);
        Assert.False(string.IsNullOrWhiteSpace(bad.Name)); // so it can be named in the email
        Assert.Null(config.Sources[1].ConfigError);        // the neighbour is unaffected
    }

    [Fact]
    public void Duplicate_company_names_flag_the_later_entry_only()
    {
        var config = AppConfig.Parse("""{ "keywords": ["x"], "sources": [ "lever:acme", "https://jobs.lever.co/ACME/some-job" ] }""");

        Assert.Null(config.Sources[0].ConfigError);
        Assert.Contains("Duplicate", config.Sources[1].ConfigError);
    }

    [Theory]
    [InlineData("""{ "keywords": [], "sources": [] }""", "at least one keyword")]
    [InlineData("""{ "keywords": [""], "sources": [] }""", "keyword is empty")]
    [InlineData("""{ "keywords": [ { "term": "x", "scope": "everything" } ] }""", "invalid scope")]
    [InlineData("""{ "keywords": "React" }""", "must be a list")]
    [InlineData("""{ "keywords": [ 5 ] }""", "quoted string or an object")]
    [InlineData("""{ "keywords": ["x"] "sources": [] }""", "not valid JSON")]
    [InlineData("[1,2]", "must contain a JSON object")]
    public void Fatal_problems_throw_with_a_helpful_message(string json, string expectedFragment)
    {
        var ex = Assert.Throws<InvalidDataException>(() => AppConfig.Parse(json));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void Missing_file_gives_a_clear_error() =>
        Assert.Throws<FileNotFoundException>(() => AppConfig.Load("does-not-exist.json"));
}
