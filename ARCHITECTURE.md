# AsyncFanOut - Architecture & Codebase Guide

## What Is This?

AsyncFanOut is a .NET library for **BFF (Backend-For-Frontend) task aggregation**. It solves the problem of a BFF needing to call multiple microservices in parallel and returning results to the frontend as fast as possible — even if some services are slow.

**Core idea:** Fan out requests to N services, return a partial result as soon as the first one completes, and continue the rest in the background so subsequent requests hit cache.

---

## High-Level Architecture

```
                         ┌──────────────────────────────────────────────┐
                         │              Your BFF Controller             │
                         │                                              │
                         │  aggregator.RunAsync(builder => {            │
                         │      builder.Add("profile", GetProfile);     │
                         │      builder.Add("orders",  GetOrders);      │
                         │      builder.Add("recs",    GetRecs);        │
                         │  });                                         │
                         └─────────────────┬────────────────────────────┘
                                           │
                                           ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                          TaskAggregator (Orchestrator)                       │
│                                                                             │
│  Phase 1: Cache Check ─────────────────────────────────────────────────┐    │
│  For each task key:                                                    │    │
│    ├─ FRESH hit   → store in results, mark Cached                      │    │
│    ├─ STALE hit   → store stale value + background refresh             │    │
│    └─ MISS        → start via TaskDeduplicator                         │    │
│                                                                        │    │
│  Phase 2: All Cached? ─────────────────────────────────────────────────┤    │
│    └─ Yes → return immediately (IsComplete: true)                      │    │
│                                                                        │    │
│  Phase 3: Wait ────────────────────────────────────────────────────────┤    │
│    ├─ If any task has waitForCompletion → WhenAll(mandatory tasks)      │    │
│    └─ Otherwise → WhenAny(all pending tasks)                           │    │
│    └─ Caller's CancellationToken controls wait timeout                 │    │
│                                                                        │    │
│  Phase 4: Snapshot ────────────────────────────────────────────────────┤    │
│    For each completed task:                                            │    │
│    ├─ Success     → store value, write to cache, mark Completed        │    │
│    ├─ Timeout     → mark TimedOut                                      │    │
│    └─ Exception   → mark Error, capture exception                      │    │
│                                                                        │    │
│  Phase 5: Background ──────────────────────────────────────────────────┘    │
│    All remaining tasks continue with CancellationToken.None                 │
│    Results written to cache as they complete                                │
│    (ensures next request gets instant response)                             │
└─────────────────────────────────────────────────────────────────────────────┘
                                           │
                                           ▼
                         ┌──────────────────────────────────────┐
                         │         AggregationResult            │
                         │  (immutable point-in-time snapshot)  │
                         │                                      │
                         │  .Get<UserProfile>("profile")  → val │
                         │  .GetMetadata("profile")  → state,   │
                         │      duration, isFromCache, error    │
                         │  .IsComplete → false (recs pending)  │
                         └──────────────────────────────────────┘
```

---

## Request Lifecycle (Two Requests)

This shows the key value proposition — the second request is instant:

```
REQUEST 1 (cold cache)
══════════════════════════════════════════════════════════════════

  t=0ms     Fan out 3 tasks in parallel
            ┌─ profile (2000ms) [waitForCompletion: true]
            ├─ orders  (30ms)
            └─ recs    (10000ms)

  t=30ms    orders completes → cached
  t=2000ms  profile completes → cached
            ── RESPONSE RETURNED ──
            { profile: {...}, orders: {...}, recs: null }
            metadata: { profile: Completed, orders: Completed, recs: Loading }
            IsComplete: false

  t=10000ms recs completes in background → cached (no one waiting)


REQUEST 2 (warm cache, moments later)
══════════════════════════════════════════════════════════════════

  t=0ms     Cache check: all 3 keys found fresh
            ── RESPONSE RETURNED IMMEDIATELY ──
            { profile: {...}, orders: {...}, recs: {...} }
            metadata: { profile: Cached, orders: Cached, recs: Cached }
            IsComplete: true
```

---

## Component Dependency Diagram

```
┌─────────────────────────────────────────────────────────┐
│                   DI Registration                        │
│          ServiceCollectionExtensions                     │
│  ┌──────────────────┐  ┌─────────────────────────────┐  │
│  │ AddAsyncFanOut()  │  │ AddAsyncFanOutWithDistCache │  │
│  │ (in-memory)       │  │ (Redis/SQL Server/etc.)     │  │
│  └────────┬─────────┘  └──────────┬──────────────────┘  │
│           │   Registers all:      │                      │
│           └───────┬───────────────┘                      │
└───────────────────┼──────────────────────────────────────┘
                    │
    ┌───────────────┼────────────────────────┐
    ▼               ▼                        ▼
┌────────┐  ┌──────────────┐  ┌──────────────────────┐
│ ITask  │  │ IBackground  │  │  IAggregationCache   │
│Aggregat│  │ TaskRunner   │  │                      │
│  or    │  │              │  │ ┌──────────────────┐ │
│        │  │ Background   │  │ │InMemoryAgg.Cache │ │
│ Task   │  │ TaskRunner   │  │ │  (IMemoryCache)  │ │
│Aggregat│  │              │  │ ├──────────────────┤ │
│  or    │  │ Fire-and-    │  │ │DistributedAgg.   │ │
│        │  │ forget with  │  │ │ Cache (IDistrib  │ │
└───┬────┘  │ exception    │  │ │  utedCache)      │ │
    │       │ logging      │  │ └──────────────────┘ │
    │       └──────────────┘  └──────────────────────┘
    │
    ├─── uses ──► TaskDeduplicator
    │             (ConcurrentDictionary, lock-free)
    │             One in-flight task per key across all requests
    │
    └─── builds via ──► AggregationBuilder
                        Fluent API: .Add<T>(key, factory, ttl, ...)
                        Produces List<AggregationTaskBase>
```

---

## File-by-File Breakdown

### Models (`src/AsyncFanOut/Models/`)

| File | Purpose |
|------|---------|
| **AggregationTask.cs** | Abstract `AggregationTaskBase` + generic `AggregationTask<T>`. Wraps a `Func<Task<T>>` factory with key, TTL, timeout, and optional Polly policy. `InvokeAsync()` applies timeout via linked CancellationTokenSource. |
| **TaskState.cs** | Enum: `Cached`, `Loading`, `Completed`, `Error`, `TimedOut` |
| **TaskMetadata.cs** | Immutable record per task: state, isFromCache, duration, error. Static factory methods (`FromCache()`, `Loading()`, `Completed()`, `ForError()`, `TimedOut()`). |
| **AggregationResult.cs** | Immutable snapshot using `FrozenDictionary`. `Get<T>(key)` for values, `GetMetadata(key)` for outcome info, `IsComplete` flag, `Keys` collection. |
| **AggregationContext.cs** | Request-scoped context with `CorrelationId` (auto-generated GUID) and `StartedAt` (UTC). |

### Aggregator (`src/AsyncFanOut/Aggregator/`)

| File | Purpose |
|------|---------|
| **ITaskAggregator.cs** | Public interface. Single method: `RunAsync(configure, context?, cancellationToken)`. |
| **TaskAggregator.cs** | **The core engine** (~256 lines). Implements the 5-phase orchestration described above. Key detail: uses `d.InvokeAsync(CancellationToken.None)` directly in the deduplicator (NOT `SafeInvokeAsync`) so exceptions propagate for classification. `SafeInvokeAsync` is only used for stale-while-revalidate background refresh. |
| **AggregationBuilder.cs** | Fluent builder. `Add<T>(key, factory, ttl, timeout?, policyWrapper?, waitForCompletion?)` returns `this` for chaining. Stores `List<AggregationTaskBase>` internally. |

### Execution (`src/AsyncFanOut/Execution/`)

| File | Purpose |
|------|---------|
| **TaskDeduplicator.cs** | Lock-free deduplication using `ConcurrentDictionary<string, Task<object?>>`. Ensures ONE in-flight factory per key globally. After completion (success or fault), removes entry so next caller retries (typically hits cache). |
| **IBackgroundTaskRunner.cs** | Interface: `void Run(Func<Task> work, ILogger? logger)`. |
| **BackgroundTaskRunner.cs** | `Task.Run()` with `CancellationToken.None` and exception logging. Decouples background work from caller lifecycle. |

### Cache (`src/AsyncFanOut/Cache/`)

| File | Purpose |
|------|---------|
| **IAggregationCache.cs** | Interface with `TryGet` (fresh only), `TryGetWithMeta` (fresh + stale), `SetAsync`, `RemoveAsync`. |
| **CacheEntryMeta.cs** | Internal wrapper storing value + `StaleAt` + `ExpiresAt` timestamps. `IsStale` and `IsExpired` computed properties. |
| **InMemoryAggregationCache.cs** | Default implementation using `IMemoryCache`. Configurable `StaleRatio` (default 0.8). |
| **DistributedAggregationCache.cs** | Redis/SQL Server compatible. JSON serialization via `System.Text.Json`. Stores type name for deserialization. |

### Extensions (`src/AsyncFanOut/Extensions/`)

| File | Purpose |
|------|---------|
| **ServiceCollectionExtensions.cs** | `AddAsyncFanOut()` (in-memory) and `AddAsyncFanOutWithDistributedCache()` (distributed). Both register `TaskAggregator`, `TaskDeduplicator`, `BackgroundTaskRunner` as singletons. Configurable via `AsyncFanOutOptions`. |

---

## Stale-While-Revalidate Pattern

```
   StaleRatio = 0.8, TTL = 5 minutes

   ◄──────── FRESH ─────────►◄── STALE ──►◄── EXPIRED ──►
   │                         │             │              │
   t=0                    t=4:00        t=5:00          t=∞
   (set)                  (staleAt)     (expiresAt)

   FRESH:   Serve from cache. No action.
   STALE:   Serve from cache + trigger background refresh.
   EXPIRED: Cache miss. New factory invocation.
```

---

## Deduplication Flow

```
   Request A ──┐
               ├──► TaskDeduplicator.GetOrAddAsync("profile")
   Request B ──┘         │
                         ▼
                  ConcurrentDictionary
                  ┌──────────────────┐
                  │ "profile" → Task │ ◄── Only ONE factory runs
                  └──────────────────┘
                         │
                    Task completes
                         │
                  ┌──────┴──────┐
                  ▼             ▼
              Request A     Request B
              gets result   gets same result
                         │
                  Entry removed from dict
                         │
   Request C ──► GetOrAddAsync("profile")
                  └──► Cache hit (no factory needed)
```

---

## Cancellation Model

```
   Caller's CancellationToken
   ┌────────────────────────────────────────────┐
   │  Controls ONLY: how long Phase 3 waits     │
   │  Does NOT cancel: background tasks          │
   └────────────────────────────────────────────┘

   CancellationToken.None
   ┌────────────────────────────────────────────┐
   │  Used for: ALL background task execution    │
   │  Reason: Cache must be populated for next   │
   │          request regardless of caller        │
   └────────────────────────────────────────────┘

   Per-Task Timeout CTS
   ┌────────────────────────────────────────────┐
   │  Created in AggregationTask.InvokeAsync()  │
   │  Linked to caller's token                   │
   │  Fires after task.Timeout duration           │
   │  Result: OperationCanceledException          │
   │          → classified as TimedOut            │
   └────────────────────────────────────────────┘
```

---

## Error Handling

```
   Main Pipeline (cache miss)              Stale Refresh (background)
   ─────────────────────────               ─────────────────────────
   d.InvokeAsync() directly                SafeInvokeAsync()
         │                                       │
         ▼                                       ▼
   Exception propagates                   Exception swallowed
   to ApplyCompletedTaskAsync             (bool success, value) returned
         │                                       │
   Classified as:                         If failed:
   ├─ OperationCanceledException            └─ Stale value remains
   │  from timeout CTS → TimedOut               in cache (still served)
   └─ Any other exception → Error
      (captured in TaskMetadata)
```

---

## Test Coverage

### `TaskDeduplicatorTests.cs` (5 tests)
- Single call invokes factory once
- Concurrent calls share one invocation
- Different keys are independent
- Completed entry removed, next call re-invokes
- Faulted entry removed, next call retries

### `InMemoryCacheTests.cs` (9 tests)
- Basic set/get flow
- Unknown key returns false
- Removal clears entry
- Stale entries: hidden by `TryGet`, visible via `TryGetWithMeta`
- StaleRatio math verified (80% of 5min = stale at 4min)
- Null value caching
- Overwrite behavior

### `TaskAggregatorTests.cs` (8 tests)
- All-cached returns complete immediately
- First completion appears; others show Loading
- Background tasks populate cache for next request
- Failing task isolated in metadata (doesn't crash aggregator)
- Timeout classified correctly
- Caller cancellation doesn't prevent background cache writes
- Metadata/Keys collection correctness

### `IntegrationTests.cs` (7 tests)
- End-to-end with real DI container
- Partial result on first request, full cache on second
- Error isolation across services
- Context propagation

---

## Sample App (`samples/AsyncFanOut.Sample/`)

```
GET /api/dashboard/{userId}

  ┌─ UserProfileService    (2000ms)  [waitForCompletion: true]
  ├─ OrderService          (30ms)
  ├─ RecommendationService (10000ms)
  └─ NotificationService   (20ms)

Response:
{
  "correlationId": "abc-123",
  "isComplete": false,
  "values": {
    "profile":       { "name": "Alice", ... },
    "orders":        [ ... ],
    "recommendations": null,        // still loading
    "notifications": [ ... ]
  },
  "metadata": {
    "profile":         { "state": "Completed", "durationMs": 2001 },
    "orders":          { "state": "Completed", "durationMs": 31 },
    "recommendations": { "state": "Loading" },
    "notifications":   { "state": "Completed", "durationMs": 22 }
  }
}
```

---

## Complete File Tree

```
TaskFanOut/
├── AsyncFanOut.slnx                          # Solution file (modern format)
├── README.md
├── src/AsyncFanOut/
│   ├── AsyncFanOut.csproj                    # net10.0, NuGet package config
│   ├── Models/
│   │   ├── AggregationTask.cs               # Task descriptor + factory wrapper
│   │   ├── AggregationContext.cs             # Correlation ID + timestamp
│   │   ├── AggregationResult.cs             # Immutable result snapshot
│   │   ├── TaskMetadata.cs                  # Per-task outcome metadata
│   │   └── TaskState.cs                     # Enum: Cached/Loading/Completed/Error/TimedOut
│   ├── Aggregator/
│   │   ├── ITaskAggregator.cs               # Public interface
│   │   ├── TaskAggregator.cs                # Core 5-phase orchestration engine
│   │   └── AggregationBuilder.cs            # Fluent task registration API
│   ├── Execution/
│   │   ├── TaskDeduplicator.cs              # Lock-free per-key deduplication
│   │   ├── IBackgroundTaskRunner.cs          # Background work interface
│   │   └── BackgroundTaskRunner.cs          # Fire-and-forget with logging
│   ├── Cache/
│   │   ├── IAggregationCache.cs             # Cache abstraction (fresh + stale)
│   │   ├── CacheEntryMeta.cs                # Internal: value + timestamps
│   │   ├── InMemoryAggregationCache.cs      # IMemoryCache wrapper
│   │   └── DistributedAggregationCache.cs   # IDistributedCache (Redis, etc.)
│   └── Extensions/
│       └── ServiceCollectionExtensions.cs   # DI registration helpers
├── tests/AsyncFanOut.Tests/
│   ├── AsyncFanOut.Tests.csproj
│   ├── TaskDeduplicatorTests.cs             # 5 tests
│   ├── InMemoryCacheTests.cs                # 9 tests
│   ├── TaskAggregatorTests.cs               # 8 tests
│   └── IntegrationTests.cs                  # 7 tests
└── samples/AsyncFanOut.Sample/
    ├── AsyncFanOut.Sample.csproj
    ├── Program.cs                            # DI setup + fake services
    └── Controllers/
        └── DashboardController.cs           # Example BFF endpoint
```
