using JobAlertAgent.Matching;
using JobAlertAgent.Models;
using JobAlertAgent.Notifications;
using JobAlertAgent.Sources;
using JobAlertAgent.State;

namespace JobAlertAgent;

public sealed class RunOptions
{
    /// <summary>Print the digest, do not send it and do not touch the state file.</summary>
    public bool DryRun { get; init; }

    /// <summary>On the very first run send every current match instead of silently seeding the state.</summary>
    public bool InitialDigest { get; init; }

    public int MaxConcurrentSources { get; init; } = 4;
}

public enum RunOutcome
{
    /// <summary>Everything worked (individual source failures are reported but tolerated).</summary>
    Success,

    /// <summary>Every enabled source failed - nothing useful could be checked.</summary>
    AllSourcesFailed,
}

public sealed record RunResult(
    RunOutcome Outcome,
    int SourcesChecked,
    int JobsFetched,
    int Matched,
    int NewJobs,
    bool Seeded,
    IReadOnlyList<SourceFailure> Failures);

public sealed class JobAlertRunner
{
    private readonly AppConfig _config;
    private readonly SourceRegistry _registry;
    private readonly SeenJobsStore _store;
    private readonly INotifier _notifier;
    private readonly TextWriter _log;
    private readonly Func<DateTimeOffset> _clock;

    public JobAlertRunner(
        AppConfig config,
        SourceRegistry registry,
        SeenJobsStore store,
        INotifier notifier,
        TextWriter? log = null,
        Func<DateTimeOffset>? clock = null)
    {
        _config = config;
        _registry = registry;
        _store = store;
        _notifier = notifier;
        _log = log ?? Console.Out;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<RunResult> RunAsync(RunOptions options, CancellationToken ct)
    {
        var now = _clock();
        var matcher = new KeywordMatcher(_config.Keywords);
        var sources = _config.Sources.Where(s => s.Enabled).ToList();

        // 1. Fetch every source, tolerating individual failures.
        var failures = new List<SourceFailure>();
        var matched = new Dictionary<string, MatchedJob>(StringComparer.Ordinal);
        var fetched = 0;
        var gate = new object();

        using var throttle = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentSources));
        await Task.WhenAll(sources.Select(async source =>
        {
            // A mistake in one config entry must not stop the others: report it like any failed source.
            if (source.ConfigError is not null)
            {
                lock (gate) failures.Add(new SourceFailure(source.Name, "Config problem: " + source.ConfigError));
                _log.WriteLine($"[FAIL] {source.Name}: Config problem: {source.ConfigError}");
                return;
            }

            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var impl = _registry.Get(source.Provider);
                var fetchedJobs = await impl.FetchAsync(source, matcher.Terms, ct).ConfigureAwait(false);
                var jobs = LocationFilter.Apply(source, fetchedJobs);
                var hits = jobs.Select(matcher.Match).Where(m => m is not null).Select(m => m!).ToList();

                lock (gate)
                {
                    fetched += fetchedJobs.Count;
                    foreach (var m in hits) matched.TryAdd(m.Job.Key, m);
                }

                _log.WriteLine($"[ok]   {source.Name}: {fetchedJobs.Count} openings fetched" + (jobs.Count != fetchedJobs.Count ? $", {jobs.Count} in selected locations" : "") + $", {hits.Count} match");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = ex.GetBaseException().Message;
                lock (gate) failures.Add(new SourceFailure(source.Name, reason));
                _log.WriteLine($"[FAIL] {source.Name}: {reason}");
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);

        failures.Sort((a, b) => string.Compare(a.SourceName, b.SourceName, StringComparison.OrdinalIgnoreCase));

        var checkedCount = sources.Count - failures.Count;
        if (sources.Count > 0 && checkedCount == 0)
        {
            // Still tell the user: silence would look like "no new jobs".
            foreach (var message in DigestFormatter.FormatAllFailed(failures))
                await _notifier.SendAsync(message, ct).ConfigureAwait(false);
            return new RunResult(RunOutcome.AllSourcesFailed, 0, 0, 0, 0, false, failures);
        }

        // 2. Decide what is new.
        var allMatched = matched.Values.ToList();
        var seeding = !_store.ExistedOnLoad && !options.InitialDigest;
        var newJobs = allMatched.Where(m => !_store.Contains(m.Job)).ToList();

        // 3. Notify first; only record jobs as seen once the message really went out,
        //    so a failed delivery is retried on the next run instead of being lost.
        if (seeding)
        {
            if (!options.DryRun)
            {
                foreach (var message in DigestFormatter.FormatSeedMessage(allMatched.Count, failures, checkedCount, now))
                    await _notifier.SendAsync(message, ct).ConfigureAwait(false);
            }
            else
            {
                // Show what would be indexed so a dry run is useful for checking the filters.
                _log.WriteLine($"[dry-run] First run: a real run would seed these {allMatched.Count} openings silently.");
                var order = _config.Sources.Select(s => s.Name).ToList();
                foreach (var message in DigestFormatter.FormatDigest(allMatched, failures, checkedCount, now, order))
                    await _notifier.SendAsync(message, ct).ConfigureAwait(false);
            }
        }
        else
        {
            var order = _config.Sources.Select(s => s.Name).ToList();
            foreach (var message in DigestFormatter.FormatDigest(newJobs, failures, checkedCount, now, order))
                await _notifier.SendAsync(message, ct).ConfigureAwait(false);
        }

        // 4. Persist state.
        if (!options.DryRun)
        {
            foreach (var m in allMatched) _store.MarkSeen(m.Job, now);
            var pruned = _store.Prune(now, _config.SeenRetentionDays);
            _store.Save();
            _log.WriteLine($"State saved: {_store.Count} tracked jobs ({pruned} pruned).");
        }

        return new RunResult(RunOutcome.Success, checkedCount, fetched, allMatched.Count,
            seeding ? 0 : newJobs.Count, seeding, failures);
    }
}

