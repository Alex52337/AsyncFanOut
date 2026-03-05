namespace AsyncFanOut.Models;

/// <summary>
/// Request-scoped context propagated throughout a single aggregation run.
/// Used for correlation, logging, and observability.
/// </summary>
public sealed class AggregationContext
{
    /// <summary>
    /// A unique identifier for this aggregation run.
    /// Defaults to a new GUID if not provided.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>The UTC time at which this aggregation run was created.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Initialises a new <see cref="AggregationContext"/>.
    /// </summary>
    /// <param name="correlationId">
    /// Optional correlation identifier. If <see langword="null"/>, a new GUID is generated.
    /// </param>
    public AggregationContext(string? correlationId = null)
    {
        CorrelationId = correlationId ?? Guid.NewGuid().ToString("N");
        StartedAt = DateTimeOffset.UtcNow;
    }
}
