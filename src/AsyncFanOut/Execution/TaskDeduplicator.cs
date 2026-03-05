using System.Collections.Concurrent;

namespace AsyncFanOut.Execution;

/// <summary>
/// Ensures that only a single in-flight <see cref="Task{TResult}"/> exists per cache key
/// at any point in time. Concurrent callers requesting the same key will share the same
/// underlying task rather than triggering duplicate downstream calls.
/// </summary>
/// <remarks>
/// When a task completes (successfully or with an error), it is removed from the dictionary.
/// Subsequent callers will start a fresh task, which will typically be served from the cache
/// if the previous call succeeded.
/// </remarks>
internal sealed class TaskDeduplicator
{
    private readonly ConcurrentDictionary<string, Task<object?>> _inflight = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the existing in-flight task for <paramref name="key"/> if one exists,
    /// otherwise invokes <paramref name="factory"/> and tracks the resulting task.
    /// The task is automatically removed from the dictionary upon completion.
    /// </summary>
    /// <param name="key">The deduplication key (typically the aggregation task key).</param>
    /// <param name="factory">Factory to invoke if no in-flight task exists.</param>
    public Task<object?> GetOrAddAsync(string key, Func<Task<object?>> factory)
    {
        // Fast path: return existing task without allocation.
        if (_inflight.TryGetValue(key, out var existing))
            return existing;

        // Start the task and register it. If another thread races and wins GetOrAdd,
        // we discard our started task and return theirs — the factory may be invoked
        // twice in that narrow race but only one result will be tracked.
        var started = StartAndTrackAsync(key, factory);
        var registered = _inflight.GetOrAdd(key, started);

        // If our task wasn't the winner, ensure it still completes to avoid leaks.
        // (started is already running; we just discard its result.)
        return registered;
    }

    private async Task<object?> StartAndTrackAsync(string key, Func<Task<object?>> factory)
    {
        try
        {
            return await factory().ConfigureAwait(false);
        }
        finally
        {
            // Always remove — whether the task succeeded or faulted — so the next
            // caller gets a fresh attempt (which will likely hit the cache on success).
            _inflight.TryRemove(key, out _);
        }
    }
}
