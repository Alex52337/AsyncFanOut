using AsyncFanOut.Aggregator;
using AsyncFanOut.Cache;
using AsyncFanOut.Execution;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AsyncFanOut.Extensions;

/// <summary>
/// Extension methods for registering AsyncFanOut services with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITaskAggregator"/> and supporting services using the
    /// in-process <see cref="IMemoryCache"/> as the backing store.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="IMemoryCache"/> if it has not already been added.
    /// All AsyncFanOut services are registered as singletons.
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional delegate to customise <see cref="AsyncFanOutOptions"/>.</param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddAsyncFanOut(
        this IServiceCollection services,
        Action<AsyncFanOutOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AsyncFanOutOptions();
        configure?.Invoke(options);

        services.AddMemoryCache();

        services.TryAddSingleton<IAggregationCache>(sp =>
            new InMemoryAggregationCache(sp.GetRequiredService<IMemoryCache>())
            {
                StaleRatio = options.StaleRatio
            });

        RegisterSharedServices(services);
        return services;
    }

    /// <summary>
    /// Registers <see cref="ITaskAggregator"/> and supporting services using an
    /// <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> as the backing store.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for registering a concrete <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
    /// implementation (e.g. Redis via <c>AddStackExchangeRedisCache</c>) before or after calling this method.
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional delegate to customise <see cref="AsyncFanOutOptions"/>.</param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddAsyncFanOutWithDistributedCache(
        this IServiceCollection services,
        Action<AsyncFanOutOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AsyncFanOutOptions();
        configure?.Invoke(options);

        services.TryAddSingleton<IAggregationCache>(sp =>
        {
            var distributed = sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
            return new DistributedAggregationCache(distributed) { StaleRatio = options.StaleRatio };
        });

        RegisterSharedServices(services);
        return services;
    }

    private static void RegisterSharedServices(IServiceCollection services)
    {
        services.TryAddSingleton<TaskDeduplicator>();
        services.TryAddSingleton<IBackgroundTaskRunner, BackgroundTaskRunner>();
        services.TryAddSingleton<ITaskAggregator, TaskAggregator>();
    }
}

/// <summary>
/// Configuration options for the AsyncFanOut library.
/// </summary>
public sealed class AsyncFanOutOptions
{
    /// <summary>
    /// The fraction of each entry's TTL after which the entry is considered stale.
    /// When an entry is stale, the cached value is returned immediately while a background
    /// refresh is triggered concurrently (stale-while-revalidate pattern).
    /// Must be in the range (0, 1]. Defaults to <c>0.8</c>.
    /// </summary>
    public double StaleRatio { get; set; } = 0.8;
}
