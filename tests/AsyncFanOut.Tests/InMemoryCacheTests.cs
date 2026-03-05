using AsyncFanOut.Cache;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace AsyncFanOut.Tests;

public sealed class InMemoryCacheTests : IDisposable
{
    private readonly IMemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions());
    private InMemoryAggregationCache CreateCache(double staleRatio = 0.8) =>
        new(_memoryCache) { StaleRatio = staleRatio };

    public void Dispose() => _memoryCache.Dispose();

    [Fact]
    public async Task SetAsync_then_TryGet_returns_value()
    {
        var cache = CreateCache();
        await cache.SetAsync("k", "hello", TimeSpan.FromMinutes(5));

        var found = cache.TryGet("k", out var value);

        Assert.True(found);
        Assert.Equal("hello", value);
    }

    [Fact]
    public async Task TryGet_returns_false_for_unknown_key()
    {
        var cache = CreateCache();
        await cache.SetAsync("other", 42, TimeSpan.FromMinutes(1));

        var found = cache.TryGet("missing", out _);

        Assert.False(found);
    }

    [Fact]
    public async Task TryGet_returns_false_after_entry_removed()
    {
        var cache = CreateCache();
        await cache.SetAsync("k", "val", TimeSpan.FromMinutes(5));
        await cache.RemoveAsync("k");

        var found = cache.TryGet("k", out _);

        Assert.False(found);
    }

    [Fact]
    public async Task TryGet_returns_false_for_stale_entry()
    {
        // StaleRatio = 0 means entry is stale immediately.
        var cache = CreateCache(staleRatio: 0.0001);
        await cache.SetAsync("k", "stale", TimeSpan.FromSeconds(10));

        // Tiny delay to ensure stale threshold is crossed
        await Task.Delay(5);

        var found = cache.TryGet("k", out _);
        Assert.False(found); // TryGet hides stale entries
    }

    [Fact]
    public async Task TryGetWithMeta_returns_stale_value_with_isStale_true()
    {
        var cache = CreateCache(staleRatio: 0.0001);
        await cache.SetAsync("k", "stale-value", TimeSpan.FromSeconds(10));

        await Task.Delay(5);

        var (found, isStale, value) = cache.TryGetWithMeta("k");

        Assert.True(found);
        Assert.True(isStale);
        Assert.Equal("stale-value", value);
    }

    [Fact]
    public async Task TryGetWithMeta_returns_not_stale_for_fresh_entry()
    {
        var cache = CreateCache(staleRatio: 1.0);
        await cache.SetAsync("k", "fresh", TimeSpan.FromMinutes(5));

        var (found, isStale, value) = cache.TryGetWithMeta("k");

        Assert.True(found);
        Assert.False(isStale);
        Assert.Equal("fresh", value);
    }

    [Fact]
    public async Task SetAsync_overwrites_existing_entry()
    {
        var cache = CreateCache();
        await cache.SetAsync("k", "first", TimeSpan.FromMinutes(5));
        await cache.SetAsync("k", "second", TimeSpan.FromMinutes(5));

        cache.TryGet("k", out var value);

        Assert.Equal("second", value);
    }

    [Fact]
    public async Task SetAsync_accepts_null_value()
    {
        var cache = CreateCache();
        await cache.SetAsync("k", (object?)null, TimeSpan.FromMinutes(1));

        var found = cache.TryGet("k", out var value);

        // null values are stored — but TryGet returns false for null entries
        // because the value itself is null (indistinguishable from a miss for callers).
        // This is by design; callers should use TryGetWithMeta to distinguish.
        Assert.True(found);
        Assert.Null(value);
    }
}
