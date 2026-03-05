using System.Diagnostics;
using AsyncFanOut.Cache;
using AsyncFanOut.Execution;
using AsyncFanOut.Models;
using Microsoft.Extensions.Logging;

namespace AsyncFanOut.Aggregator;

/// <summary>
/// Default <see cref="ITaskAggregator"/> implementation.
/// </summary>
internal sealed class TaskAggregator : ITaskAggregator
{
    private readonly IAggregationCache _cache;
    private readonly TaskDeduplicator _deduplicator;
    private readonly IBackgroundTaskRunner _backgroundRunner;
    private readonly ILogger<TaskAggregator>? _logger;

    public TaskAggregator(
        IAggregationCache cache,
        TaskDeduplicator deduplicator,
        IBackgroundTaskRunner backgroundRunner,
        ILogger<TaskAggregator>? logger = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _deduplicator = deduplicator ?? throw new ArgumentNullException(nameof(deduplicator));
        _backgroundRunner = backgroundRunner ?? throw new ArgumentNullException(nameof(backgroundRunner));
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<AggregationResult> RunAsync(
        Action<AggregationBuilder> configure,
        AggregationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = context ?? new AggregationContext();
        var builder = new AggregationBuilder();
        configure(builder);
        var descriptors = builder.Tasks;

        _logger?.LogDebug(
            "Aggregation run {CorrelationId} starting with {TaskCount} task(s).",
            ctx.CorrelationId, descriptors.Count);

        var values = new Dictionary<string, object?>(descriptors.Count, StringComparer.Ordinal);
        var metadata = new Dictionary<string, TaskMetadata>(descriptors.Count, StringComparer.Ordinal);
        var pending = new List<(AggregationTaskBase Descriptor, Task<object?> Task, Stopwatch Sw)>(descriptors.Count);

        // ── Phase 1: Cache check ────────────────────────────────────────────────
        foreach (var descriptor in descriptors)
        {
            var (found, isStale, cachedValue) = _cache.TryGetWithMeta(descriptor.Key);

            if (found)
            {
                values[descriptor.Key] = cachedValue;
                metadata[descriptor.Key] = TaskMetadata.FromCache();

                _logger?.LogDebug("Cache hit for key '{Key}' (stale={IsStale}).", descriptor.Key, isStale);

                if (isStale)
                {
                    // Stale-while-revalidate: serve stale value now, refresh in background.
                    // Use SafeInvokeAsync here — background refresh must never fault the runner.
                    var d = descriptor;
                    _backgroundRunner.Run(async () =>
                    {
                        _logger?.LogDebug("Background revalidation starting for stale key '{Key}'.", d.Key);
                        var (succeeded, freshValue) = await SafeInvokeAsync(d).ConfigureAwait(false);
                        if (succeeded)
                        {
                            await _cache.SetAsync(d.Key, freshValue, d.Ttl, CancellationToken.None)
                                        .ConfigureAwait(false);
                            _logger?.LogDebug("Background revalidation complete for key '{Key}'.", d.Key);
                        }
                    }, _logger);
                }
            }
            else
            {
                // No fresh cache entry — start the task via the deduplicator.
                // Use InvokeAsync directly so faults (errors, timeouts) propagate correctly
                // to ApplyCompletedTaskAsync, which classifies them as Error or TimedOut.
                var d = descriptor;
                var sw = Stopwatch.StartNew();
                var task = _deduplicator.GetOrAddAsync(
                    d.Key,
                    () => d.InvokeAsync(CancellationToken.None));

                pending.Add((d, task, sw));
                values[d.Key] = null;
                metadata[d.Key] = TaskMetadata.Loading();

                _logger?.LogDebug("Task started for key '{Key}'.", d.Key);
            }
        }

        // ── Phase 2: All tasks were cached ─────────────────────────────────────
        if (pending.Count == 0)
        {
            _logger?.LogDebug("Aggregation run {CorrelationId} served entirely from cache.", ctx.CorrelationId);
            return new AggregationResult(values, metadata, isComplete: true, ctx);
        }

        // ── Phase 3: Wait for first completion ─────────────────────────────────
        bool callerCancelled = false;
        try
        {
            await Task.WhenAny(pending.Select(p => p.Task))
                      .WaitAsync(cancellationToken)
                      .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogDebug(
                "Aggregation run {CorrelationId} caller cancelled before first result.", ctx.CorrelationId);
            callerCancelled = true;
        }

        // ── Phase 4: Snapshot all completed tasks ───────────────────────────────
        var remaining = new List<(AggregationTaskBase Descriptor, Task<object?> Task)>();

        foreach (var (descriptor, task, sw) in pending)
        {
            if (task.IsCompleted)
            {
                sw.Stop();
                await ApplyCompletedTaskAsync(descriptor, task, sw.Elapsed, values, metadata)
                    .ConfigureAwait(false);
            }
            else
            {
                remaining.Add((descriptor, task));
            }
        }

        // ── Phase 5: Background completion for remaining tasks ──────────────────
        // This runs whether or not the caller cancelled — background tasks MUST run
        // to completion so the cache is populated for the next request.
        if (remaining.Count > 0)
        {
            var capturedCache = _cache;
            var capturedLogger = _logger;
            var capturedRemaining = remaining;

            _backgroundRunner.Run(async () =>
            {
                foreach (var (descriptor, task) in capturedRemaining)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var value = await task.ConfigureAwait(false);
                        sw.Stop();
                        await capturedCache.SetAsync(descriptor.Key, value, descriptor.Ttl, CancellationToken.None)
                                           .ConfigureAwait(false);
                        capturedLogger?.LogDebug(
                            "Background task for key '{Key}' completed in {Elapsed}ms.",
                            descriptor.Key, sw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        capturedLogger?.LogWarning(ex,
                            "Background task for key '{Key}' failed after {Elapsed}ms.",
                            descriptor.Key, sw.ElapsedMilliseconds);
                    }
                }
            }, _logger);
        }

        if (callerCancelled)
        {
            return new AggregationResult(values, metadata, isComplete: false, ctx);
        }

        bool isComplete = remaining.Count == 0;
        _logger?.LogDebug(
            "Aggregation run {CorrelationId} returning snapshot (complete={IsComplete}, background={Background}).",
            ctx.CorrelationId, isComplete, remaining.Count);

        return new AggregationResult(values, metadata, isComplete, ctx);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes the descriptor's factory catching all exceptions.
    /// Used exclusively for stale-while-revalidate background refresh where
    /// failures must not propagate.
    /// </summary>
    private static async Task<(bool Succeeded, object? Value)> SafeInvokeAsync(AggregationTaskBase descriptor)
    {
        try
        {
            var value = await descriptor.InvokeAsync(CancellationToken.None).ConfigureAwait(false);
            return (true, value);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Reads a completed task, classifies its outcome, updates the values/metadata
    /// dictionaries, and writes to the cache on success.
    /// </summary>
    private async Task ApplyCompletedTaskAsync(
        AggregationTaskBase descriptor,
        Task<object?> task,
        TimeSpan elapsed,
        Dictionary<string, object?> values,
        Dictionary<string, TaskMetadata> metadata)
    {
        if (task.IsFaulted)
        {
            var ex = task.Exception?.InnerException ?? task.Exception!;
            _logger?.LogWarning(ex, "Task for key '{Key}' faulted.", descriptor.Key);
            values[descriptor.Key] = null;
            // OperationCanceledException from a timeout CTS is classified as TimedOut.
            metadata[descriptor.Key] = ex is OperationCanceledException
                ? TaskMetadata.TimedOut(elapsed)
                : TaskMetadata.ForError(ex, elapsed);
            return;
        }

        if (task.IsCanceled)
        {
            _logger?.LogWarning("Task for key '{Key}' was cancelled (timeout).", descriptor.Key);
            values[descriptor.Key] = null;
            metadata[descriptor.Key] = TaskMetadata.TimedOut(elapsed);
            return;
        }

        var value = task.Result;
        values[descriptor.Key] = value;
        metadata[descriptor.Key] = TaskMetadata.Completed(elapsed);

        await _cache.SetAsync(descriptor.Key, value, descriptor.Ttl, CancellationToken.None)
                    .ConfigureAwait(false);
    }
}
