using AsyncFanOut.Aggregator;
using AsyncFanOut.Extensions;
using AsyncFanOut.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AsyncFanOut.Tests;

/// <summary>
/// End-to-end integration tests simulating a BFF aggregating multiple downstream services.
/// </summary>
public sealed class IntegrationTests : IDisposable
{
    private readonly ServiceProvider _provider;

    public IntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddAsyncFanOut(opt => opt.StaleRatio = 0.8);
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private ITaskAggregator Aggregator => _provider.GetRequiredService<ITaskAggregator>();

    // ── Simulated downstream services ──────────────────────────────────────────

    private record UserProfile(string Name, string Email);
    private record Order(int Id, string Product);
    private record Recommendation(string Title);

    private static Task<UserProfile> GetProfileAsync(string userId) =>
        Task.FromResult(new UserProfile($"User-{userId}", $"{userId}@example.com"));

    private static async Task<List<Order>> GetOrdersAsync(string userId)
    {
        await Task.Delay(30); // Simulate latency
        return [new(1, "Widget"), new(2, "Gadget")];
    }

    private static async Task<List<Recommendation>> GetRecommendationsAsync(string userId)
    {
        await Task.Delay(80); // Simulate higher latency
        return [new("Book A"), new("Book B")];
    }

    // ── Tests ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DI_registration_resolves_aggregator()
    {
        Assert.NotNull(Aggregator);
    }

    [Fact]
    public async Task First_request_returns_partial_result()
    {
        const string userId = "user-partial";

        var result = await Aggregator.RunAsync(b =>
        {
            b.Add("profile", () => GetProfileAsync(userId), TimeSpan.FromMinutes(5));
            b.Add("orders", () => GetOrdersAsync(userId), TimeSpan.FromMinutes(1));
            b.Add("recommendations", () => GetRecommendationsAsync(userId), TimeSpan.FromMinutes(1));
        });

        // Profile completes immediately so it should be in the result
        var profile = result.Get<UserProfile>("profile");
        Assert.NotNull(profile);
        Assert.Equal($"User-{userId}", profile.Name);

        // Result may not be complete (orders/recommendations still loading)
        // At minimum profile should be Completed state
        Assert.Equal(TaskState.Completed, result.GetMetadata("profile").State);
    }

    [Fact]
    public async Task Second_request_serves_from_cache()
    {
        const string userId = "user-cached";
        int orderCallCount = 0;

        // First request — populates cache
        await Aggregator.RunAsync(b =>
        {
            b.Add("profile", () => GetProfileAsync(userId), TimeSpan.FromMinutes(5));
            b.Add("orders", async () =>
            {
                Interlocked.Increment(ref orderCallCount);
                return await GetOrdersAsync(userId);
            }, TimeSpan.FromMinutes(5));
        });

        // Wait for background tasks to complete
        await Task.Delay(200);

        int orderCallCount2 = 0;

        // Second request — all from cache
        var result2 = await Aggregator.RunAsync(b =>
        {
            b.Add("profile", () => GetProfileAsync(userId), TimeSpan.FromMinutes(5));
            b.Add("orders", async () =>
            {
                Interlocked.Increment(ref orderCallCount2);
                return await GetOrdersAsync(userId);
            }, TimeSpan.FromMinutes(5));
        });

        Assert.True(result2.IsComplete);
        Assert.Equal(TaskState.Cached, result2.GetMetadata("profile").State);
        Assert.Equal(TaskState.Cached, result2.GetMetadata("orders").State);
        Assert.Equal(0, orderCallCount2); // factory not called on cache hit
        Assert.Equal(1, orderCallCount);  // only called once on first request
    }

    [Fact]
    public async Task Error_in_one_service_does_not_affect_others()
    {
        const string userId = "user-error";

        var result = await Aggregator.RunAsync(b =>
        {
            b.Add("profile", () => GetProfileAsync(userId), TimeSpan.FromMinutes(5));
            b.Add<List<Order>>("orders", () => throw new HttpRequestException("Service unavailable"),
                TimeSpan.FromMinutes(1));
        });

        // Profile should still be available
        Assert.NotNull(result.Get<UserProfile>("profile"));

        // Orders slot reflects the error
        var ordersMeta = result.GetMetadata("orders");
        Assert.Equal(TaskState.Error, ordersMeta.State);
        Assert.IsType<HttpRequestException>(ordersMeta.Error);
        Assert.Null(result.Get<List<Order>>("orders"));
    }

    [Fact]
    public async Task Aggregation_context_is_propagated_to_result()
    {
        const string correlationId = "integration-test-456";
        var ctx = new AggregationContext(correlationId);

        var result = await Aggregator.RunAsync(
            b => b.Add("k", () => Task.FromResult(1), TimeSpan.FromMinutes(1)),
            context: ctx);

        Assert.Equal(correlationId, result.Context.CorrelationId);
    }

    [Fact]
    public async Task All_tasks_complete_when_awaited_with_sufficient_delay()
    {
        const string userId = "user-complete";

        // First run starts everything
        await Aggregator.RunAsync(b =>
        {
            b.Add("profile", () => GetProfileAsync(userId), TimeSpan.FromMinutes(5));
            b.Add("orders", () => GetOrdersAsync(userId), TimeSpan.FromMinutes(5));
            b.Add("recommendations", () => GetRecommendationsAsync(userId), TimeSpan.FromMinutes(5));
        });

        // Wait enough time for slowest task (recommendations ~80ms)
        await Task.Delay(300);

        // Run again — everything should be cached
        var result = await Aggregator.RunAsync(b =>
        {
            b.Add("profile", () => GetProfileAsync(userId), TimeSpan.FromMinutes(5));
            b.Add("orders", () => GetOrdersAsync(userId), TimeSpan.FromMinutes(5));
            b.Add("recommendations", () => GetRecommendationsAsync(userId), TimeSpan.FromMinutes(5));
        });

        Assert.True(result.IsComplete);
        Assert.All(result.Keys, k => Assert.Equal(TaskState.Cached, result.GetMetadata(k).State));

        var recs = result.Get<List<Recommendation>>("recommendations");
        Assert.NotNull(recs);
        Assert.Equal(2, recs.Count);
    }
}
