using System.Net;
using System.Text.RegularExpressions;

namespace JobAlertAgent.Infrastructure;

internal static partial class TextUtil
{
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*\n\s*")]
    private static partial Regex Newlines();

    /// <summary>
    /// Converts HTML (optionally entity-escaped, as Greenhouse returns it) to plain text.
    /// </summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var text = html;
        // Greenhouse sends HTML escaped as &lt;p&gt;...; decode once so the tags become real tags.
        if (text.Contains("&lt;", StringComparison.Ordinal) && !text.Contains('<'))
            text = WebUtility.HtmlDecode(text);

        text = ScriptOrStyle().Replace(text, " ");
        text = BlockBreak().Replace(text, "\n");
        text = Tag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ");
        text = Newlines().Replace(text, "\n");
        return text.Trim();
    }

    public static string Join(params string?[] parts) =>
        string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
