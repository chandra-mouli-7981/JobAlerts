using System.Text.Json;
using JobAlertAgent.Models;

namespace JobAlertAgent.State;

/// <summary>
/// Persists the keys of jobs that have already been reported so each opening is announced once.
/// Stored as a small, diff-friendly JSON file that the workflow commits back to the repository.
/// </summary>
public sealed class SeenJobsStore
{
    private sealed class Entry
    {
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public SortedDictionary<string, Entry> Jobs { get; set; } = new(StringComparer.Ordinal);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly FileModel _model;

    private SeenJobsStore(string path, FileModel model, bool existed)
    {
        _path = path;
        _model = model;
        ExistedOnLoad = existed;
    }

    /// <summary>False on the very first run (no state file yet) - used to seed instead of spamming.</summary>
    public bool ExistedOnLoad { get; }

    public int Count => _model.Jobs.Count;

    public static SeenJobsStore Load(string path)
    {
        if (!File.Exists(path))
            return new SeenJobsStore(path, new FileModel(), existed: false);

        var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), JsonOptions) ?? new FileModel();
        model.Jobs = new SortedDictionary<string, Entry>(model.Jobs, StringComparer.Ordinal);
        return new SeenJobsStore(path, model, existed: true);
    }

    public bool Contains(Job job) => _model.Jobs.ContainsKey(job.Key);

    /// <summary>Records the job as reported. Returns true if it was new.</summary>
    public bool MarkSeen(Job job, DateTimeOffset now)
    {
        if (_model.Jobs.ContainsKey(job.Key))
        {
            Touch(job, now);
            return false;
        }

        _model.Jobs[job.Key] = new Entry { FirstSeen = now, LastSeen = now };
        return true;
    }

    /// <summary>
    /// Refreshes LastSeen for an already-known job. To avoid rewriting (and committing) the state file
    /// every day, the timestamp is only advanced once it is more than <see cref="TouchGranularity"/> old.
    /// </summary>
    public void Touch(Job job, DateTimeOffset now)
    {
        if (_model.Jobs.TryGetValue(job.Key, out var entry) && now - entry.LastSeen > TouchGranularity)
            entry.LastSeen = now;
    }

    public static readonly TimeSpan TouchGranularity = TimeSpan.FromDays(7);

    /// <summary>Drops entries not seen for <paramref name="retentionDays"/> days. Returns how many were removed.</summary>
    public int Prune(DateTimeOffset now, int retentionDays)
    {
        var cutoff = now - TimeSpan.FromDays(retentionDays);
        var stale = _model.Jobs.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList();
        foreach (var key in stale) _model.Jobs.Remove(key);
        return stale.Count;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write to a temp file first so a crash cannot leave a truncated state file behind.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_model, JsonOptions) + Environment.NewLine);
        File.Move(tmp, _path, overwrite: true);
    }
}
