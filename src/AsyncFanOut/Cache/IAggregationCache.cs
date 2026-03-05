namespace AsyncFanOut.Cache;

/// <summary>
/// Abstraction over the cache backing store used by <see cref="Aggregator.ITaskAggregator"/>.
/// All implementations must be thread-safe.
/// </summary>
public interface IAggregationCache
{
    /// <summary>
    /// Attempts to retrieve a non-expired, non-stale cached value.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">
    /// When this method returns <see langword="true"/>, contains the cached value (which may itself be
    /// <see langword="null"/>). Undefined when the method returns <see langword="false"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a fresh (non-stale, non-expired) entry was found;
    /// <see langword="false"/> if the key is absent, expired, or stale.
    /// </returns>
    bool TryGet(string key, out object? value);

    /// <summary>
    /// Retrieves a cached value along with staleness metadata.
    /// Use this overload to implement stale-while-revalidate: when
    /// <c>isStale</c> is <see langword="true"/>, return the value immediately
    /// and schedule a background refresh.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>
    /// A tuple of:
    /// <list type="bullet">
    ///   <item><c>found</c> — <see langword="true"/> if a non-expired entry exists.</item>
    ///   <item><c>isStale</c> — <see langword="true"/> if the entry is within its stale window.</item>
    ///   <item><c>value</c> — the cached payload when <c>found</c> is <see langword="true"/>.</item>
    /// </list>
    /// </returns>
    (bool found, bool isStale, object? value) TryGetWithMeta(string key);

    /// <summary>
    /// Stores or replaces a cache entry with the specified TTL.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache (may be <see langword="null"/>).</param>
    /// <param name="ttl">How long the entry should remain in the cache before expiring.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(string key, object? value, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a cache entry if it exists.
    /// </summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
