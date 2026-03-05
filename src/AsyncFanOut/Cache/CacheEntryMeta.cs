namespace AsyncFanOut.Cache;

/// <summary>
/// Internal wrapper stored in the backing cache store.
/// Tracks staleness independently of the store's own expiry TTL,
/// enabling stale-while-revalidate semantics.
/// </summary>
internal sealed class CacheEntryMeta
{
    /// <summary>The cached payload (boxed).</summary>
    public required object? Value { get; init; }

    /// <summary>
    /// After this point in time the entry is considered stale.
    /// A stale entry should be returned immediately but a background refresh
    /// should be triggered concurrently.
    /// </summary>
    public required DateTimeOffset StaleAt { get; init; }

    /// <summary>
    /// After this point in time the entry must not be used at all.
    /// The backing store is told to expire at this time so entries are
    /// automatically evicted.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public bool IsStale => DateTimeOffset.UtcNow > StaleAt;
    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
}
