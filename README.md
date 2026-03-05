# AsyncFanOut

[![NuGet](https://img.shields.io/nuget/v/AsyncFanOut.svg)](https://www.nuget.org/packages/AsyncFanOut)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)

**Task Aggregator for BFF (Backend-For-Frontend) architectures.**

AsyncFanOut orchestrates parallel downstream microservice calls and improves perceived latency for frontend applications by returning a partial response as soon as the first result is available, continuing remaining tasks in the background, and serving subsequent requests instantly from cache.

---

## Why AsyncFanOut?

A typical BFF endpoint needs data from 4–6 downstream services. The naive approach awaits each sequentially — the user waits for the slowest service. Firing everything in parallel and awaiting `Task.WhenAll` is better, but the user still waits for the slowest call.

AsyncFanOut solves this with **progressive hydration**:

| Request   | Behaviour                                                                                                               |
| --------- | ----------------------------------------------------------------------------------------------------------------------- |
| Request 1 | All tasks start concurrently. First result returned immediately. Remaining complete in background, populating the cache. |
| Request 2 | Cached values returned instantly. No downstream calls for warm keys.                                                    |

```text
Request 1 timeline:
  t=0ms   ──── profile (5ms) ──────────────────────► return partial result
  t=0ms   ──── orders (30ms) ──────────────────────────────────────► cache
  t=0ms   ──── recommendations (80ms) ──────────────────────────────────────► cache
                │
                └─ Returns at t≈5ms with profile populated
                   (orders & recommendations show Loading state)

Request 2 timeline:
  t=0ms   ──── cache hit ──► return complete result in <1ms
```

---

## Installation

```shell
dotnet add package AsyncFanOut
```

---

## Quick Start

### 1. Register services

```csharp
// Program.cs
builder.Services.AddAsyncFanOut(options =>
{
    // Entries become stale at 80% of TTL, triggering background refresh
    // while serving the stale value (stale-while-revalidate).
    options.StaleRatio = 0.8;
});
```

### 2. Use in a BFF controller

```csharp
[ApiController, Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly ITaskAggregator _aggregator;

    public DashboardController(ITaskAggregator aggregator) => _aggregator = aggregator;

    [HttpGet("{userId}")]
    public async Task<IActionResult> GetDashboard(string userId)
    {
        var result = await _aggregator.RunAsync(builder =>
        {
            // ⚠️ Keys are global — always include the scoping dimension (userId, tenantId, etc.)
            // Using key: "profile" would return user A's data for user B.
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
        },
        cancellationToken: HttpContext.RequestAborted);

        return Ok(new
        {
            profile         = result.Get<UserProfile>($"profile:{userId}"),
            orders          = result.Get<List<Order>>($"orders:{userId}"),
            recommendations = result.Get<List<Recommendation>>($"recommendations:{userId}"),
            isComplete      = result.IsComplete,
            meta            = result.Keys.ToDictionary(k => k, k => result.GetMetadata(k).State)
        });
    }
}
```

**First response** (fast — returned when profile completes ~5ms):

```json
{
  "profile": { "name": "Alice", "email": "alice@example.com" },
  "orders": null,
  "recommendations": null,
  "isComplete": false,
  "meta": {
    "profile:alice": "Completed",
    "orders:alice": "Loading",
    "recommendations:alice": "Loading"
  }
}
```

**Second response** (instant from cache):

```json
{
  "profile": { "name": "Alice", "email": "alice@example.com" },
  "orders": [{ "id": 1, "product": "Widget" }],
  "recommendations": [{ "title": "Clean Code" }],
  "isComplete": true,
  "meta": {
    "profile:alice": "Cached",
    "orders:alice": "Cached",
    "recommendations:alice": "Cached"
  }
}
```

---

## ⚠️ Cache Key Scoping

**Keys are global.** The library stores values by the literal string you provide as `key`. If two different users share the same key, they will share the same cached value.

**Always include your scoping dimension in the key:**

```csharp
// ❌ Wrong — all users get the same cached data
builder.Add("profile", () => _profileService.GetProfile(userId), ttl);

// ✅ Correct — each user has their own cache slot
builder.Add($"profile:{userId}", () => _profileService.GetProfile(userId), ttl);
```

This applies to any dimension that distinguishes data: `userId`, `tenantId`, locale, currency, etc.

---

## Core API

### `ITaskAggregator`

```csharp
Task<AggregationResult> RunAsync(
    Action<AggregationBuilder> configure,
    AggregationContext? context = null,
    CancellationToken cancellationToken = default);
```

### `AggregationBuilder`

```csharp
// Async factory
builder.Add<T>(
    key: $"profile:{userId}",
    task: () => service.GetAsync(),
    ttl: TimeSpan.FromMinutes(5),
    timeout: TimeSpan.FromSeconds(2),          // optional per-task timeout
    policyWrapper: inner => policy.ExecuteAsync(inner)); // optional Polly policy
```

### `AggregationResult`

```csharp
T?           result.Get<T>("key");          // null if loading/error/timed-out
TaskMetadata result.GetMetadata("key");     // state, duration, error, isFromCache
bool         result.IsComplete;            // true when all tasks were resolved at snapshot time
IReadOnlyCollection<string> result.Keys;
```

### `TaskMetadata`

| Property      | Type         | Description                                           |
| ------------- | ------------ | ----------------------------------------------------- |
| `State`       | `TaskState`  | `Cached`, `Loading`, `Completed`, `Error`, `TimedOut` |
| `IsFromCache` | `bool`       | Value was served from cache                           |
| `Duration`    | `TimeSpan?`  | Factory wall-clock time                               |
| `Error`       | `Exception?` | Exception when `State == Error`                       |

---

## Caching

### In-Memory (default)

```csharp
builder.Services.AddAsyncFanOut(opt => opt.StaleRatio = 0.8);
```

### Redis / Distributed Cache

```csharp
builder.Services.AddStackExchangeRedisCache(opt => opt.Configuration = "localhost:6379");
builder.Services.AddAsyncFanOutWithDistributedCache(opt => opt.StaleRatio = 0.8);
```

### Stale-While-Revalidate

When `StaleRatio = 0.8` (the default), an entry with `ttl = 5 min`:

- Is **fresh** from t=0 to t=4min — served from cache, no downstream call
- Is **stale** from t=4min to t=5min — served from cache immediately, background refresh triggered
- Is **expired** after t=5min — cache miss, fresh call made

---

## Request Deduplication

If two concurrent BFF requests need the same key and neither is cached, **only one downstream call is made**. Both requests await the same `Task`. Once complete, subsequent requests hit the cache.

```text
Request A ──── cache miss ──── starts factory call ────────────────► result
Request B ──── cache miss ──── waits for same task ─────────────────► result
                                         │
                               only ONE downstream call
```

---

## Error Handling

A failing task sets its slot to `TaskState.Error` and captures the exception in `TaskMetadata.Error`. All other slots are unaffected. The aggregator never throws.

```csharp
var meta = result.GetMetadata($"orders:{userId}");
if (meta.State == TaskState.Error)
{
    logger.LogWarning(meta.Error, "Orders failed for user {UserId}", userId);
}
```

---

## Cancellation

Passing a `CancellationToken` controls how long the caller waits for the first result. **It does not cancel background completion tasks.** Background tasks always run to completion to ensure the cache is populated for the next request.

```csharp
// If the frontend disconnects, we stop waiting for the first result
// but background tasks continue and the cache is still populated.
var result = await aggregator.RunAsync(b => { ... },
    cancellationToken: HttpContext.RequestAborted);
```

---

## Per-Task Timeouts

```csharp
builder.Add(
    key: $"recommendations:{userId}",
    task: () => _recService.GetAsync(userId),
    ttl: TimeSpan.FromSeconds(30),
    timeout: TimeSpan.FromSeconds(2)); // Give up after 2s, not 30s
```

Timed-out tasks receive `TaskState.TimedOut` and are not cached. The next request will retry.

---

## Polly Integration

No hard dependency on Polly. Pass a policy wrapper using the `policyWrapper` parameter:

```csharp
// Define your Polly policy once
var retryPolicy = Policy
    .Handle<HttpRequestException>()
    .WaitAndRetryAsync(3, i => TimeSpan.FromMilliseconds(100 * i));

// Wire it per task
builder.Add(
    key: $"orders:{userId}",
    task: () => _orderService.GetOrdersAsync(userId),
    ttl: TimeSpan.FromMinutes(1),
    policyWrapper: inner => retryPolicy.ExecuteAsync(inner));
```

---

## Observability

AsyncFanOut uses `ILogger<TaskAggregator>` throughout. Enable debug logging to see per-key timings, cache hit/miss, stale revalidation, and background completion:

```json
{
  "Logging": {
    "LogLevel": {
      "AsyncFanOut": "Debug"
    }
  }
}
```

---

## Performance Considerations

- **`FrozenDictionary`** — result values and metadata use `FrozenDictionary<string, T>` for faster repeated reads after construction.
- **Minimal allocations** — `Task.WhenAny` is called only on uncached tasks. Cache hits are synchronous with no async overhead.
- **Lock-free deduplication** — `ConcurrentDictionary.GetOrAdd` avoids explicit locking on the hot path.
- **Background tasks** — launched via `Task.Run(..., CancellationToken.None)`; exceptions are caught and logged, never unobserved.
- **`ConfigureAwait(false)`** throughout — avoids unnecessary context switching in ASP.NET Core.

---

## Roadmap

- **OpenTelemetry** — `ActivitySource` integration for distributed tracing spans per aggregated key
- **`AsyncFanOut.Polly`** — pre-wired extension methods for common Polly policies
- **SignalR push** — `IResultObserver` hook to push background completions to the client in real-time
- **`WaitForAllAsync(result, timeout)`** — convenience helper for scenarios requiring complete data
- **Prometheus metrics** — per-key hit rate, duration histograms, and error rate counters
- **Request-scoped deduplication** — scope deduplication within a single request to avoid cross-request key collisions

---

## License

[MIT](LICENSE)
