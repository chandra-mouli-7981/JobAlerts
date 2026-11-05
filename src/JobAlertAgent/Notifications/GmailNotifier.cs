using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;

namespace JobAlertAgent.Notifications;

/// <summary>Abstraction over the SMTP transport so the notifier can be unit-tested without a network.</summary>
public interface ISmtpSender
{
    Task SendAsync(MailMessage message, CancellationToken ct);
}

/// <summary>Sends through Gmail's SMTP server (smtp.gmail.com:587, STARTTLS) with an app password.</summary>
public sealed class GmailSmtpSender : ISmtpSender
{
    private readonly string _address;
    private readonly string _appPassword;

    public GmailSmtpSender(string address, string appPassword)
    {
        _address = address;
        _appPassword = appPassword;
    }

    public async Task SendAsync(MailMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient("smtp.gmail.com", 587)
        {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_address, _appPassword),
            Timeout = 60_000,
        };

        await client.SendMailAsync(message, ct).ConfigureAwait(false);
    }
}

/// <summary>Sends the digest as an HTML email.</summary>
public sealed partial class GmailNotifier : INotifier
{
    private readonly ISmtpSender _sender;
    private readonly string _from;
    private readonly string _to;

    /// <param name="from">The Gmail address used to log in and as the sender.</param>
    /// <param name="to">Recipient. Defaults to <paramref name="from"/> (you email yourself).</param>
    public GmailNotifier(ISmtpSender sender, string from, string? to = null)
    {
        if (string.IsNullOrWhiteSpace(from)) throw new ArgumentException("Sender address is required.", nameof(from));
        _sender = sender;
        _from = from.Trim();
        _to = string.IsNullOrWhiteSpace(to) ? _from : to.Trim();
    }

    public static GmailNotifier Create(string address, string appPassword, string? to = null) =>
        new(new GmailSmtpSender(address.Trim(), appPassword.Replace(" ", "")), address, to);

    public async Task SendAsync(string htmlMessage, CancellationToken ct)
    {
        using var mail = new MailMessage
        {
            From = new MailAddress(_from),
            Subject = BuildSubject(htmlMessage),
            Body = BuildBody(htmlMessage),
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };
        mail.To.Add(new MailAddress(_to));

        try
        {
            await _sender.SendAsync(mail, ct).ConfigureAwait(false);
        }
        catch (SmtpException ex)
        {
            throw new EmailException(Describe(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not EmailException)
        {
            throw new EmailException($"Could not send email: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>The first line of the message, stripped of tags, becomes the subject.</summary>
    internal static string BuildSubject(string html)
    {
        var firstLine = html.Split('\n', 2)[0];
        var text = WebUtility.HtmlDecode(AnyTag().Replace(firstLine, "")).Trim();
        return string.IsNullOrEmpty(text) ? "Job Alert Agent" : text;
    }

    /// <summary>
    /// Turns the digest lines into an HTML page: lines starting with "- " become list items,
    /// everything else becomes a paragraph. Inline tags (&lt;b&gt;, &lt;a&gt;) are already valid HTML.
    /// </summary>
    internal static string BuildBody(string html)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;line-height:1.5\">");

        var inList = false;
        foreach (var line in html.Split('\n'))
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!inList) { sb.Append("<ul>"); inList = true; }
                sb.Append("<li>").Append(line.AsSpan(2)).Append("</li>");
                continue;
            }

            if (inList) { sb.Append("</ul>"); inList = false; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            sb.Append("<p>").Append(line).Append("</p>");
        }

        if (inList) sb.Append("</ul>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Describe(SmtpException ex)
    {
        // SmtpException often just says "Failure sending mail."; the real reason is in the inner exception.
        var detail = ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.GetBaseException().Message})";
        if (detail.Contains("5.7.8", StringComparison.Ordinal)
            || detail.Contains("535", StringComparison.Ordinal)
            || detail.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
        {
            return "Gmail rejected the login. Use a Google app password (needs 2-Step Verification), "
                   + $"not your normal password. Server said: {detail}";
        }

        return $"Could not send email: {detail}";
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();
}

public sealed class EmailException : Exception
{
    public EmailException(string message) : base(message) { }
}
