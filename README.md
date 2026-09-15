**A note from a real person* before ai generated readme summerizing all sessions and design decisions:**

The design was made by me which is actually a follow up to the first design interview which is nice. I wanted to expand and refine the original design presented on the interview using the same redis focused solution for atomic operations. I hope the use of Lua and redis external cache wasn't missing the point of this assignemnt but I think making the service scalable and avoiding the use of external services would greatly miss the intentions you had in mind.
I chose not to include rate limiting and handling hot/cold traffic differently after a long investigation - the tradeoff between the complexity and time effort of a production-ready solution and the actual latency impact hot players make on cold player with this design I measured during tests wasn't worth the effort - it is described later in the readme. This assignment makes the use of only one primary redis instance which is great to demonstrate the design I had in mind for this and can be further expandable.
Thanks for taking your time and looking at my solution, I hope you enjoy it.

# Player Service

.NET 10 / C# 14 Minimal API for sessions, scores, atomic gifts, and a leaderboard. Uses one external, memory-only Redis primary; API processes hold no player state. The SDK is pinned to 10.0.401. The small domain records are merged into Application, as permitted by the design. The API project file lives at the root so `dotnet run` works there; its source remains under `src/PlayerService.Api`.

## Run

On this Mac, .NET and Redis are installed through Homebrew. The newer Microsoft SDK is also installed under `.dotnet/` and selected by `global.json`. From this directory:

```sh
# Terminal 1, unless Redis is already running:
redis-server --bind 127.0.0.1 --port 6379 --save '' --appendonly no --maxmemory-policy noeviction

# Terminal 2:
dotnet run
# http://localhost:5080

# Optional second API instance, sharing the same Redis:
dotnet run --no-build -- --urls http://localhost:5081
```

For a fresh machine, install .NET 10 and Redis, or run `bash install-sdk.sh` and use `./.dotnet/dotnet` in place of `dotnet`. A .NET 10 host can resolve the local SDK through `global.json`; older hosts cannot. Docker alternative for Redis: `docker compose --profile storage up -d redis` (do not also start native Redis on port 6379).

Override settings using environment variables such as `Redis__ConnectionString`, `Redis__KeyPrefix`, `Redis__SessionSeconds`, and `Redis__IdempotencySeconds`. All API instances must use identical settings. Defaults: localhost:6379, prefix `player-service:`, 30-minute sessions, 24-hour idempotency retention. Redis must be a dedicated standalone primary with `noeviction`; Redis Cluster is unsupported. No persistence is configured: restarting Redis loses players, sessions, and receipts. Stop foreground processes with Ctrl+C; installed tools remain available.

## API

```sh
curl -s http://localhost:5080/login -H 'Content-Type: application/json' \
  -d '{"playerId":"p1","deviceId":"phone1"}'
# Copy sessionToken from the response:
export PLAYER_TOKEN='<sessionToken>'

curl -s http://localhost:5080/players/p1/stats \
  -H "Authorization: Bearer $PLAYER_TOKEN"

curl -s http://localhost:5080/players/p1/stats/score \
  -H "Authorization: Bearer $PLAYER_TOKEN" -H 'Content-Type: application/json' \
  -d '{"points":100,"requestId":"87a7d53c-8573-4c73-a0e5-773906c505f1"}'

# Log p2 in on another device before gifting:
curl -s http://localhost:5080/login -H 'Content-Type: application/json' \
  -d '{"playerId":"p2","deviceId":"phone2"}'
curl -s http://localhost:5080/players/p1/gifts \
  -H "Authorization: Bearer $PLAYER_TOKEN" -H 'Content-Type: application/json' \
  -d '{"toPlayerId":"p2","points":50,"requestId":"baf464ac-d422-44ab-b45b-22fdd15a8dac"}'

curl -s 'http://localhost:5080/leaderboard?n=10' \
  -H "Authorization: Bearer $PLAYER_TOKEN"
```

IDs contain 1–64 ASCII letters, digits, underscores, or hyphens. Points are positive integers; scores and gift counters are bounded to 2,147,483,647. Transfers that would exceed that bound fail before debiting anyone. This keeps every integer exact in Redis Lua, JSON, and the sorted-set score. `giftsSent` and `giftsReceived` count transfers, not points. `lastActive` is an ISO 8601 UTC timestamp for the player's most recent successful request/login; receiving a gift does not count as activity.

Any valid session may read player stats or the leaderboard. Score and gift routes require the session's player to match the route. Leaderboard size defaults to 10 and is limited to 100. Health and metrics endpoints are operational exceptions to session authentication: `/health/live`, `/health/ready`, `/metrics`. The assignment has no real authentication: knowing a player ID is sufficient to log in on a new device. These endpoints are for local evaluation, not a public deployment.

## Concurrency and sessions

One short Lua script owns each operation, including its session check. Redis serializes complete scripts. A gift validates both balances and all relevant key types, then changes both hashes, both leaderboard entries, counters, and its receipt without interleaving. Opposite-direction gifts cannot deadlock because there are no application locks or distributed lock leases. Score additions and gifts share this same serialization boundary. API instances each reuse one connection multiplexer, initialized and warmed before traffic.

All key names are built by `RedisKeys` in C# and passed explicitly through Lua's `KEYS`. Each request makes a preliminary primary read to resolve the device's prior token (login) or the token's player/device identity (authenticated calls). This is key discovery only: Lua rereads the mapping and checks identity, ownership, and expiry before any state change. If another login wins between discovery and execution, the losing login cannot overwrite it; a removed, expired, replaced, or changed session cannot execute using stale keys. This adds a discovery round trip compared with the original implementation. Explicit key declarations do not make cross-slot gifts work on Redis Cluster.

The shared key slots are session, device, authenticated player's hash, authenticated player's session index, and leaderboard (`KEYS[1..5]`). Login adds the prior session key; stats adds the target player's hash; score adds its receipt key; gift adds the recipient hash, recipient session index, and gift receipt key. Startup warmup uses an explicit branch that returns before accessing keys.

Every first successful login grants exactly 1,000 points. A device has one session; simultaneous duplicate logins produce one 200 and conflicts for the others. A player may use several devices. Successful requests slide that device's 30-minute expiry; failed requests do not. Session and reverse device keys must both be live and match. A crashed device is released after inactivity; a client should persist its token across restarts. Login itself is not idempotent, and no logout endpoint is included.

Online state is a per-player sorted set of token expiry times, generated by Redis's clock. Gifts count entries with expiry strictly after the current time. Matching session/device leases are updated in the same script. No session keys may be externally deleted or evicted independently: doing so would invalidate this index invariant. Each activity removes at most 32 old entries, so cleanup cannot grow into an unbounded script. Inactive session sets expire after the lease plus 60 seconds.

Lua provides isolation, **not rollback after script errors**. Scripts validate key types and numeric fields before writing, and JSON snapshots are encoded before balance changes. Infrastructure failure, out-of-memory failure during a script, or external schema corruption is outside the healthy-primary atomicity guarantee. Use adequate memory capacity, `noeviction`, and service-exclusive key ownership. Memory-only storage also means there is no durability or exactly-once guarantee across a Redis restart/failover that loses data.

## Retries and status codes

Score and gift receipts are scoped by operation, sender, and GUID, for 24 hours after the initial result. Identical retries return the original response snapshot even after later activity. Reusing an ID with different points or recipient returns `409 idempotency_conflict`. Gift business rejections are recorded too: an offline-recipient rejection remains a rejection if that recipient subsequently logs in. Valid authentication is still required for replay. Receipt retention does not slide on retries; after it expires, the ID can execute again.

The assignment asks both for "current stats" on score retry and identical answers for simultaneous duplicates. Following the design, committed snapshots take precedence; `GET /stats` supplies current stats. This is an explicit interpretation of the ambiguity.

| Status | Meaning and retry policy |
| --- | --- |
| 200 | Success. Reads can be repeated; repeat mutations only with the same request ID. |
| 400 / 413 / 415 | Malformed JSON/binding, oversized body, or unsupported content type. Correct the request. |
| 401 | Missing, expired, or superseded token. Acquire a valid session; preserve a mutation's ID on retry. |
| 403 | Token player does not own the mutation route. |
| 404 | Requested stats player does not exist. |
| 409 | Active device, offline recipient, insufficient balance, numeric limit, or conflicting ID reuse. Recorded business rejections repeat unchanged; a new intent needs a new ID. |
| 422 | Invalid IDs, points, self-gift, empty request ID, or leaderboard size. Correct the request. |
| 503 | Redis unavailable/timeout or request deadline. Mutation outcome may be unknown: retry using the **same** ID. Login is not safely retryable. |

Redis's default request timeout is 2 seconds and the HTTP deadline is 5 seconds. Commands fail fast when disconnected instead of accumulating in a reconnect backlog. A timeout or disconnected client cannot cancel a mutation already sent to Redis. There is no automatic retry with a new ID. No player identifiers or tokens are logged or added as metric labels.

## Leaderboard and load limitation

The authoritative player hashes and the derived `leaderboard:score` sorted set change in the same script. A top-N range read is O(log P + N); updating an entry costs O(log P). Reads use the primary, so a read after a completed mutation sees that update or newer state, and a leaderboard never observes only half a gift. No full sort or additional response cache is needed.

**The strict load-isolation requirements are not fully satisfied by a single Redis primary.** Redis executes all scripts serially: hot-player work can delay cold-player reads, despite having no application-level player locks. The design is appropriate for demonstrating correct arbitrary-player atomic gifts within the assignment's scope, but is not a claim of strict latency isolation. Short scripts, bounded cleanup, bounded top-N, warm connections, and timeouts limit interference; they do not eliminate it. Reads renew sessions and therefore also execute as scripts on the primary.

## Hot-player protection experiment and final decision

**Decision: revert the protection implementation.** The service has no player rate limiter, fair-scheduling queues, identity cache, reserved admission slots, or traffic-specific 429 responses. The previous request cancellation behavior and original load harness are restored. The experiment's report and raw results are retained as historical evidence, not as an available feature or a current performance measurement of the reverted build.

The experiment combined fair per-player scheduling with a shared limit of 40 requests/sec/player, burst 8, queues of 8 per instance, and a 150 ms queue deadline. Twelve local trials compared protection OFF/ON at two offered hot traffic levels, with 80 cold reads/sec and two API instances. Latency below is the median of three per-run **successful-response p99** measurements; rejection rates aggregate mixed-phase attempts.

| Offered hot traffic | Protection | Cold p99 | Hot p99 | Cold non-success | Hot rejected (429) |
| --- | --- | ---: | ---: | ---: | ---: |
| 3,200 requests/sec | OFF | 4.46 ms | 3.82 ms | 0% | 0% |
| 3,200 requests/sec | ON | 3.41 ms | 151.31 ms | 0% | 97.46% |
| 12,800 requests/sec | OFF | 5.89 ms | 7.25 ms | 0% | 0% |
| 12,800 requests/sec | ON | 3.93 ms | 152.71 ms | 0% | 99.37% |

Neither mode had server/transport failures. All 19,192 measured cold requests succeeded, and every trial conserved 34,000 points. Cold median latency increased with protection enabled. The generator skipped a small number of scheduled launches; these are reported separately in the full report and are not counted as HTTP failures.

**Why it was reverted:** saving roughly 1–2 ms in cold p99 did not justify rejecting 97–99% of hot requests and increasing successful hot p99 to about 150 ms. The 40 requests/sec cap was not calibrated against a measured capacity boundary or an agreed cold-player latency target. The tests proved enforcement and queue fairness, but did not demonstrate a worthwhile net benefit or failure prevention at these loads. The enabled implementation passed 28 tests before removal. After reverting, the original 22-test correctness suite passes, and the Release solution builds with zero warnings/errors.

Keep the simpler, unthrottled implementation. Reconsider admission controls only after defining a cold-player latency/error target, measuring where the unprotected service breaches it, and demonstrating that a calibrated control protects that target while preserving useful hot throughput. This decision does not remove the shared-Redis interference limitation or establish safety under unlimited load. These localhost measurements are not a production capacity forecast or a statistically established performance guarantee.

- [Full experiment report, baseline comparisons, and raw results](artifacts/traffic-isolation-2026-09-14/REPORT.md).
- [Earlier removed limiter experiment](artifacts/admission-ab-2026-09-14/REPORT.md), which also did not establish a cold-p99 benefit.

The saved ON results cannot be reproduced by setting `Traffic__Enabled` on the current service: that configuration and implementation have been removed. The historical runner is blocked to prevent producing misleading OFF/ON results. The archived analysis script can still regenerate the report from saved JSON.


## Verify

```sh
dotnet test PlayerService.slnx
dotnet run --project tools/PlayerService.LoadHarness -- \
  http://localhost:5080,http://localhost:5081 5 32 artifacts/load-results.json
```

Tests use real Redis at `TEST_REDIS` (default localhost:6379), with unique per-test prefixes and two independent ASP.NET hosts/multiplexers. They never flush Redis or skip missing dependencies. Coverage includes duplicate login, multiple devices, expiry and sliding renewal, superseded tokens, authorization, concurrent increments, duplicate response equality, overlapping opposite gifts, insufficient funds, remembered rejections, overflow, wrong key types, and metric cardinality. Live leaderboard samples during gifts assert conservation; after writes settle, all balances, counters, and leaderboard entries are checked. Sequential individual reads during transfers would not be a valid conservation snapshot.

The overlapping workload contains all 132 directed pairs among 12 players plus seeded additional transfers, shuffled before 1,200 concurrent requests. Assertions verify graph coverage, individual balances/counters, and total conservation. Additional real-Redis tests intercept the point between key discovery and script execution to force session and login races. A test-only Lua wrapper rejects undeclared key access on the exercised script paths, including receipt replay, cleanup, dangling-device repair, and warmup.

The HTTP load harness compares cold reads alone with cold reads alongside hot-player gifts across the given API URLs. It reports throughput, cold/hot p50/p95/p99, status counts, and final conservation. It is a closed-loop local diagnostic, not a production capacity forecast or an open-loop saturation test. It creates 34 uniquely named test players; their in-memory player records remain until Redis restarts, and session/receipt keys expire normally.

Reported latency and throughput include all response statuses; status counts combine hot and cold traffic. Inspect errors before interpreting performance. Saved admission-enabled results are historical and do not describe the current implementation.

## Metrics

OpenTelemetry exports .NET HTTP duration/status and active-request metrics, Redis operation latency/errors, business outcomes/replays, and leaderboard size. `operation`, `outcome`, and `replay` labels have bounded values. Redis latency includes client queueing and the network round trip. Leaderboard duration is available under `operation="Leaderboard"`.

```sh
# Docker Desktop must be running. Bind the API so Docker can scrape it:
dotnet run --no-build -- --urls http://0.0.0.0:5080
docker compose up -d prometheus grafana
# Prometheus: http://localhost:9090
# Grafana dashboard: http://localhost:3000/d/player-service
```

The dashboard has four panels: HTTP rate/p95/p99, Redis latency/failures, gifts/replays, and 5xx/timeouts. Prometheus scrapes ports 5080 and 5081; if only one API instance is running, remove the other target or expect it to appear down. Stop monitoring with `docker compose down`.

The direct OpenTelemetry Prometheus ASP.NET exporter remains beta (`1.18.0-beta.1`); the SDK/hosting package is stable. This preserves the design's API `/metrics` endpoint. If all dependencies must be stable, use the stable OTLP exporter with Prometheus's OTLP receiver or a collector, which changes the scraping setup. Tracing, logging backends, alerts, UI, persistence, deployment automation, and authentication hardening are intentionally excluded.

## References

- [.NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) and [C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14).
- [Redis Lua execution and its blocking semantics](https://redis.io/docs/latest/develop/programmability/eval-intro/).

Implementation changes from the design: .NET 10/C# 14; merged the tiny domain into Application; atomic session checks for every operation; request-payload conflict detection; explicit integer bounds; bounded session cleanup with range-count online checks; startup script warmup; and a runnable two-instance concurrency suite plus hot/cold HTTP harness. The original design files are unchanged.
