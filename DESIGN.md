# Player Service Design

This document records decisions agreed during design. The original requirements are in [Backend_Home_Assignment.md](Backend_Home_Assignment.md).

## Architecture

- Build a .NET 10 ASP.NET Core Web API.
- Use an external Redis instance as the in-memory datastore. No database is used.
- API instances are stateless and can scale horizontally behind a load balancer.
- Use one Redis primary for all mutations in this assignment. Redis Lua scripts provide atomic multi-key operations; this avoids the cross-slot atomicity limitation of a Redis Cluster for transfers between arbitrary players.
- Use `StackExchange.Redis` and short, preloaded Lua scripts for state-changing operations.

## Login And Sessions

### Token and session policy

- Create a simple opaque session token with `Guid.NewGuid()`; real authentication and token hardening are out of scope.
- Persist the token on the mobile client. After an app/device restart, the client uses the saved token instead of logging in again.
- Sessions use a 30-minute sliding inactivity expiry. Every successful authenticated request renews the expiry.
- This balances the stated requirements: a short idle period keeps the session alive, while an abandoned/crashed client naturally releases its device after the inactivity lease.
- Login is not idempotent because the specified request has no request ID. Two simultaneous identical logins have one `200 OK` result and one `409 Conflict` result.
- Expired or replaced tokens return `401 Unauthorized`.

### Device and player policy

- A device may have exactly one active session.
- The same player may have active sessions on several devices.
- A player is online when at least one unexpired device session exists. Gifting relies on this definition.
- No logout endpoint is included in v1 because it is not required by the assignment.

### Redis data

```text
session:{token}       -> playerId, deviceId       TTL: 30 minutes
device:{deviceId}     -> token                    TTL: 30 minutes
session:player:{id}   -> sorted set: token => expiry timestamp
```

The first two keys are intentional reverse indexes. Requests provide only a token and need token-to-session lookup in O(1); login provides a device ID and needs device-to-active-session lookup in O(1). A single opaque-token key could not support both without scanning sessions.

`session:player:{id}` represents a player's device sessions. Its members are removed when checking online status if their expiry score has passed; the set itself gets a modest TTL extension on activity so inactive-player sets are eventually reclaimed.

### Atomic operations

- The login Lua script checks the device index, removes a dangling entry if necessary, creates the session and device keys with identical TTLs, and adds the token to the player's active-session set.
- The authentication Lua script resolves the token, verifies that its device key points back to that token, renews both TTLs, and refreshes the active-session expiry score.
- A token missing either required key, or no longer matching the device index, is rejected.

## Open Design Areas

- Concurrency test harness

## .NET Service Structure

Use a lightweight hexagonal/clean modular-monolith structure:

```text
src/
  PlayerService.Api/
    Endpoints/
    Contracts/
    Middleware/
    Program.cs

  PlayerService.Application/
    Abstractions/
    Sessions/
    Scores/
    Gifts/
    Leaderboard/

  PlayerService.Domain/
    Models/
    Results/
    Errors/

  PlayerService.Infrastructure.Redis/
    Scripts/
    Stores/
    RedisKeys.cs
    ServiceCollectionExtensions.cs

tests/
  PlayerService.Tests/
```

`PlayerService.Domain` remains deliberately small. If it contains only a few pure records and error types, it may be merged into `PlayerService.Application` without changing the dependency direction.

```text
Api -> Application -> Domain
Api -> Infrastructure.Redis -> Domain
```

- `Api` owns Minimal API endpoints, HTTP contracts, status mapping, middleware, configuration binding, and composition.
- `Application` owns commands, result types, validation, and use-case-oriented abstractions.
- `Infrastructure.Redis` owns `StackExchange.Redis`, key naming, and Lua script execution.
- `Domain` contains only pure business models, result records, and error types; it has no ASP.NET Core or Redis dependency.
- `Tests` contains unit and Redis-backed integration/concurrency tests.

Use operation-oriented abstractions such as `ISessionStore`, `IScoreUpdateStore`, `IGiftStore`, `IPlayerStatsStore`, and `ILeaderboardStore`. Do not introduce generic player CRUD repositories: `IGiftStore.ExecuteAsync` must map to one Redis Lua script so that application code cannot accidentally separate the atomic transfer.

### Naming

- Use PascalCase for projects, namespaces, folders, types, methods, and public members; camelCase for locals and parameters.
- Prefix interfaces with `I` and suffix asynchronous methods with `Async`.
- Name each file after its primary type, such as `RedisGiftStore.cs`, `GiftResult.cs`, and `LoginRequest.cs`.
- Use `Abstractions` rather than `Ports` for the interface folder; it is more familiar in .NET codebases.

## Score Updates

### Rules

- `POST /players/{playerId}/stats/score` accepts a strictly positive integer number of points. This endpoint only adds points; deductions belong to gifting.
- The authenticated token must belong to the route's `playerId`; otherwise the request returns `403 Forbidden`.
- Every valid mutation request must include a GUID `requestId`.

### Redis data

```text
player:{playerId}                         -> Redis hash: score, giftsSent, giftsReceived, lastActive
score-request:{playerId}:{requestId}      -> committed response snapshot, TTL: 24 hours
```

Player data is initialized with a score of 1,000 as part of the first successful login.

### Atomic update

A score-update Lua script performs the complete idempotency check and increment:

1. Validate the request before any mutation.
2. Look up the request-result key. If it exists, return its stored response without changing the score.
3. Increment the player's score with `HINCRBY`.
4. Build and store the successful response snapshot with a 24-hour TTL.
5. Return that snapshot.

Redis executes Lua scripts serially. Thus requests with different IDs each see the last committed score, while simultaneous requests with the same ID produce one increment and identical responses. The commit boundary is the successful completion of the script; no client can interleave a command between its idempotency check and score increment.

Scripts are atomic with respect to interleaving but Redis does not roll back writes if a script errors midway. Inputs and Redis key types must be validated before the first write, and remaining commands must be deterministic and unable to fail under the established schema.

The request-result key saves the original committed stats snapshot. This is the stronger interpretation of the requirements: concurrent duplicates always return the same answer. A later `GET /stats` returns the then-current score after other mutations.

## Gifting

### Redis data

```text
player:{senderId}                         -> score, giftsSent, giftsReceived, lastActive
player:{recipientId}                      -> score, giftsSent, giftsReceived, lastActive
gift-request:{senderId}:{requestId}       -> original outcome, TTL: 24 hours
session:player:{recipientId}              -> sorted set: token => session-expiry timestamp
```

The 24-hour TTL belongs only to the gift idempotency record. The active-session sorted-set score is a Redis-server timestamp of `now + 30 minutes`, refreshed whenever that device successfully authenticates. It is not a 24-hour state.

### Atomic transfer

One Redis Lua script owns each gift. It validates the sender session and renews it, then:

1. Return the stored original outcome when the sender/request ID key exists.
2. Reject invalid input and self-gifts.
3. Remove recipient session-set members whose expiry is at or before Redis's current time. Reject the gift when no member remains.
4. Reject insufficient sender balance.
5. Debit the sender, increment `giftsSent`, credit the recipient, and increment `giftsReceived`.
6. Store and return the successful outcome for 24 hours.

Definitive business rejections, including `recipient_offline` and insufficient balance, are stored as outcomes too. A retry of the same request ID always returns the initial outcome; it does not reconsider whether a recipient came online later.

Redis serializes the gift script with score-update scripts and all other Lua scripts. A score update therefore runs entirely before or entirely after a gift, never between debit and credit. Its ordering can change the balance available to a gift, as it should; each successful gift itself conserves points exactly.

The active-session set is an online-state index. Matching 30-minute TTLs on `session:{token}` and `device:{deviceId}` are the source of session validity; the set efficiently answers whether a player has at least one live device session. The set receives a cleanup TTL after inactivity to prevent abandoned membership metadata from accumulating.

## Leaderboard

### Redis data and ownership

```text
player:{playerId}          -> authoritative hash: score, giftsSent, giftsReceived, lastActive
leaderboard:score          -> Redis sorted set: playerId => score
```

The player hash is the score source of truth. `leaderboard:score` is a synchronously maintained, derived read index. Keeping the score in the player hash makes `GET /players/{playerId}/stats` a single direct record read.

All score-changing Lua scripts update both locations without interleaving:

```text
HINCRBY player:{id} score points
ZADD leaderboard:score resultingScore playerId
```

A gift performs the corresponding pair for both sender and recipient before the script completes. No non-script code may mutate either score location.

### Reads and consistency

- `GET /leaderboard` reads `leaderboard:score` with `ZREVRANGE leaderboard:score 0 N-1 WITHSCORES`.
- A top-N read is `O(log P + N)`, while each changed player costs `O(log P)` to reposition in the sorted-set index. No request sorts all players.
- Tie ordering uses Redis's default deterministic ordering; no product-level tie-breaker is required.
- No separate response cache is used. The sorted set is the in-memory materialized index, and small top-N range reads are inexpensive. Caching responses would be invalidated by nearly every mutation and add staleness and cache-miss coordination without material benefit.
- The staleness guarantee is strong for primary reads: once a mutation script returns, its sorted-set update has completed, so subsequent primary leaderboard reads include the changed score. A reader cannot observe a gift with only one participant's leaderboard entry updated.

## Behaviour Under Load

- API instances are stateless, use asynchronous handlers, and share one `StackExchange.Redis` `ConnectionMultiplexer` per process. No Redis connection is created per request.
- There are no application-level player locks, queues, or synchronized shared dictionaries. Every write is a short, bounded Redis Lua script, and reads are direct keyed Redis reads.
- A hot player's requests contend only by consuming Redis command capacity; they cannot hold a player-specific lock that blocks a cold player's state. Redis may still queue all commands briefly at its single primary, so this design does not claim hard latency isolation under sustained primary saturation.
- This tradeoff is deliberate for the assignment: a single primary supplies the atomic multi-player gift transaction. Strict workload isolation would require sharded ownership plus cross-shard transaction coordination, which is outside the intended 4-6 hour scope.
- Per-player gift rate limiting is deliberately excluded from v1. It remains the next protective measure if hot-player traffic needs to be constrained.
- Scripts must contain only bounded Redis operations: no scans, full sorts, external calls, blocking waits, or unbounded loops.
- Use request timeouts. The server never invents a new idempotency ID when a mutation times out.

### Retry and response policy

| Request | Retry policy |
| --- | --- |
| `GET /stats`, `GET /leaderboard` | Always safe to retry. |
| Score update and gift | Safe only with the same `requestId`. |
| Login | Not safely retryable because the specified request has no request ID. |
| Redis timeout or temporary outage | Return `503 Service Unavailable`; clients may retry mutations with the same request ID. |

Use `401` for expired tokens, `403` when a token does not belong to the route player, `409` for an already-active device session and definitive gift conflicts, and `422` for invalid input.

## Observability

### Scope and technology

- Instrument the .NET service with OpenTelemetry metrics.
- Expose a Prometheus-compatible `/metrics` endpoint from the API.
- Provide a small Docker Compose observability stack containing Prometheus and Grafana. Prometheus scrapes the API endpoint; Grafana uses Prometheus as its data source.
- Do not add Grafana Alloy, Loki, Tempo, logging infrastructure, a tracing backend, alerting rules, or distributed tracing in v1. OpenTelemetry keeps those additions possible later without coupling application code to a vendor.

### Metrics

- HTTP request count, duration histogram, response-status count, and in-flight request count.
- Redis/Lua script latency histogram and Redis timeout/error count.
- Login outcomes: success, active-device conflict, and expired-token rejection.
- Score-update and gift outcomes, including idempotency hits.
- Leaderboard read duration and returned entry count.

Do not use `playerId`, `deviceId`, `requestId`, or session token as metric labels: they have unbounded cardinality.

### Dashboard

Create a compact Grafana dashboard with four panels:

1. Request rate and p95/p99 request latency.
2. Redis/Lua latency and Redis failure count.
3. Gift outcomes and idempotency-hit rate.
4. HTTP 5xx and timeout rate.
