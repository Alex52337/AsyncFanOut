using AsyncFanOut.Extensions;
using AsyncFanOut.Sample.Controllers;

var builder = WebApplication.CreateBuilder(args);

// ── Services ─────────────────────────────────────────────────────────────────

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Register AsyncFanOut with in-memory cache (default).
// For Redis, replace with: services.AddStackExchangeRedisCache(...)
//                          services.AddAsyncFanOutWithDistributedCache(...)
builder.Services.AddAsyncFanOut(options =>
{
    // Entry becomes stale at 80% of TTL — triggers background refresh while
    // serving the stale value to the caller (stale-while-revalidate).
    options.StaleRatio = 0.8;
});

// Register fake downstream service implementations.
// Replace with your real HttpClient-based service clients in production.
builder.Services.AddSingleton<IProfileService, FakeProfileService>();
builder.Services.AddSingleton<IOrderService, FakeOrderService>();
builder.Services.AddSingleton<IRecommendationService, FakeRecommendationService>();
builder.Services.AddSingleton<INotificationService, FakeNotificationService>();

// ── App Pipeline ──────────────────────────────────────────────────────────────

var app = builder.Build();

app.UseHttpsRedirection();
app.MapControllers();

// Quick status endpoint
app.MapGet("/", () => new
{
    name = "AsyncFanOut Sample BFF",
    description = "Hit GET /api/dashboard/{userId} twice to observe cache behaviour.",
    exampleUrl = "/api/dashboard/user123"
});

app.Run();
