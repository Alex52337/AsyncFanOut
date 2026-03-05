using Microsoft.Extensions.Logging;

namespace AsyncFanOut.Execution;

/// <summary>
/// Runs fire-and-forget async work that must not be tied to the caller's lifecycle or
/// <see cref="System.Threading.CancellationToken"/>.
/// </summary>
public interface IBackgroundTaskRunner
{
    /// <summary>
    /// Schedules <paramref name="work"/> to run in the background.
    /// Exceptions thrown by <paramref name="work"/> are caught and logged rather than
    /// propagated, ensuring unobserved task exceptions do not crash the process.
    /// </summary>
    /// <param name="work">The async delegate to execute.</param>
    /// <param name="logger">Optional logger for capturing background errors.</param>
    void Run(Func<Task> work, ILogger? logger = null);
}
