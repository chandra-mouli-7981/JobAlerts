using JobAlertAgent.Matching;
using JobAlertAgent.Models;

namespace JobAlertAgent.Tests;

public class KeywordMatcherTests
{
    private static KeywordMatcher Matcher(params (string Term, string Scope)[] keywords) =>
        new(keywords.Select(k => new KeywordConfig { Term = k.Term, Scope = k.Scope }));

    private static KeywordMatcher AllKeywords() => Matcher(
        ("Applied AI Engineer", MatchScope.TitleAndDescription),
        (".NET", MatchScope.TitleAndDescription),
        ("React", MatchScope.TitleAndDescription),
        ("Angular", MatchScope.TitleAndDescription),
        ("Azure", MatchScope.TitleAndDescription));

    private static Job J(string title, string? description = null, IReadOnlyList<string>? server = null) => new()
    {
        SourceName = "Co", Id = Guid.NewGuid().ToString(), Title = title, Url = "https://x",
        Description = description, ServerMatchedKeywords = server,
    };

    [Theory]
    [InlineData("Senior .NET Developer")]
    [InlineData("Backend Engineer (ASP.NET Core)")]
    [InlineData("Full Stack (.NET/React)")]
    [InlineData("Applied   AI   Engineer, Startups")]
    [InlineData("APPLIED AI ENGINEER")]
    [InlineData("React Native Developer")]
    [InlineData("Angular, TypeScript")]
    [InlineData("Azure Cloud Architect")]
    public void Matches_in_title(string title) => Assert.NotNull(AllKeywords().Match(J(title)));

    [Theory]
    [InlineData("Reaction Engineer")]
    [InlineData("Reactive Systems Developer")]
    [InlineData("Triangular Arbitrage Quant")]
    [InlineData("Azurelane Designer")]
    [InlineData("Dotnet-free Java Developer")]
    [InlineData("Applied AI Engineering Manager")]
    public void Does_not_match_partial_words(string title) => Assert.Null(AllKeywords().Match(J(title)));

    [Fact]
    public void Matches_description_and_reports_all_matched_keywords_in_config_order()
    {
        var m = AllKeywords().Match(J("Software Engineer", "We use Azure, Angular and .NET 8."));

        Assert.NotNull(m);
        Assert.Equal(new[] { ".NET", "Angular", "Azure" }, m!.MatchedKeywords);
    }

    [Fact]
    public void Title_scope_ignores_description()
    {
        var matcher = Matcher(("Azure", MatchScope.Title), ("React", MatchScope.TitleAndDescription));

        Assert.Null(matcher.Match(J("Engineer", "Experience with Azure required")));
        Assert.NotNull(matcher.Match(J("Azure Engineer")));
        Assert.NotNull(matcher.Match(J("Engineer", "Experience with React")));
    }

    [Fact]
    public void Server_matched_keywords_count_for_description_scope_but_not_title_scope()
    {
        var matcher = Matcher(("Azure", MatchScope.TitleAndDescription), (".NET", MatchScope.Title));
        var job = J("Senior Consultant", server: new[] { "Azure", ".NET" });

        var m = matcher.Match(job);

        Assert.NotNull(m);
        Assert.Equal(new[] { "Azure" }, m!.MatchedKeywords); // .NET is title-only and absent from the title
    }

    [Fact]
    public void Returns_null_when_nothing_matches() =>
        Assert.Null(AllKeywords().Match(J("Accountant", "Excel and SAP")));

    [Fact]
    public void Regex_special_characters_in_terms_are_escaped()
    {
        var matcher = Matcher(("C++", MatchScope.Title), ("Node.js", MatchScope.Title));

        Assert.NotNull(matcher.Match(J("Senior C++ Developer")));
        Assert.NotNull(matcher.Match(J("Node.js Engineer")));
        Assert.Null(matcher.Match(J("Nodexjs Engineer")));
    }
}
