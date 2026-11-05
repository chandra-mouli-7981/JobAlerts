using System.Text.RegularExpressions;
using System.Web;

namespace JobAlertAgent.Models;

/// <summary>
/// Works out provider + board name from a pasted career-site link such as
/// <c>https://jobs.lever.co/meesho</c> or from the shorthand <c>lever:meesho</c>.
/// </summary>
public static partial class SourceUrlParser
{
    public const string SupportedHelp =
        "Paste a Greenhouse, Lever, Ashby or Workable job-board link (boards.greenhouse.io/<name>, jobs.lever.co/<name>, "
        + "jobs.ashbyhq.com/<name>, apply.workable.com/<name>), a Workday careers page (<name>.wd5.myworkdayjobs.com/<site>), amazon.jobs, apply.careers.microsoft.com, or use the shorthand "
        + "\"greenhouse:<name>\" / \"lever:<name>\" / \"ashby:<name>\" / \"workable:<name>\". "
        + "Tip: on a company's own careers page, the Apply button often links to one of these sites.";

    private static readonly HashSet<string> GreenhouseHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "boards.greenhouse.io", "job-boards.greenhouse.io", "boards.eu.greenhouse.io", "job-boards.eu.greenhouse.io",
    };

    [GeneratedRegex(@"^(greenhouse|lever|ashby|workable):([A-Za-z0-9_.\-]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Shorthand();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^([a-z0-9\-]+)\.(wd\d+)\.myworkdayjobs\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex WorkdayHost();

    [GeneratedRegex(@"^[a-z]{2}-[A-Za-z]{2}$")]
    private static partial Regex Locale();

    public static bool TryParse(string input, out string provider, out string? slug, out string error)
    {
        provider = "";
        slug = null;
        error = "";
        input = (input ?? "").Trim();

        if (input.Length == 0)
        {
            error = "Empty link.";
            return false;
        }

        var shorthand = Shorthand().Match(input);
        if (shorthand.Success)
        {
            provider = shorthand.Groups[1].Value.ToLowerInvariant();
            slug = shorthand.Groups[2].Value;
            return true;
        }

        // Allow links pasted without the scheme, e.g. "jobs.lever.co/meesho".
        var candidate = input.Contains("://", StringComparison.Ordinal) ? input : "https://" + input;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = $"'{input}' is not a valid link. {SupportedHelp}";
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (GreenhouseHosts.Contains(host))
        {
            provider = ProviderNames.Greenhouse;
            // Embedded boards look like /embed/job_board?for=<name>
            slug = segments.Length > 0 && segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase)
                ? HttpUtility.ParseQueryString(uri.Query)["for"]
                : segments.FirstOrDefault();
            return Finish(input, ref slug, ref error);
        }

        if (host == "boards-api.greenhouse.io")
        {
            provider = ProviderNames.Greenhouse;
            slug = segments.Length >= 3 && segments[0] == "v1" && segments[1] == "boards" ? segments[2] : null;
            return Finish(input, ref slug, ref error);
        }

        if (host == "jobs.lever.co")
        {
            provider = ProviderNames.Lever;
            slug = segments.FirstOrDefault();
            return Finish(input, ref slug, ref error);
        }

        if (host == "jobs.eu.lever.co")
        {
            error = $"'{input}' is a Lever EU site, which is not supported yet.";
            return false;
        }

        if (host == "jobs.ashbyhq.com")
        {
            provider = ProviderNames.Ashby;
            slug = segments.FirstOrDefault();
            return Finish(input, ref slug, ref error);
        }

        if (host == "apply.workable.com")
        {
            provider = ProviderNames.Workable;
            slug = segments.FirstOrDefault();
            if (slug is "j" or "api") // /j/<id> is a single job link, not the company page
            {
                error = $"'{input}' is a link to a single Workable job. Use the company page, e.g. https://apply.workable.com/<company>/";
                slug = null;
                return false;
            }

            return Finish(input, ref slug, ref error);
        }

        if (host.EndsWith(".workable.com", StringComparison.Ordinal) && host.Count(c => c == '.') == 2
            && !host.StartsWith("apply.", StringComparison.Ordinal))
        {
            provider = ProviderNames.Workable;
            slug = host[..host.IndexOf('.')];
            return Finish(input, ref slug, ref error);
        }

        var workday = WorkdayHost().Match(host);
        if (workday.Success)
        {
            provider = ProviderNames.Workday;
            // /<site> or /<locale>/<site>, e.g. /en-US/External
            var rest = segments.AsEnumerable();
            if (segments.Length > 0 && Locale().IsMatch(segments[0])) rest = segments.Skip(1);
            var site = rest.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(site) || !SlugPattern().IsMatch(site))
            {
                error = $"'{input}' is a Workday address without the career-site name. "
                        + "Use the full careers page link, e.g. https://<company>.wd5.myworkdayjobs.com/<SiteName>";
                return false;
            }

            slug = $"{workday.Groups[1].Value}/{workday.Groups[2].Value}/{site}";
            return true;
        }

        if (host == "amazon.jobs" || host.EndsWith(".amazon.jobs", StringComparison.Ordinal))
        {
            provider = ProviderNames.Amazon;
            return true;
        }

        if (host == "careers.microsoft.com" || host == "apply.careers.microsoft.com")
        {
            provider = ProviderNames.Microsoft;
            return true;
        }

        error = $"Don't recognise the career site in '{input}'. {SupportedHelp}";
        return false;
    }

    /// <summary>Derives a readable company name, e.g. "sarvam-ai" -> "Sarvam Ai".</summary>
    public static string DeriveName(string provider, string? slug)
    {
        switch (provider)
        {
            case ProviderNames.Amazon: return "Amazon";
            case ProviderNames.Microsoft: return "Microsoft";
        }

        if (string.IsNullOrWhiteSpace(slug)) return "";
        if (provider == ProviderNames.Workday) slug = slug.Split('/')[0]; // "tenant/wd5/Site" -> tenant
        var words = slug.Split(new[] { '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }

    private static bool Finish(string input, ref string? slug, ref string error)
    {
        if (!string.IsNullOrWhiteSpace(slug) && SlugPattern().IsMatch(slug))
            return true;

        error = $"Could not find the company name in '{input}'. {SupportedHelp}";
        slug = null;
        return false;
    }
}
