using JobAlertAgent.Models;

namespace JobAlertAgent.Tests;

public class SourceUrlParserTests
{
    [Theory]
    [InlineData("https://boards.greenhouse.io/anthropic", "greenhouse", "anthropic")]
    [InlineData("https://job-boards.greenhouse.io/anthropic/jobs/5456619008", "greenhouse", "anthropic")]
    [InlineData("https://job-boards.eu.greenhouse.io/groww/jobs/4970739101", "greenhouse", "groww")]
    [InlineData("https://boards.greenhouse.io/embed/job_board?for=databricks", "greenhouse", "databricks")]
    [InlineData("https://boards-api.greenhouse.io/v1/boards/razorpaysoftwareprivatelimited/jobs", "greenhouse", "razorpaysoftwareprivatelimited")]
    [InlineData("https://jobs.lever.co/meesho", "lever", "meesho")]
    [InlineData("https://jobs.lever.co/meesho/7d9af9b5-c1c7-48ec-bbb5-9b25e49f6596?lever-source=x", "lever", "meesho")]
    [InlineData("jobs.lever.co/paytm", "lever", "paytm")]
    [InlineData("  HTTPS://JOBS.ASHBYHQ.COM/openai  ", "ashby", "openai")]
    [InlineData("https://jobs.ashbyhq.com/sarvam/c0406fe0-c8c0-44aa-aa3e-b879f2bf1049", "ashby", "sarvam")]
    [InlineData("lever:cred", "lever", "cred")]
    [InlineData("Greenhouse:groww", "greenhouse", "groww")]
    [InlineData("ashby:openai", "ashby", "openai")]
    public void Recognises_board_links_and_shorthand(string input, string provider, string slug)
    {
        Assert.True(SourceUrlParser.TryParse(input, out var p, out var s, out var error), error);
        Assert.Equal(provider, p);
        Assert.Equal(slug, s);
    }

    [Theory]
    [InlineData("https://www.amazon.jobs/en/search?base_query=azure", "amazon")]
    [InlineData("https://amazon.jobs", "amazon")]
    [InlineData("https://apply.careers.microsoft.com/careers?domain=microsoft.com", "microsoft")]
    [InlineData("https://careers.microsoft.com/v2/global/en/home.html", "microsoft")]
    public void Recognises_amazon_and_microsoft_without_a_slug(string input, string provider)
    {
        Assert.True(SourceUrlParser.TryParse(input, out var p, out var s, out var error), error);
        Assert.Equal(provider, p);
        Assert.Null(s);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/careers")]
    [InlineData("https://www.linkedin.com/company/acme/jobs")]
    [InlineData("https://jobs.lever.co/")]
    [InlineData("https://boards.greenhouse.io/")]
    [InlineData("https://boards.greenhouse.io/embed/job_board")]
    [InlineData("https://jobs.eu.lever.co/acme")]
    [InlineData("not a link at all")]
    public void Rejects_unknown_or_incomplete_links_with_help(string input)
    {
        Assert.False(SourceUrlParser.TryParse(input, out _, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("lever", "meesho", "Meesho")]
    [InlineData("ashby", "sarvam-ai", "Sarvam Ai")]
    [InlineData("greenhouse", "pine_labs", "Pine Labs")]
    [InlineData("amazon", null, "Amazon")]
    [InlineData("microsoft", null, "Microsoft")]
    public void Derives_readable_names(string provider, string? slug, string expected) =>
        Assert.Equal(expected, SourceUrlParser.DeriveName(provider, slug));
}
