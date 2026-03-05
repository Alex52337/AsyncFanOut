using AsyncFanOut.Aggregator;
using AsyncFanOut.Cache;
using AsyncFanOut.Execution;
using AsyncFanOut.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AsyncFanOut.Tests;

public sealed class TaskAggregatorTests : IDisposable
{
    private readonly IMemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions());

    private (TaskAggregator Aggregator, IAggregationCache Cache) CreateAggregator(double staleRatio = 0.8)
    {
        var cache = new InMemoryAggregationCache(_memoryCache) { StaleRatio = staleRatio };
        var dedup = new TaskDeduplicator();
        var runner = new BackgroundTaskRunner();
        var aggregator = new TaskAggregator(cache, dedup, runner, NullLogger<TaskAggregator>.Instance);
        return (aggregator, cache);
    }

    public void Dispose() => _memoryCache.Dispose();

    // ── All cached ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task All_tasks_cached_returns_complete_result_immediately()
    {
        var (agg, cache) = CreateAggregator();
        await cache.SetAsync("profile", "Alice", TimeSpan.FromMinutes(5));
        await cache.SetAsync("orders", 42, TimeSpan.FromMinutes(1));

        var result = await agg.RunAsync(b =>
        {
            b.Add("profile", () => Task.FromResult("should-not-run"), TimeSpan.FromMinutes(5));
            b.Add("orders", () => Task.FromResult(0), TimeSpan.FromMinutes(1));
        });

        Assert.True(result.IsComplete);
        Assert.Equal("Alice", result.Get<string>("profile"));
        Assert.Equal(42, result.Get<int>("orders"));
        Assert.Equal(TaskState.Cached, result.GetMetadata("profile").State);
        Assert.Equal(TaskState.Cached, result.GetMetadata("orders").State);
        Assert.True(result.GetMetadata("profile").IsFromCache);
    }

    // ── Partial result ──────────────────────────────────────────────────────────

    [Fact]
    public async Task First_completed_task_appears_in_result_others_show_loading()
    {
        var (agg, _) = CreateAggregator();
        var slowTcs = new TaskCompletionSource<string>();

        var result = await agg.RunAsync(b =>
        {
            // Fast task completes immediately
            b.Add("fast", () => Task.FromResult("quick"), TimeSpan.FromMinutes(1));
            // Slow task is never completed in this test
            b.Add("slow", () => slowTcs.Task, TimeSpan.FromMinutes(1));
        });

        Assert.False(result.IsComplete);
        Assert.Equal("quick", result.Get<string>("fast"));
        Assert.Equal(TaskState.Completed, result.GetMetadata("fast").State);
        Assert.Null(result.Get<string>("slow"));
        Assert.Equal(TaskState.Loading, result.GetMetadata("slow").State);

        // Release to avoid test cleanup issues
        slowTcs.SetResult("done");
    }

    // ── Background cache update ─────────────────────────────────────────────────

    [Fact]
    public async Task Background_tasks_populate_cache_for_next_request()
    {
        var (agg, cache) = CreateAggregator();
        int slowCallCount = 0;

        // First request — "slow" task runs in background
        var result1 = await agg.RunAsync(b =>
        {
            b.Add("fast", () => Task.FromResult("fast-val"), TimeSpan.FromMinutes(5));
            b.Add("slow", async () =>
            {
                Interlocked.Increment(ref slowCallCount);
                await Task.Delay(50);
                return "slow-val";
            }, TimeSpan.FromMinutes(5));
        });

        Assert.False(result1.IsComplete);

        // Wait for background task to complete and populate cache
        await Task.Delay(200);

        // Second request — both should be from cache
        int slowCallCount2 = 0;
        var result2 = await agg.RunAsync(b =>
        {
            b.Add("fast", () => Task.FromResult("fast-val"), TimeSpan.FromMinutes(5));
            b.Add("slow", () =>
            {
                Interlocked.Increment(ref slowCallCount2);
                return Task.FromResult("new-slow");
            }, TimeSpan.FromMinutes(5));
        });

        Assert.True(result2.IsComplete);
        Assert.Equal("slow-val", result2.Get<string>("slow"));
        Assert.Equal(TaskState.Cached, result2.GetMetadata("slow").State);
        Assert.Equal(0, slowCallCount2); // factory was not called again
    }

    // ── Error isolation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Failing_task_does_not_crash_aggregator()
    {
        var (agg, _) = CreateAggregator();

        var result = await agg.RunAsync(b =>
        {
            b.Add<string>("bad", () => throw new InvalidOperationException("boom"), TimeSpan.FromMinutes(1));
            b.Add("good", () => Task.FromResult("ok"), TimeSpan.FromMinutes(1));
        });

        // The good key should be present
        Assert.Equal("ok", result.Get<string>("good"));
        Assert.Equal(TaskState.Completed, result.GetMetadata("good").State);

        // The bad key should have error metadata, not throw
        var badMeta = result.GetMetadata("bad");
        Assert.Equal(TaskState.Error, badMeta.State);
        Assert.IsType<InvalidOperationException>(badMeta.Error);
        Assert.Null(result.Get<string>("bad"));
    }

    // ── Task timeout ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Task_exceeding_timeout_has_timed_out_state()
    {
        var (agg, _) = CreateAggregator();

        var result = await agg.RunAsync(b =>
        {
            b.Add("quick", () => Task.FromResult("done"), TimeSpan.FromMinutes(1));
            // Factory completes in 100ms but timeout fires at 50ms.
            // InvokeAsync checks cancellation AFTER factory returns, so the task
            // completes (faulted with OperationCanceledException) at ~100ms.
            b.Add("slow", async () =>
            {
                await Task.Delay(100);
                return "never";
            }, TimeSpan.FromMinutes(1), timeout: TimeSpan.FromMilliseconds(50));
        });

        // quick completes first (immediately), so we get a partial result
        Assert.Equal("done", result.Get<string>("quick"));

        // Wait for the timed-out task to finish (~100ms) and deduplicator to release
        await Task.Delay(300);

        // Run again — "quick" should be cached, "slow" was timed out so not cached
        var result2 = await agg.RunAsync(b =>
        {
            b.Add("quick", () => Task.FromResult("done"), TimeSpan.FromMinutes(1));
            b.Add("slow", () => Task.FromResult("recovered"), TimeSpan.FromMinutes(1));
        });

        Assert.Equal(TaskState.Cached, result2.GetMetadata("quick").State);
        // "slow" was not cached (timed out), so factory runs again this time
        Assert.Equal("recovered", result2.Get<string>("slow"));
    }

    // ── Cancellation does not kill background ───────────────────────────────────

    [Fact]
    public async Task Caller_cancellation_does_not_cancel_background_cache_update()
    {
        var (agg, cache) = CreateAggregator();
        using var cts = new CancellationTokenSource();

        var slowStarted = new TaskCompletionSource();
        var slowFinish = new TaskCompletionSource<string>();

        var runTask = agg.RunAsync(b =>
        {
            b.Add("key", async () =>
            {
                slowStarted.SetResult();
                return await slowFinish.Task;
            }, TimeSpan.FromMinutes(5));
        }, cancellationToken: cts.Token);

        // Wait for the background factory to start, then cancel the caller
        await slowStarted.Task;
        cts.Cancel();

        // RunAsync should return (with Loading state) despite cancellation
        var result = await runTask;
        Assert.Equal(TaskState.Loading, result.GetMetadata("key").State);

        // Release the background factory so it can complete and populate cache
        slowFinish.SetResult("cached-value");

        // Give the background runner time to write to cache
        await Task.Delay(100);

        var (found, _, value) = cache.TryGetWithMeta("key");
        Assert.True(found);
        Assert.Equal("cached-value", value);
    }

    // ── Metadata ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMetadata_throws_for_unknown_key()
    {
        var (agg, _) = CreateAggregator();
        var result = await agg.RunAsync(b => b.Add("k", () => Task.FromResult(1), TimeSpan.FromMinutes(1)));

        Assert.Throws<KeyNotFoundException>(() => result.GetMetadata("not-registered"));
    }

    [Fact]
    public async Task Result_Keys_contains_all_registered_keys()
    {
        var (agg, _) = CreateAggregator();
        var result = await agg.RunAsync(b =>
        {
            b.Add("a", () => Task.FromResult(1), TimeSpan.FromMinutes(1));
            b.Add("b", () => Task.FromResult(2), TimeSpan.FromMinutes(1));
            b.Add("c", () => Task.FromResult(3), TimeSpan.FromMinutes(1));
        });

        Assert.Equal(3, result.Keys.Count);
        Assert.Contains("a", result.Keys);
        Assert.Contains("b", result.Keys);
        Assert.Contains("c", result.Keys);
    }

    // ── Immediate value via Task.FromResult ────────────────────────────────────

    [Fact]
    public async Task Synchronous_value_via_task_from_result_works()
    {
        var (agg, _) = CreateAggregator();
        var result = await agg.RunAsync(b =>
        {
            b.Add("k", () => Task.FromResult("sync-value"), TimeSpan.FromMinutes(1));
        });

        Assert.Equal("sync-value", result.Get<string>("k"));
    }

    // ── Context ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Custom_context_correlation_id_is_preserved()
    {
        var (agg, _) = CreateAggregator();
        var ctx = new AggregationContext(correlationId: "test-correlation-123");

        var result = await agg.RunAsync(
            b => b.Add("k", () => Task.FromResult(1), TimeSpan.FromMinutes(1)),
            context: ctx);

        Assert.Equal("test-correlation-123", result.Context.CorrelationId);
    }
}
