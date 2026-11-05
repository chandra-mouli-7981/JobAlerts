using System.Text.RegularExpressions;
using JobAlertAgent.Models;

namespace JobAlertAgent.Matching;

/// <summary>
/// Case-insensitive keyword matching with word-boundary semantics, so "React" does not match
/// "Reaction" but ".NET" still matches "ASP.NET" and "Applied AI Engineer" tolerates extra spaces.
/// </summary>
public sealed class KeywordMatcher
{
    private sealed record Rule(string Term, bool TitleOnly, Regex Pattern);

    private readonly List<Rule> _rules;

    public KeywordMatcher(IEnumerable<KeywordConfig> keywords)
    {
        _rules = keywords
            .Select(k => new Rule(
                k.Term.Trim(),
                string.Equals(k.Scope, MatchScope.Title, StringComparison.OrdinalIgnoreCase),
                BuildPattern(k.Term.Trim())))
            .ToList();
    }

    public IReadOnlyList<string> Terms => _rules.Select(r => r.Term).ToList();

    /// <summary>Returns the matched job, or null when no keyword applies.</summary>
    public MatchedJob? Match(Job job)
    {
        var hits = new List<string>();

        foreach (var rule in _rules)
        {
            var inTitle = rule.Pattern.IsMatch(job.Title);

            var serverMatched = job.ServerMatchedKeywords?.Contains(rule.Term, StringComparer.OrdinalIgnoreCase) == true;
            var inDescription = !rule.TitleOnly
                                && (serverMatched
                                    || (!string.IsNullOrEmpty(job.Description) && rule.Pattern.IsMatch(job.Description)));

            if (inTitle || inDescription)
                hits.Add(rule.Term);
        }

        return hits.Count == 0 ? null : new MatchedJob(job, hits);
    }

    internal static Regex BuildPattern(string term)
    {
        // Escape, then let any run of whitespace in the term match any run of whitespace in the text.
        var body = Regex.Escape(term);
        body = Regex.Replace(body, @"(\\ )+", @"\s+");

        // A boundary is only meaningful next to a word character; ".NET" has none on its left,
        // which is what lets it match inside "ASP.NET".
        var prefix = IsWordChar(term[0]) ? @"(?<![\p{L}\p{N}_])" : "";
        var suffix = IsWordChar(term[^1]) ? @"(?![\p{L}\p{N}_])" : "";

        return new Regex(prefix + body + suffix,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
