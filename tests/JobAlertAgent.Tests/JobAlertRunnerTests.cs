using JobAlertAgent.Models;
using JobAlertAgent.Notifications;
using JobAlertAgent.Sources;
using JobAlertAgent.State;

namespace JobAlertAgent.Tests;

public sealed class JobAlertRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jobalert-runner-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "seen.json");
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeSource : IJobSource
    {
        public Func<SourceConfig, IReadOnlyList<Job>> Jobs { get; set; } = _ => Array.Empty<Job>();
        public string Provider => "greenhouse";

        public Task<IReadOnlyList<Job>> FetchAsync(SourceConfig source, IReadOnlyList<string> terms, CancellationToken ct) =>
            Task.FromResult(Jobs(source));
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<string> Messages { get; } = new();
        public bool Fail { get; set; }

        public Task SendAsync(string htmlMessage, CancellationToken ct)
        {
            if (Fail) throw new EmailException("boom");
            Messages.Add(htmlMessage);
            return Task.CompletedTask;
        }
    }

    private static Job J(string source, string id, string title, string? desc = null) => new()
    {
        SourceName = source, Id = id, Title = title, Url = $"https://jobs/{source}/{id}", Description = desc,
    };

    private static AppConfig Config(params string[] sources) => new()
    {
        Keywords = { new KeywordConfig { Term = ".NET" }, new KeywordConfig { Term = "Azure", Scope = MatchScope.Title } },
        Sources = sources.Select(n => new SourceConfig { Name = n, Provider = "greenhouse", Slug = n.ToLowerInvariant() }).ToList(),
    };

    private JobAlertRunner Runner(AppConfig config, FakeSource source, SeenJobsStore store, INotifier notifier) =>
        new(config, new SourceRegistry(new IJobSource[] { source }), store, notifier, TextWriter.Null, () => Now);

    [Fact]
    public async Task First_run_seeds_state_silently_and_sends_only_a_setup_message()
    {
        var source = new FakeSource { Jobs = _ => new[] { J("A", "1", ".NET Developer"), J("A", "2", "Cook") } };
        var notifier = new RecordingNotifier();
        var store = SeenJobsStore.Load(StatePath);

        var result = await Runner(Config("A"), source, store, notifier).RunAsync(new RunOptions(), default);

        Assert.True(result.Seeded);
        Assert.Equal(0, result.NewJobs);
        Assert.Equal(1, result.Matched);
        var message = Assert.Single(notifier.Messages);
        Assert.Contains("set up", message);
        Assert.DoesNotContain(".NET Developer", message);
        Assert.True(SeenJobsStore.Load(StatePath).Contains(J("A", "1", "x")));
    }

    [Fact]
    public async Task Second_run_reports_only_jobs_not_seen_before()
    {
        var jobs = new List<Job> { J("A", "1", ".NET Developer") };
        var source = new FakeSource { Jobs = _ => jobs };

        await Runner(Config("A"), source, SeenJobsStore.Load(StatePath), new RecordingNotifier())
            .RunAsync(new RunOptions(), default);

        jobs.Add(J("A", "3", "Senior .NET Engineer"));
        var notifier = new RecordingNotifier();
        var result = await Runner(Config("A"), source, SeenJobsStore.Load(StatePath), notifier)
            .RunAsync(new RunOptions(), default);

        Assert.False(result.Seeded);
        Assert.Equal(1, result.NewJobs);
        var message = Assert.Single(notifier.Messages);
        Assert.Contains("1 new opening", message);
        Assert.Contains("Senior .NET Engineer", message);
        Assert.DoesNotContain(">.NET Developer<", message);
    }

    [Fact]
    public async Task Run_with_nothing_new_still_sends_a_heartbeat_message()
    {
        var source = new FakeSource { Jobs = _ => new[] { J("A", "1", ".NET Developer") } };
        await Runner(Config("A"), source, SeenJobsStore.Load(StatePath), new RecordingNotifier())
            .RunAsync(new RunOptions(), default);

        var notifier = new RecordingNotifier();
        await Runner(Config("A"), source, SeenJobsStore.Load(StatePath), notifier).RunAsync(new RunOptions(), default);

        Assert.Contains("No new openings today", Assert.Single(notifier.Messages));
    }

    [Fact]
    public async Task Initial_digest_option_sends_everything_on_first_run()
    {
        var source = new FakeSource { Jobs = _ => new[] { J("A", "1", ".NET Developer") } };
        var notifier = new RecordingNotifier();

        var result = await Runner(Config("A"), source, SeenJobsStore.Load(StatePath), notifier)
            .RunAsync(new RunOptions { InitialDigest = true }, default);

        Assert.False(result.Seeded);
        Assert.Contains(".NET Developer", Assert.Single(notifier.Messages));
    }

    [Fact]
    public async Task Failed_source_is_reported_and_other_sources_still_work()
    {
        var source = new FakeSource
        {
            Jobs = s => s.Name == "Bad" ? throw new HttpRequestException("HTTP 404 for x") : new[] { J("Good", "1", ".NET Dev") },
        };
        var notifier = new RecordingNotifier();
        await Runner(Config("Good", "Bad"), source, SeenJobsStore.Load(StatePath), new RecordingNotifier())
            .RunAsync(new RunOptions(), default); // seed

        var result = await Runner(Config("Good", "Bad"), source, SeenJobsStore.Load(StatePath), notifier)
            .RunAsync(new RunOptions(), default);

        Assert.Equal(RunOutcome.Success, result.Outcome);
        Assert.Equal(1, result.SourcesChecked);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("Bad", failure.SourceName);
        var message = Assert.Single(notifier.Messages);
        Assert.Contains("Could not check 1 source", message);
        Assert.Contains("Bad: HTTP 404", message);
    }

    [Fact]
    public async Task Config_problem_in_one_source_is_reported_and_does_not_stop_the_others()
    {
        var called = new List<string>();
        var source = new FakeSource
        {
            Jobs = s => { called.Add(s.Name); return new[] { J(s.Name, "1", ".NET Dev") }; },
        };
        var config = Config("Good");
        config.Sources.Add(new SourceConfig { Name = "Typo", ConfigError = "Don't recognise the career site in 'x'." });
        await Runner(config, source, SeenJobsStore.Load(StatePath), new RecordingNotifier())
            .RunAsync(new RunOptions(), default); // seed

        var notifier = new RecordingNotifier();
        var result = await Runner(config, source, SeenJobsStore.Load(StatePath), notifier)
            .RunAsync(new RunOptions(), default);

        Assert.Equal(RunOutcome.Success, result.Outcome);
        Assert.Equal(new[] { "Good", "Good" }, called); // "Typo" was never fetched
        Assert.Equal(1, result.SourcesChecked);
        var message = Assert.Single(notifier.Messages);
        Assert.Contains("Typo: Config problem: Don", message);
        Assert.Contains("recognise the career site", message);
    }

    [Fact]
    public async Task All_sources_failing_is_flagged_and_does_not_look_like_no_new_jobs()
    {
        var source = new FakeSource { Jobs = _ => throw new HttpRequestException("down") };
        var notifier = new RecordingNotifier();

        var result = await Runner(Config("A", "B"), source, SeenJobsStore.Load(StatePath), notifier)
            .RunAsync(new RunOptions(), default);

        Assert.Equal(RunOutcome.AllSourcesFailed, result.Outcome);
        var message = Assert.Single(notifier.Messages);
        Assert.Contains("every source failed", message);
        Assert.DoesNotContain("No new openings", message);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task Failed_notification_leaves_state_untouched_so_jobs_are_retried_next_run()
    {
        var jobs = new List<Job> { J("A", "1", ".NET Developer") };
        var source = new FakeSource { Jobs = _ => jobs };
        await Runner(Config("A"), source, SeenJobsStore.Load(StatePath), new RecordingNotifier())
            .RunAsync(new RunOptions(), default); // seed
        var seeded = File.ReadAllText(StatePath);

        jobs.Add(J("A", "2", ".NET Architect"));
        await Assert.ThrowsAsync<EmailException>(() =>
            Runner(Config("A"), source, SeenJobsStore.Load(StatePath), new RecordingNotifier { Fail = true })
                .RunAsync(new RunOptions(), default));
        Assert.Equal(seeded, File.ReadAllText(StatePath));

        var notifier = new RecordingNotifier();
        await Runner(Config("A"), source, SeenJobsStore.Load(StatePath), notifier).RunAsync(new RunOptions(), default);
        Assert.Contains(".NET Architect", Assert.Single(notifier.Messages));
    }

    [Fact]
    public async Task Dry_run_neither_sends_through_real_channel_nor_writes_state()
    {
        var source = new FakeSource { Jobs = _ => new[] { J("A", "1", ".NET Developer") } };
        var notifier = new RecordingNotifier();

        await Runner(Config("A"), source, SeenJobsStore.Load(StatePath), notifier)
            .RunAsync(new RunOptions { DryRun = true }, default);

        Assert.False(File.Exists(StatePath));
        Assert.Contains(".NET Developer", Assert.Single(notifier.Messages)); // shown via the (console) notifier
    }

    [Fact]
    public async Task Disabled_sources_are_skipped()
    {
        var called = new List<string>();
        var source = new FakeSource { Jobs = s => { called.Add(s.Name); return Array.Empty<Job>(); } };
        var config = Config("A", "B");
        config.Sources[1].Enabled = false;

        await Runner(config, source, SeenJobsStore.Load(StatePath), new RecordingNotifier()).RunAsync(new RunOptions(), default);

        Assert.Equal(new[] { "A" }, called);
    }

    [Fact]
    public async Task Same_job_is_not_reported_twice_within_one_run()
    {
        var source = new FakeSource { Jobs = _ => new[] { J("A", "1", ".NET Dev"), J("A", "1", ".NET Dev") } };
        await Runner(Config("A"), new FakeSource(), SeenJobsStore.Load(StatePath), new RecordingNotifier())
            .RunAsync(new RunOptions(), default); // seed with empty

        var notifier = new RecordingNotifier();
        var result = await Runner(Config("A"), source, SeenJobsStore.Load(StatePath), notifier)
            .RunAsync(new RunOptions(), default);

        Assert.Equal(1, result.NewJobs);
    }
}

