using Microsoft.Extensions.Caching.Memory;

namespace AsyncFanOut.Cache;

/// <summary>
/// In-process cache backed by <see cref="IMemoryCache"/>.
/// Supports stale-while-revalidate semantics via a configurable <see cref="StaleRatio"/>.
/// Thread-safe; <see cref="IMemoryCache"/> provides its own internal locking.
/// </summary>
public sealed class InMemoryAggregationCache : IAggregationCache
{
    private readonly IMemoryCache _cache;

    /// <summary>
    /// The fraction of the TTL after which an entry is considered stale.
    /// For example, a value of <c>0.8</c> with a 5-minute TTL makes entries stale after 4 minutes
    /// but still usable until 5 minutes, triggering a background refresh in the final minute.
    /// Must be in the range (0, 1].
    /// </summary>
    public double StaleRatio { get; init; } = 0.8;

    /// <summary>
    /// Initialises a new <see cref="InMemoryAggregationCache"/>.
    /// </summary>
    /// <param name="cache">The underlying memory cache instance.</param>
    public InMemoryAggregationCache(IMemoryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc/>
    public bool TryGet(string key, out object? value)
    {
        var (found, isStale, val) = TryGetWithMeta(key);
        if (found && !isStale)
        {
            value = val;
            return true;
        }
        value = null;
        return false;
    }

    /// <inheritdoc/>
    public (bool found, bool isStale, object? value) TryGetWithMeta(string key)
    {
        if (_cache.TryGetValue(key, out CacheEntryMeta? meta) && meta is not null && !meta.IsExpired)
            return (true, meta.IsStale, meta.Value);

        return (false, false, null);
    }

    /// <inheritdoc/>
    public Task SetAsync(string key, object? value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var ratio = Math.Clamp(StaleRatio, double.Epsilon, 1.0);
        var now = DateTimeOffset.UtcNow;
        var meta = new CacheEntryMeta
        {
            Value = value,
            StaleAt = now.Add(TimeSpan.FromTicks((long)(ttl.Ticks * ratio))),
            ExpiresAt = now.Add(ttl)
        };

        _cache.Set(key, meta, absoluteExpirationRelativeToNow: ttl);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        return Task.CompletedTask;
    }
}
