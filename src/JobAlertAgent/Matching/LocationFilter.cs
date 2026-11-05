using System.Text.RegularExpressions;
using JobAlertAgent.Models;

namespace JobAlertAgent.Matching;

/// <summary>
/// Optional per-company location restriction (<see cref="SourceConfig.Locations"/>). A job is kept when its
/// location text contains any of the configured places as a whole word ("India" does not match "Indiana").
/// The place "India" also covers the main Indian cities, because many postings list only a city.
/// </summary>
public static class LocationFilter
{
    private static readonly string[] IndiaPlaces =
    {
        "India", "Bengaluru", "Bangalore", "Hyderabad", "Pune", "Mumbai", "Navi Mumbai", "Gurgaon", "Gurugram",
        "Chennai", "Noida", "Delhi", "New Delhi", "Kolkata", "Ahmedabad", "Kochi", "Cochin", "Jaipur",
        "Thiruvananthapuram", "Trivandrum", "Coimbatore", "Chandigarh", "Mohali", "Indore", "Nagpur",
        "Vadodara", "Lucknow", "Bhubaneswar", "Mysuru", "Mysore", "Vizag", "Visakhapatnam",
    };

    public static IReadOnlyList<Job> Apply(SourceConfig source, IReadOnlyList<Job> jobs)
    {
        var regex = BuildRegex(source.Locations);
        return regex is null ? jobs : jobs.Where(j => regex.IsMatch(j.Location ?? "")).ToList();
    }

    internal static Regex? BuildRegex(IEnumerable<string> locations)
    {
        var places = new List<string>();
        foreach (var raw in locations)
        {
            var place = raw.Trim();
            if (place.Length == 0) continue;
            if (place.Equals("India", StringComparison.OrdinalIgnoreCase)) places.AddRange(IndiaPlaces);
            else places.Add(place);
        }

        if (places.Count == 0) return null;

        var alternation = string.Join("|", places.Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape));
        return new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{alternation})(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    }
}
