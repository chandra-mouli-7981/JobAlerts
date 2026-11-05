using JobAlertAgent;
using JobAlertAgent.Models;
using JobAlertAgent.Notifications;
using JobAlertAgent.Sources;
using JobAlertAgent.State;

const string Usage = """
    Job Alert Agent - checks company career sites and emails a digest of new matching openings (Gmail).

    Usage: JobAlertAgent [options]
      --config <path>      Config file (default: config/jobalert.config.json)
      --state <path>       Seen-jobs state file (default: data/seen-jobs.json)
      --dry-run            Print the digest; do not send it and do not modify the state file
      --initial-digest     On the first run, send all current matches instead of silently seeding
      --test-email         Send a test email to verify the Gmail settings below, then exit
      -h, --help           Show this help

    Environment (required unless --dry-run):
      GMAIL_ADDRESS        Gmail account used to log in and send
      GMAIL_APP_PASSWORD   Google app password (needs 2-Step Verification), not the normal password
      MAIL_TO              Optional recipient; defaults to GMAIL_ADDRESS
    """;

string configPath = "config/jobalert.config.json";
string statePath = "data/seen-jobs.json";
bool dryRun = false, initialDigest = false, testEmail = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
        case "--state" when i + 1 < args.Length: statePath = args[++i]; break;
        case "--dry-run": dryRun = true; break;
        case "--initial-digest": initialDigest = true; break;
        case "--test-email": testEmail = true; break;
        case "-h" or "--help": Console.WriteLine(Usage); return 0;
        default:
            Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}\n\n{Usage}");
            return 2;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    INotifier notifier;
    if (dryRun)
    {
        notifier = new ConsoleNotifier();
    }
    else
    {
        var address = Environment.GetEnvironmentVariable("GMAIL_ADDRESS");
        var appPassword = Environment.GetEnvironmentVariable("GMAIL_APP_PASSWORD");
        var mailTo = Environment.GetEnvironmentVariable("MAIL_TO");
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(appPassword))
        {
            Console.Error.WriteLine("GMAIL_ADDRESS and GMAIL_APP_PASSWORD must be set (or use --dry-run).");
            return 2;
        }

        notifier = GmailNotifier.Create(address, appPassword, mailTo);
    }

    if (testEmail)
    {
        await notifier.SendAsync("<b>Job Alert Agent</b>: test email\n- Setup works.", cts.Token);
        Console.WriteLine("Test email sent.");
        return 0;
    }

    var config = AppConfig.Load(configPath);
    var store = SeenJobsStore.Load(statePath);
    var registry = SourceRegistry.CreateDefault(SourceRegistry.CreateHttpClient());

    var runner = new JobAlertRunner(config, registry, store, notifier);
    var result = await runner.RunAsync(
        new RunOptions { DryRun = dryRun, InitialDigest = initialDigest }, cts.Token);

    Console.WriteLine(
        $"Done: {result.SourcesChecked} sources checked, {result.JobsFetched} openings fetched, "
        + $"{result.Matched} matching, {result.NewJobs} new{(result.Seeded ? " (first run: seeded)" : "")}, "
        + $"{result.Failures.Count} source failure(s).");

    return result.Outcome == RunOutcome.AllSourcesFailed ? 1 : 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Run failed: {ex.GetBaseException().Message}");
    return 1;
}
