using System.Collections.Frozen;

namespace AsyncFanOut.Models;

/// <summary>
/// An immutable, point-in-time snapshot of an aggregation run.
/// Values for tasks that were still executing when the snapshot was taken will be
/// <see langword="null"/>; check <see cref="GetMetadata"/> for <see cref="TaskState.Loading"/>.
/// </summary>
public sealed class AggregationResult
{
    private readonly FrozenDictionary<string, object?> _values;
    private readonly FrozenDictionary<string, TaskMetadata> _metadata;

    /// <summary>
    /// <see langword="true"/> when all registered tasks had completed (or been served from cache)
    /// at the time this result was produced.
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>The request-scoped context associated with this aggregation run.</summary>
    public AggregationContext Context { get; }

    internal AggregationResult(
        Dictionary<string, object?> values,
        Dictionary<string, TaskMetadata> metadata,
        bool isComplete,
        AggregationContext context)
    {
        _values = values.ToFrozenDictionary(StringComparer.Ordinal);
        _metadata = metadata.ToFrozenDictionary(StringComparer.Ordinal);
        IsComplete = isComplete;
        Context = context;
    }

    /// <summary>
    /// Returns the value for the specified key cast to <typeparamref name="T"/>,
    /// or <see langword="default"/> if the task was still loading, errored, timed out,
    /// or returned a null value.
    /// </summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="key">The task key as registered in <see cref="Aggregator.AggregationBuilder"/>.</param>
    public T? Get<T>(string key)
    {
        if (_values.TryGetValue(key, out var raw) && raw is T typed)
            return typed;
        return default;
    }

    /// <summary>
    /// Returns the <see cref="TaskMetadata"/> for the specified key.
    /// </summary>
    /// <param name="key">The task key as registered in <see cref="Aggregator.AggregationBuilder"/>.</param>
    /// <exception cref="KeyNotFoundException">Thrown when no task was registered with this key.</exception>
    public TaskMetadata GetMetadata(string key) =>
        _metadata.TryGetValue(key, out var meta)
            ? meta
            : throw new KeyNotFoundException($"No task was registered with key '{key}'.");

    /// <summary>All registered task keys in this result.</summary>
    public IReadOnlyCollection<string> Keys => _metadata.Keys;
}
