namespace JobAlertAgent.Models;

/// <summary>A single job opening normalised across all career-site providers.</summary>
public sealed class Job
{
    public required string SourceName { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Location { get; init; } = "";
    public required string Url { get; init; }

    /// <summary>Plain-text description when the provider returns one; otherwise null.</summary>
    public string? Description { get; init; }

    public DateTimeOffset? PostedAt { get; init; }

    /// <summary>
    /// Keywords that the provider's own search already matched (search-based providers whose
    /// results carry no description text, e.g. Microsoft). Null for list-based providers.
    /// </summary>
    public IReadOnlyList<string>? ServerMatchedKeywords { get; init; }

    /// <summary>Stable identity used by the seen-jobs store.</summary>
    public string Key => $"{SourceName}:{Id}";
}

/// <summary>A job together with the keywords that caused it to match.</summary>
public sealed record MatchedJob(Job Job, IReadOnlyList<string> MatchedKeywords);
