using System.Net;
using JobAlertAgent.Models;
using JobAlertAgent.Notifications;
using JobAlertAgent.Tests.Helpers;

namespace JobAlertAgent.Tests;

public class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);

    private static MatchedJob M(string source, string id, string title, string location = "Pune", params string[] kw) =>
        new(new Job { SourceName = source, Id = id, Title = title, Location = location, Url = $"https://jobs/{id}?a=1&b=2" },
            kw.Length == 0 ? new[] { ".NET" } : kw);

    [Fact]
    public void Digest_escapes_html_and_lists_company_title_location_and_keywords()
    {
        var msgs = DigestFormatter.FormatDigest(
            new[] { M("Acme <Corp>", "1", "Dev & <b>Lead</b>", "A, B", ".NET", "Azure") },
            Array.Empty<SourceFailure>(), 3, Now, new[] { "Acme <Corp>" });

        var text = Assert.Single(msgs);
        Assert.Contains("Job alert 25 Oct 2026: 1 new opening", text);
        Assert.Contains("<b>Acme &lt;Corp&gt;</b>", text);
        Assert.Contains("Dev &amp; &lt;b&gt;Lead&lt;/b&gt;", text);
        Assert.Contains("href=\"https://jobs/1?a=1&amp;b=2\"", text);
        Assert.Contains("(A, B)", text);
        Assert.Contains("[.NET, Azure]", text);
    }

    [Fact]
    public void Digest_orders_by_configured_source_order()
    {
        var msgs = DigestFormatter.FormatDigest(
            new[] { M("Zed", "1", "Zed job"), M("Alpha", "2", "Alpha job") },
            Array.Empty<SourceFailure>(), 2, Now, new[] { "Zed", "Alpha" });

        var text = Assert.Single(msgs);
        Assert.True(text.IndexOf("Zed job", StringComparison.Ordinal) < text.IndexOf("Alpha job", StringComparison.Ordinal));
    }

    [Fact]
    public void Digest_is_a_single_message_by_default_even_for_hundreds_of_jobs()
    {
        var jobs = Enumerable.Range(1, 800).Select(i => M("Acme", i.ToString(), $"Senior Engineer number {i}")).ToList();

        var msgs = DigestFormatter.FormatDigest(jobs, Array.Empty<SourceFailure>(), 1, Now, new[] { "Acme" });

        var text = Assert.Single(msgs);
        Assert.Equal(800, text.Split('\n').Count(l => l.StartsWith("- <b>Acme</b>")));
    }

    [Fact]
    public void Digest_splits_into_messages_below_an_explicit_limit_without_cutting_lines()
    {
        var jobs = Enumerable.Range(1, 200).Select(i => M("Acme", i.ToString(), $"Senior Engineer number {i}")).ToList();
        const int limit = 3800;

        var msgs = DigestFormatter.FormatDigest(jobs, Array.Empty<SourceFailure>(), 1, Now, new[] { "Acme" }, limit);

        Assert.True(msgs.Count > 1);
        Assert.All(msgs, m => Assert.True(m.Length <= limit));
        Assert.Equal(200, msgs.SelectMany(m => m.Split('\n')).Count(l => l.StartsWith("- <b>Acme</b>")));
        Assert.All(msgs.SelectMany(m => m.Split('\n')).Where(l => l.StartsWith("- <b>")), l => Assert.EndsWith("]", l));
    }

    [Fact]
    public void Digest_lists_failed_sources_even_when_there_are_no_new_jobs()
    {
        var msgs = DigestFormatter.FormatDigest(Array.Empty<MatchedJob>(),
            new[] { new SourceFailure("PhonePe", "HTTP 404 <x>") }, 5, Now, Array.Empty<string>());

        var text = Assert.Single(msgs);
        Assert.Contains("No new openings today (checked 5 sources)", text);
        Assert.Contains("Could not check 1 source:", text);
        Assert.Contains("PhonePe: HTTP 404 &lt;x&gt;", text);
    }

    [Fact]
    public void Long_titles_and_locations_are_truncated()
    {
        var text = DigestFormatter.FormatJob(M("A", "1", new string('T', 500), new string('L', 500)));

        Assert.True(text.Length < 500);
        Assert.Contains("...", text);
    }

    private sealed class FakeSmtp : ISmtpSender
    {
        public List<System.Net.Mail.MailMessage> Sent { get; } = new();
        public Exception? Throw { get; set; }
        public List<(string Subject, string Body, string From, string To)> Captured { get; } = new();

        public Task SendAsync(System.Net.Mail.MailMessage message, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            Captured.Add((message.Subject, message.Body, message.From!.Address, message.To.Single().Address));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Email_uses_first_line_as_subject_and_renders_list_items()
    {
        var smtp = new FakeSmtp();
        var message = "<b>Job alert 25 Oct 2026: 2 new openings</b>\n\n"
                      + "- <b>Acme</b>: <a href=\"https://j/1\">Dev &amp; Lead</a> (Pune) [.NET]\n"
                      + "- <b>Beta</b>: <a href=\"https://j/2\">Eng</a> [Azure]\n\n"
                      + "<b>Could not check 1 source:</b>\n- Gamma: HTTP 404";

        await new GmailNotifier(smtp, "me@gmail.com").SendAsync(message, default);

        var mail = Assert.Single(smtp.Captured);
        Assert.Equal("Job alert 25 Oct 2026: 2 new openings", mail.Subject);
        Assert.Equal("me@gmail.com", mail.From);
        Assert.Equal("me@gmail.com", mail.To); // defaults to emailing yourself
        Assert.Contains("<ul><li><b>Acme</b>: <a href=\"https://j/1\">Dev &amp; Lead</a> (Pune) [.NET]</li>"
                        + "<li><b>Beta</b>", mail.Body);
        Assert.Contains("<p><b>Could not check 1 source:</b></p><ul><li>Gamma: HTTP 404</li></ul>", mail.Body);
    }

    [Fact]
    public async Task Email_subject_decodes_entities_and_honours_explicit_recipient()
    {
        var smtp = new FakeSmtp();

        await new GmailNotifier(smtp, " me@gmail.com ", " other@example.com ")
            .SendAsync("<b>Tom &amp; Jerry</b>", default);

        var mail = Assert.Single(smtp.Captured);
        Assert.Equal("Tom & Jerry", mail.Subject);
        Assert.Equal("other@example.com", mail.To);
    }

    [Fact]
    public async Task Email_login_failure_gives_actionable_error_without_leaking_secrets()
    {
        var smtp = new FakeSmtp
        {
            Throw = new System.Net.Mail.SmtpException(
                "5.7.8 Username and Password not accepted. For more information, go to https://support.google.com"),
        };

        var ex = await Assert.ThrowsAsync<EmailException>(() =>
            new GmailNotifier(smtp, "me@gmail.com").SendAsync("<b>x</b>", default));

        Assert.Contains("app password", ex.Message);
        Assert.Contains("2-Step Verification", ex.Message);
    }

    [Fact]
    public async Task Email_network_failure_is_wrapped_in_EmailException()
    {
        var smtp = new FakeSmtp { Throw = new HttpRequestException("no route") };

        var ex = await Assert.ThrowsAsync<EmailException>(() =>
            new GmailNotifier(smtp, "me@gmail.com").SendAsync("<b>x</b>", default));

        Assert.Contains("no route", ex.Message);
    }

    [Fact]
    public async Task Email_cancellation_is_not_wrapped()
    {
        var smtp = new FakeSmtp { Throw = new OperationCanceledException() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GmailNotifier(smtp, "me@gmail.com").SendAsync("<b>x</b>", default));
    }

    [Fact]
    public void Email_requires_sender_address()
    {
        Assert.Throws<ArgumentException>(() => new GmailNotifier(new FakeSmtp(), " "));
    }
}
