namespace AsyncFanOut.Models;

/// <summary>
/// Represents the lifecycle state of a single task slot within an <see cref="AggregationResult"/>.
/// </summary>
public enum TaskState
{
    /// <summary>Value was served from the cache without invoking the downstream factory.</summary>
    Cached,

    /// <summary>Task was started but had not yet completed at the time the result snapshot was taken.</summary>
    Loading,

    /// <summary>Task completed successfully and a value is available.</summary>
    Completed,

    /// <summary>Task threw an unhandled exception. The exception is available via <see cref="TaskMetadata.Error"/>.</summary>
    Error,

    /// <summary>Task exceeded its configured per-task timeout.</summary>
    TimedOut
}
