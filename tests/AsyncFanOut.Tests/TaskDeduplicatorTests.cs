using AsyncFanOut.Execution;
using Xunit;

namespace AsyncFanOut.Tests;

public sealed class TaskDeduplicatorTests
{
    [Fact]
    public async Task Single_call_invokes_factory_once()
    {
        var dedup = new TaskDeduplicator();
        int callCount = 0;

        var result = await dedup.GetOrAddAsync("key", async () =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(1);
            return (object?)"value";
        });

        Assert.Equal(1, callCount);
        Assert.Equal("value", result);
    }

    [Fact]
    public async Task Concurrent_calls_same_key_invoke_factory_once()
    {
        var dedup = new TaskDeduplicator();
        int callCount = 0;
        var barrier = new TaskCompletionSource();

        Func<Task<object?>> factory = async () =>
        {
            Interlocked.Increment(ref callCount);
            await barrier.Task;
            return (object?)"shared";
        };

        // Start many concurrent tasks for the same key.
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => dedup.GetOrAddAsync("key", factory))
            .ToList();

        // Let all tasks start before releasing the barrier.
        await Task.Delay(50);
        barrier.SetResult();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, callCount);
        Assert.All(results, r => Assert.Equal("shared", r));
    }

    [Fact]
    public async Task Different_keys_invoke_factory_independently()
    {
        var dedup = new TaskDeduplicator();
        int callCount = 0;

        await dedup.GetOrAddAsync("a", async () => { Interlocked.Increment(ref callCount); await Task.Yield(); return (object?)"a"; });
        await dedup.GetOrAddAsync("b", async () => { Interlocked.Increment(ref callCount); await Task.Yield(); return (object?)"b"; });

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task After_completion_next_call_re_invokes_factory()
    {
        var dedup = new TaskDeduplicator();
        int callCount = 0;

        await dedup.GetOrAddAsync("key", async () =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Yield();
            return (object?)"first";
        });

        // First call completed and removed its entry. A second call should invoke again.
        await dedup.GetOrAddAsync("key", async () =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Yield();
            return (object?)"second";
        });

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task Faulted_task_is_removed_and_next_call_retries()
    {
        var dedup = new TaskDeduplicator();
        int callCount = 0;

        // First call throws.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dedup.GetOrAddAsync("key", async () =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }));

        // Second call should succeed — the faulted entry was removed.
        var result = await dedup.GetOrAddAsync("key", async () =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Yield();
            return (object?)"recovered";
        });

        Assert.Equal(2, callCount);
        Assert.Equal("recovered", result);
    }
}
