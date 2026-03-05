namespace AsyncFanOut.Models;

/// <summary>
/// Immutable metadata describing the outcome of one task slot in an <see cref="AggregationResult"/>.
/// </summary>
public sealed record TaskMetadata
{
    /// <summary>The final state of this task.</summary>
    public required TaskState State { get; init; }

    /// <summary>
    /// <see langword="true"/> when this value was served from the cache
    /// without invoking the downstream factory.
    /// </summary>
    public required bool IsFromCache { get; init; }

    /// <summary>
    /// Wall-clock duration of the factory invocation.
    /// <see langword="null"/> when served from cache or when the task is still <see cref="TaskState.Loading"/>.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// The exception captured when <see cref="State"/> is <see cref="TaskState.Error"/>.
    /// <see langword="null"/> otherwise.
    /// </summary>
    public Exception? Error { get; init; }

    internal static TaskMetadata FromCache() =>
        new() { State = TaskState.Cached, IsFromCache = true };

    internal static TaskMetadata Loading() =>
        new() { State = TaskState.Loading, IsFromCache = false };

    internal static TaskMetadata Completed(TimeSpan duration) =>
        new() { State = TaskState.Completed, IsFromCache = false, Duration = duration };

    internal static TaskMetadata ForError(Exception ex, TimeSpan duration) =>
        new() { State = TaskState.Error, IsFromCache = false, Duration = duration, Error = ex };

    internal static TaskMetadata TimedOut(TimeSpan duration) =>
        new() { State = TaskState.TimedOut, IsFromCache = false, Duration = duration };
}
