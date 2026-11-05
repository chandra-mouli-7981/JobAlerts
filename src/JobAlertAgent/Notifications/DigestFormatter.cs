using System.Globalization;
using System.Net;
using System.Text;
using JobAlertAgent.Models;

namespace JobAlertAgent.Notifications;

public sealed record SourceFailure(string SourceName, string Reason);

/// <summary>
/// Builds the HTML digest. Email has no practical size limit for a digest, so by default everything
/// goes into a single message; <c>maxLength</c> exists to split it if a channel ever needs that.
/// </summary>
public static class DigestFormatter
{
    public const int DefaultMaxLength = 500_000;

    public static IReadOnlyList<string> FormatDigest(
        IReadOnlyList<MatchedJob> newJobs,
        IReadOnlyList<SourceFailure> failures,
        int sourcesChecked,
        DateTimeOffset now,
        IReadOnlyList<string> sourceOrder,
        int maxLength = DefaultMaxLength)
    {
        var date = now.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        var lines = new List<string>();

        if (newJobs.Count == 0)
        {
            lines.Add($"<b>Job alert {date}</b>");
            lines.Add($"No new openings today (checked {sourcesChecked} source{(sourcesChecked == 1 ? "" : "s")}).");
        }
        else
        {
            lines.Add($"<b>Job alert {date}: {newJobs.Count} new opening{(newJobs.Count == 1 ? "" : "s")}</b>");
            lines.Add("");

            var order = sourceOrder.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
            foreach (var m in newJobs
                         .OrderBy(m => order.TryGetValue(m.Job.SourceName, out var i) ? i : int.MaxValue)
                         .ThenByDescending(m => m.Job.PostedAt ?? DateTimeOffset.MinValue)
                         .ThenBy(m => m.Job.Title, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(FormatJob(m));
            }
        }

        if (failures.Count > 0)
        {
            lines.Add("");
            lines.Add($"<b>Could not check {failures.Count} source{(failures.Count == 1 ? "" : "s")}:</b>");
            foreach (var f in failures)
                lines.Add($"- {Esc(f.SourceName)}: {Esc(Truncate(f.Reason, 450))}");
        }

        return Chunk(lines, maxLength);
    }

    public static IReadOnlyList<string> FormatSeedMessage(
        int indexedCount, IReadOnlyList<SourceFailure> failures, int sourcesChecked, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            "<b>Job Alert Agent is set up</b>",
            $"Indexed {indexedCount} current matching opening{(indexedCount == 1 ? "" : "s")} across {sourcesChecked} source{(sourcesChecked == 1 ? "" : "s")}. "
            + "From the next run you will only be notified about new openings.",
        };

        if (failures.Count > 0)
        {
            lines.Add("");
            lines.Add($"<b>Could not check {failures.Count} source{(failures.Count == 1 ? "" : "s")}:</b>");
            foreach (var f in failures)
                lines.Add($"- {Esc(f.SourceName)}: {Esc(Truncate(f.Reason, 450))}");
        }

        return Chunk(lines, DefaultMaxLength);
    }

    public static IReadOnlyList<string> FormatAllFailed(IReadOnlyList<SourceFailure> failures)
    {
        var lines = new List<string>
        {
            "<b>Job Alert Agent: every source failed</b>",
            "No career site could be checked today, so this is not a 'no new jobs' result.",
        };
        foreach (var f in failures)
            lines.Add($"- {Esc(f.SourceName)}: {Esc(Truncate(f.Reason, 450))}");
        return Chunk(lines, DefaultMaxLength);
    }

    internal static string FormatJob(MatchedJob m)
    {
        var job = m.Job;
        var sb = new StringBuilder();
        sb.Append("- <b>").Append(Esc(job.SourceName)).Append("</b>: ");
        sb.Append("<a href=\"").Append(Esc(job.Url)).Append("\">").Append(Esc(Truncate(job.Title, 140))).Append("</a>");
        if (!string.IsNullOrWhiteSpace(job.Location))
            sb.Append(" (").Append(Esc(Truncate(job.Location, 80))).Append(')');
        sb.Append(" [").Append(Esc(string.Join(", ", m.MatchedKeywords))).Append(']');
        return sb.ToString();
    }

    internal static IReadOnlyList<string> Chunk(IReadOnlyList<string> lines, int maxLength)
    {
        var messages = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            var safeLine = line.Length > maxLength ? line[..maxLength] : line;
            if (current.Length > 0 && current.Length + 1 + safeLine.Length > maxLength)
            {
                messages.Add(current.ToString().TrimEnd());
                current.Clear();
            }

            if (current.Length > 0) current.Append('\n');
            current.Append(safeLine);
        }

        if (current.Length > 0) messages.Add(current.ToString().TrimEnd());
        return messages;
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "...";
}

