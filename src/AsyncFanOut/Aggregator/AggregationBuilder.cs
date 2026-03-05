using AsyncFanOut.Models;

namespace AsyncFanOut.Aggregator;

/// <summary>
/// Fluent builder for registering tasks in an aggregation run.
/// </summary>
public sealed class AggregationBuilder
{
    internal List<AggregationTaskBase> Tasks { get; } = [];

    /// <summary>
    /// Registers an async task with the given key and cache TTL.
    /// </summary>
    /// <typeparam name="T">The type of value produced by <paramref name="task"/>.</typeparam>
    /// <param name="key">
    /// A unique cache key. If a cached value exists for this key it will be returned
    /// without invoking <paramref name="task"/>.
    /// </param>
    /// <param name="task">The async factory that produces the value.</param>
    /// <param name="ttl">How long a successful result should be cached.</param>
    /// <param name="timeout">
    /// Optional per-task timeout. When exceeded the result slot will have
    /// <see cref="Models.TaskState.TimedOut"/> state.
    /// </param>
    /// <param name="policyWrapper">
    /// Optional policy wrapper for retry or circuit-breaker behaviour.
    /// Compatible with Polly without a hard dependency:
    /// <code>policyWrapper: inner => retryPolicy.ExecuteAsync(inner)</code>
    /// </param>
    /// <returns>This builder instance for chaining.</returns>
    public AggregationBuilder Add<T>(
        string key,
        Func<Task<T>> task,
        TimeSpan ttl,
        TimeSpan? timeout = null,
        Func<Func<Task<T>>, Task<T>>? policyWrapper = null)
    {
        Tasks.Add(new AggregationTask<T>(key, task, ttl, timeout)
        {
            PolicyWrapper = policyWrapper
        });
        return this;
    }

}
