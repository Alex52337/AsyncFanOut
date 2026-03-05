using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace AsyncFanOut.Cache;

/// <summary>
/// Cache implementation backed by <see cref="IDistributedCache"/>, compatible with
/// Redis, SQL Server, and other distributed stores.
/// Values are serialised to JSON using <see cref="System.Text.Json"/>.
/// </summary>
/// <remarks>
/// <para>
/// Because <see cref="IAggregationCache.TryGet"/> and <see cref="IAggregationCache.TryGetWithMeta"/>
/// are synchronous, this implementation calls the async distributed-cache APIs via
/// <c>GetAwaiter().GetResult()</c>. This is safe in ASP.NET Core (no synchronisation context)
/// but should be avoided in environments that use a synchronisation context (e.g. legacy WinForms/WPF).
/// </para>
/// <para>
/// Stale-while-revalidate metadata is serialised alongside the value in a JSON wrapper object.
/// </para>
/// </remarks>
public sealed class DistributedAggregationCache : IAggregationCache
{
    private readonly IDistributedCache _cache;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// The fraction of the TTL after which an entry is considered stale.
    /// Defaults to <c>0.8</c>.
    /// </summary>
    public double StaleRatio { get; init; } = 0.8;

    /// <summary>Initialises a new <see cref="DistributedAggregationCache"/>.</summary>
    public DistributedAggregationCache(IDistributedCache cache)
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
        var bytes = _cache.Get(key);
        if (bytes is null) return (false, false, null);

        var wrapper = Deserialize(bytes);
        if (wrapper is null || wrapper.IsExpired) return (false, false, null);

        return (true, wrapper.IsStale, wrapper.Value);
    }

    /// <inheritdoc/>
    public async Task SetAsync(string key, object? value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var ratio = Math.Clamp(StaleRatio, double.Epsilon, 1.0);
        var now = DateTimeOffset.UtcNow;

        var wrapper = new DistributedCacheEntryWrapper
        {
            Value = value,
            StaleAt = now.Add(TimeSpan.FromTicks((long)(ttl.Ticks * ratio))),
            ExpiresAt = now.Add(ttl)
        };

        var bytes = Serialize(wrapper);
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        };

        await _cache.SetAsync(key, bytes, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);

    private static byte[] Serialize(DistributedCacheEntryWrapper wrapper) =>
        JsonSerializer.SerializeToUtf8Bytes(wrapper, _jsonOptions);

    private static DistributedCacheEntryWrapper? Deserialize(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<DistributedCacheEntryWrapper>(bytes, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Internal DTO for JSON serialisation
    private sealed class DistributedCacheEntryWrapper
    {
        public object? Value { get; init; }
        public DateTimeOffset StaleAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }

        public bool IsStale => DateTimeOffset.UtcNow > StaleAt;
        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
    }
}
