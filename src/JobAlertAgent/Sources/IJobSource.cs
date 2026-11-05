using JobAlertAgent.Models;

namespace JobAlertAgent.Sources;

/// <summary>Fetches the current openings of one company from one kind of career system.</summary>
public interface IJobSource
{
    /// <summary>Matches <see cref="SourceConfig.Provider"/> (lower-case).</summary>
    string Provider { get; }

    /// <param name="source">The configured company.</param>
    /// <param name="searchTerms">
    /// The configured keyword terms. List-based providers (Greenhouse, Ashby, Lever) ignore them and
    /// return every opening; search-based providers (Amazon, Microsoft) issue one query per term.
    /// </param>
    Task<IReadOnlyList<Job>> FetchAsync(
        SourceConfig source, IReadOnlyList<string> searchTerms, CancellationToken ct);
}
