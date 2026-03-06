using AsyncFanOut.Aggregator;
using AsyncFanOut.Models;
using Microsoft.AspNetCore.Mvc;

namespace AsyncFanOut.Sample.Controllers;

// ── Domain Models ────────────────────────────────────────────────────────────

public record UserProfile(string UserId, string Name, string Email, string AvatarUrl);
public record Order(int Id, string Product, decimal Amount, DateTimeOffset PlacedAt);
public record Recommendation(string Title, string Category, double Score);
public record Notification(int Id, string Message, bool IsRead);

// ── Fake downstream services ─────────────────────────────────────────────────

public interface IProfileService { Task<UserProfile> GetProfileAsync(string userId); }
public interface IOrderService { Task<List<Order>> GetOrdersAsync(string userId); }
public interface IRecommendationService { Task<List<Recommendation>> GetAsync(string userId); }
public interface INotificationService { Task<List<Notification>> GetUnreadAsync(string userId); }

public sealed class FakeProfileService : IProfileService
{
    public Task<UserProfile> GetProfileAsync(string userId) =>
        Task.FromResult(new UserProfile(userId, $"User {userId}", $"{userId}@example.com",
            $"https://avatars.example.com/{userId}"));
}

public sealed class FakeOrderService : IOrderService
{
    public async Task<List<Order>> GetOrdersAsync(string userId)
    {
        await Task.Delay(30); // Simulate 30ms latency
        return
        [
            new(1, "Widget Pro", 49.99m, DateTimeOffset.UtcNow.AddDays(-5)),
            new(2, "Gadget Ultra", 129.99m, DateTimeOffset.UtcNow.AddDays(-2))
        ];
    }
}

public sealed class FakeRecommendationService : IRecommendationService
{
    public async Task<List<Recommendation>> GetAsync(string userId)
    {
        await Task.Delay(10000); // Simulate 80ms latency
        return
        [
            new("Clean Code", "Books", 0.95),
            new("The Pragmatic Programmer", "Books", 0.92),
            new("Designing Data-Intensive Applications", "Books", 0.89)
        ];
    }
}

public sealed class FakeNotificationService : INotificationService
{
    public async Task<List<Notification>> GetUnreadAsync(string userId)
    {
        await Task.Delay(20); // Simulate 20ms latency
        return
        [
            new(1, "Your order has shipped!", false),
            new(2, "New recommendations available", false)
        ];
    }
}

// ── BFF Controller ───────────────────────────────────────────────────────────

/// <summary>
/// A BFF (Backend-For-Frontend) dashboard endpoint that aggregates data from
/// multiple microservices using AsyncFanOut.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class DashboardController : ControllerBase
{
    private readonly ITaskAggregator _aggregator;
    private readonly IProfileService _profileService;
    private readonly IOrderService _orderService;
    private readonly IRecommendationService _recommendationService;
    private readonly INotificationService _notificationService;

    public DashboardController(
        ITaskAggregator aggregator,
        IProfileService profileService,
        IOrderService orderService,
        IRecommendationService recommendationService,
        INotificationService notificationService)
    {
        _aggregator = aggregator;
        _profileService = profileService;
        _orderService = orderService;
        _recommendationService = recommendationService;
        _notificationService = notificationService;
    }

    /// <summary>
    /// Returns a partial dashboard response as soon as the first upstream call completes.
    /// Remaining services continue in the background and populate the cache.
    /// Subsequent calls return instantly from cache.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    [HttpGet("{userId}")]
    public async Task<IActionResult> GetDashboard(string userId)
    {
        var result = await _aggregator.RunAsync(builder =>
        {
            builder.Add(
                key: $"profile:{userId}",
                task: () => _profileService.GetProfileAsync(userId),
                ttl: TimeSpan.FromMinutes(5));

            builder.Add(
                key: $"orders:{userId}",
                task: () => _orderService.GetOrdersAsync(userId),
                ttl: TimeSpan.FromMinutes(1));

            builder.Add(
                key: $"recommendations:{userId}",
                task: () => _recommendationService.GetAsync(userId),
                ttl: TimeSpan.FromSeconds(30));

            builder.Add(
                key: $"notifications:{userId}",
                task: () => _notificationService.GetUnreadAsync(userId),
                ttl: TimeSpan.FromSeconds(10));
        },
        context: new AggregationContext(HttpContext.TraceIdentifier),
        cancellationToken: HttpContext.RequestAborted);

        return Ok(new
        {
            correlationId = result.Context.CorrelationId,
            isComplete = result.IsComplete,
            profile = result.Get<UserProfile>($"profile:{userId}"),
            orders = result.Get<List<Order>>($"orders:{userId}"),
            recommendations = result.Get<List<Recommendation>>($"recommendations:{userId}"),
            notifications = result.Get<List<Notification>>($"notifications:{userId}"),
            meta = result.Keys.ToDictionary(
                k => k,
                k => new
                {
                    state = result.GetMetadata(k).State.ToString(),
                    isFromCache = result.GetMetadata(k).IsFromCache,
                    durationMs = result.GetMetadata(k).Duration?.TotalMilliseconds
                })
        });
    }
}
