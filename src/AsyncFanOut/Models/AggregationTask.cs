namespace AsyncFanOut.Models;

/// <summary>
/// Non-generic base class for task descriptors. Allows the aggregator to work
/// with a heterogeneous list of typed tasks without knowing <c>T</c> at each call site.
/// </summary>
public abstract class AggregationTaskBase
{
    /// <summary>The unique cache key for this task.</summary>
    public string Key { get; }

    /// <summary>How long a successfully retrieved value should remain in the cache.</summary>
    public TimeSpan Ttl { get; }

    /// <summary>
    /// Optional per-task timeout. When exceeded, the task slot receives
    /// <see cref="TaskState.TimedOut"/> state.
    /// </summary>
    public TimeSpan? Timeout { get; }

    /// <summary>
    /// When <see langword="true"/>, the aggregator will not return until this task
    /// has completed (successfully, faulted, or timed out). Other tasks may still
    /// be in-flight and will complete in the background.
    /// </summary>
    public bool WaitForCompletion { get; init; }

    /// <summary>Initialises a new <see cref="AggregationTaskBase"/>.</summary>
    protected AggregationTaskBase(string key, TimeSpan ttl, TimeSpan? timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be a positive duration.");

        Key = key;
        Ttl = ttl;
        Timeout = timeout;
    }

    /// <summary>
    /// Invokes the factory, applying the configured timeout and policy wrapper.
    /// Returns a boxed result on success, or propagates exceptions on failure.
    /// </summary>
    internal abstract Task<object?> InvokeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A typed task descriptor that wraps a <see cref="Func{TResult}"/> factory producing
/// <typeparamref name="T"/> values.
/// </summary>
/// <typeparam name="T">The type of value produced by this task.</typeparam>
public sealed class AggregationTask<T> : AggregationTaskBase
{
    private readonly Func<Task<T>> _factory;

    /// <summary>
    /// An optional policy wrapper applied around the factory invocation.
    /// Use this for Polly retry / circuit-breaker policies without adding a hard dependency.
    /// </summary>
    /// <example>
    /// <code>
    /// policyWrapper: inner => retryPolicy.ExecuteAsync(inner)
    /// </code>
    /// </example>
    public Func<Func<Task<T>>, Task<T>>? PolicyWrapper { get; init; }

    /// <summary>Initialises a new <see cref="AggregationTask{T}"/>.</summary>
    /// <param name="key">Unique cache key.</param>
    /// <param name="factory">Async factory that produces the value.</param>
    /// <param name="ttl">Cache time-to-live for a successful result.</param>
    /// <param name="timeout">Optional per-task timeout.</param>
    public AggregationTask(
        string key,
        Func<Task<T>> factory,
        TimeSpan ttl,
        TimeSpan? timeout = null)
        : base(key, ttl, timeout)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    internal override async Task<object?> InvokeAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linkedCts = null;

        try
        {
            if (Timeout.HasValue)
            {
                timeoutCts = new CancellationTokenSource(Timeout.Value);
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutCts.Token);
            }

            var effectiveCt = linkedCts?.Token ?? cancellationToken;

            T result = PolicyWrapper is not null
                ? await PolicyWrapper(_factory).ConfigureAwait(false)
                : await _factory().ConfigureAwait(false);

            // Check after the call returns so we correctly classify timeout vs success.
            effectiveCt.ThrowIfCancellationRequested();

            return result;
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }
}
