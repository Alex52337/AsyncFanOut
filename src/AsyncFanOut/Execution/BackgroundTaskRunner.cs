using Microsoft.Extensions.Logging;

namespace AsyncFanOut.Execution;

/// <summary>
/// Default <see cref="IBackgroundTaskRunner"/> implementation.
/// Uses <see cref="Task.Run(Func{Task}, CancellationToken)"/> with
/// <see cref="CancellationToken.None"/> so background work is never
/// cancelled by the caller's token.
/// </summary>
internal sealed class BackgroundTaskRunner : IBackgroundTaskRunner
{
    /// <inheritdoc/>
    public void Run(Func<Task> work, ILogger? logger = null)
    {
        // Deliberately not awaited. CancellationToken.None ensures background
        // work outlives any caller cancellation.
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Unhandled exception in background aggregation task.");
            }
        }, CancellationToken.None);
    }
}
