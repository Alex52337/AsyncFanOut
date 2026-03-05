using AsyncFanOut.Models;

namespace AsyncFanOut.Aggregator;

/// <summary>
/// Orchestrates parallel downstream calls with cache-first retrieval and
/// partial-result streaming for BFF architectures.
/// </summary>
/// <remarks>
/// <para>
/// On the first call, all uncached tasks start concurrently. The aggregator returns an
/// <see cref="AggregationResult"/> as soon as the first task completes, with remaining
/// task slots marked <see cref="TaskState.Loading"/>. The outstanding tasks continue
/// in the background and populate the cache as they finish.
/// </para>
/// <para>
/// On subsequent calls, cached values are returned immediately without invoking the
/// downstream factory again.
/// </para>
/// </remarks>
public interface ITaskAggregator
{
    /// <summary>
    /// Runs the configured tasks and returns a partial result as soon as the first
    /// uncached task completes.
    /// </summary>
    /// <param name="configure">
    /// Delegate that populates an <see cref="AggregationBuilder"/> with the tasks to run.
    /// </param>
    /// <param name="context">
    /// Optional request-scoped context. A new context with a generated correlation ID
    /// is created if not provided.
    /// </param>
    /// <param name="cancellationToken">
    /// Governs how long the caller waits for the first result.
    /// Does <em>not</em> cancel background completion tasks — those use
    /// <see cref="CancellationToken.None"/> to ensure the cache is always populated.
    /// </param>
    /// <returns>
    /// An <see cref="AggregationResult"/> snapshot. Check <see cref="AggregationResult.IsComplete"/>
    /// and per-key <see cref="AggregationResult.GetMetadata"/> for loading / error states.
    /// </returns>
    Task<AggregationResult> RunAsync(
        Action<AggregationBuilder> configure,
        AggregationContext? context = null,
        CancellationToken cancellationToken = default);
}
