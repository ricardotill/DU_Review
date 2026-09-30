using System.Collections.Concurrent;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Slows down guessing: after too many wrong keys from one address, it has to wait.
/// </summary>
public class LoginAttemptLimiter
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();

    public LoginAttemptLimiter(TimeProvider time)
    {
        _time = time;
    }

    public bool IsBlocked(string client) => RecentFailures(client) >= MaxFailures;

    public void RecordFailure(string client)
    {
        var failures = _failures.GetOrAdd(client, _ => new List<DateTimeOffset>());
        lock (failures) failures.Add(_time.GetUtcNow());
    }

    public void Reset(string client) => _failures.TryRemove(client, out _);

    private int RecentFailures(string client)
    {
        if (!_failures.TryGetValue(client, out var failures)) return 0;

        var since = _time.GetUtcNow() - Window;
        lock (failures)
        {
            failures.RemoveAll(f => f < since);
            return failures.Count;
        }
    }
}
