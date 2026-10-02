using System.Collections.Concurrent;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Slows down guessing: after too many wrong keys from one address, it has to wait.
/// </summary>
public class LoginAttemptLimiter
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Above this many addresses, addresses without recent failures are forgotten, so the list cannot grow forever.</summary>
    public const int PruneAbove = 1000;

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();

    public LoginAttemptLimiter(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>The number of addresses remembered; for tests.</summary>
    public int TrackedClients => _failures.Count;

    public bool IsBlocked(string client) => RecentFailures(client) >= MaxFailures;

    public void RecordFailure(string client)
    {
        var failures = _failures.GetOrAdd(client, _ => new List<DateTimeOffset>());
        lock (failures) failures.Add(_time.GetUtcNow());
        PruneIfLarge();
    }

    /// <summary>
    /// Checks and counts an attempt in one step, so many attempts sent at the same moment cannot all slip through
    /// before the first is counted. The attempt counts as a failure until <see cref="Reset"/> is called after a right key.
    /// </summary>
    /// <returns>False when the address has to wait.</returns>
    public bool TryAttempt(string client)
    {
        var failures = _failures.GetOrAdd(client, _ => new List<DateTimeOffset>());
        bool allowed;
        lock (failures)
        {
            var since = _time.GetUtcNow() - Window;
            failures.RemoveAll(f => f < since);
            allowed = failures.Count < MaxFailures;
            if (allowed) failures.Add(_time.GetUtcNow());
        }

        PruneIfLarge();
        return allowed;
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

    private void PruneIfLarge()
    {
        if (_failures.Count <= PruneAbove) return;

        foreach (var client in _failures.Keys)
        {
            if (RecentFailures(client) == 0) _failures.TryRemove(client, out _);
        }
    }
}
