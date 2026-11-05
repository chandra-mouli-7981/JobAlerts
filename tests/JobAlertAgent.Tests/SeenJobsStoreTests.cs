using JobAlertAgent.Models;
using JobAlertAgent.State;

namespace JobAlertAgent.Tests;

public sealed class SeenJobsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jobalert-tests-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "nested", "seen-jobs.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Job J(string id, string source = "Co") => new()
    {
        SourceName = source, Id = id, Title = "t", Url = "https://x",
    };

    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Missing_file_means_first_run_and_empty_store()
    {
        var store = SeenJobsStore.Load(StatePath);

        Assert.False(store.ExistedOnLoad);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Save_creates_directories_and_roundtrips()
    {
        var store = SeenJobsStore.Load(StatePath);
        Assert.True(store.MarkSeen(J("1"), T0));
        Assert.False(store.MarkSeen(J("1"), T0));
        store.MarkSeen(J("2", "Other"), T0);
        store.Save();

        var reloaded = SeenJobsStore.Load(StatePath);

        Assert.True(reloaded.ExistedOnLoad);
        Assert.Equal(2, reloaded.Count);
        Assert.True(reloaded.Contains(J("1")));
        Assert.True(reloaded.Contains(J("2", "Other")));
        Assert.False(reloaded.Contains(J("2"))); // same id, different company => different key
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public void Save_is_deterministic_so_unchanged_state_produces_no_git_diff()
    {
        var a = SeenJobsStore.Load(StatePath);
        a.MarkSeen(J("b"), T0);
        a.MarkSeen(J("a"), T0);
        a.Save();
        var first = File.ReadAllText(StatePath);

        var b = SeenJobsStore.Load(StatePath);
        b.MarkSeen(J("a"), T0.AddDays(1)); // within touch granularity: must not rewrite timestamps
        b.Save();

        Assert.Equal(first, File.ReadAllText(StatePath));
    }

    [Fact]
    public void Prune_removes_only_entries_not_seen_within_retention()
    {
        var store = SeenJobsStore.Load(StatePath);
        store.MarkSeen(J("old"), T0);
        store.MarkSeen(J("fresh"), T0.AddDays(50));

        var removed = store.Prune(T0.AddDays(61), retentionDays: 60);

        Assert.Equal(1, removed);
        Assert.False(store.Contains(J("old")));
        Assert.True(store.Contains(J("fresh")));
    }

    [Fact]
    public void Touch_keeps_long_lived_jobs_from_being_pruned()
    {
        var store = SeenJobsStore.Load(StatePath);
        store.MarkSeen(J("evergreen"), T0);

        for (var day = 8; day <= 120; day += 8)
        {
            store.Touch(J("evergreen"), T0.AddDays(day));
            store.Prune(T0.AddDays(day), retentionDays: 60);
        }

        Assert.True(store.Contains(J("evergreen")));
    }
}
