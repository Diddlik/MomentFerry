using System.Collections.Concurrent;

namespace MomentFerry.Web.Diagnostics;

/// <summary>
/// Bounded in-memory ring of the application's own log records, so the Web UI can show why a file was
/// skipped or held without access to the container log. Deliberately not persisted: the audit trail
/// lives in the operation history, this is only the recent-activity view.
/// </summary>
public sealed class ActivityLog(int capacity = 500)
{
    private readonly ConcurrentQueue<ActivityLogEntry> _entries = new();
    private long _sequence;

    public void Add(DateTimeOffset at, LogLevel level, string category, string message)
    {
        _entries.Enqueue(new ActivityLogEntry(
            Interlocked.Increment(ref _sequence),
            at,
            level.ToString(),
            category,
            message));

        while (_entries.Count > capacity && _entries.TryDequeue(out _)) { }
    }

    public IReadOnlyList<ActivityLogEntry> Recent(int limit, LogLevel minimumLevel) =>
        _entries
            .Where(x => Enum.Parse<LogLevel>(x.Level) >= minimumLevel)
            .OrderByDescending(x => x.Sequence)
            .Take(limit)
            .ToArray();
}

public sealed record ActivityLogEntry(
    long Sequence,
    DateTimeOffset At,
    string Level,
    string Category,
    string Message);
