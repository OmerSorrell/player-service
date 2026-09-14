# Project review — 2026-09-13

Scope: C# application/API/Redis adapter, all Lua scripts, tests, load harness, project settings, installer, and monitoring configuration. This is a source and dependency review, not a production security certification. The enum refactor is complete. Follow-up fixes below resolve findings 1 and 3; other findings remain recommendations. Locations refer to the originally reviewed version where line numbers have shifted.

## Completed change

Added internal `RedisOperation` and changed the script dictionary, public-method dispatch, and private executor to use it. An explicit mapping preserves existing embedded resource names and Prometheus operation labels even if enum members are renamed. The script dictionary is exposed internally through `IReadOnlyDictionary`. No HTTP contracts, Lua behavior, or Redis keys changed.

## Findings, in priority order

### 1. Resolved — The overlapping-transfer test only exercised independent pairs

Location: [ConcurrencyTests.cs](tests/PlayerService.Tests/ConcurrencyTests.cs), lines 112–113.

With 12 players, the parity of `i` always matches the parity of `i % 12`. Consequently, the recipient formula generates only `0→1`, `1→0`, `2→3`, `3→2`, through `10→11`, `11→10`. Each player only interacts with one counterpart. The test demonstrates opposite-direction concurrency and conservation, but does not exercise arbitrary overlapping transfer graphs despite its name and the verification report's description.

Resolution: the workload now includes all 132 directed pairs among 12 players, seeded additional pairs, and a deterministic shuffle. It asserts all counterpart relationships and a full-player cycle, then checks individual expected balances/counters as well as live and final conservation. A separate test confirms that expiry on one device does not prevent gifting to a recipient whose other device remains active.

### 2. P2 — Permanent Redis script failures are classified as temporary outages

Location: [Program.cs](src/PlayerService.Api/Program.cs), lines 49–62.

The exception filters cover every `RedisException`, including server errors caused by wrong key types or malformed stored data. These become `503` with `Retry-After: 1`, although retrying cannot repair the data. Logs retain only the exception class. At startup, a script error is also described as Redis being unavailable; readiness only tests PING and can report healthy even when a script cannot run.

Recommendation: distinguish transport/timeouts from permanent script/schema errors, give unexpected internal failures a consistent 500 response, and record sanitized operation/error classification. If startup script validation fails permanently, fail startup or readiness rather than reporting only a connectivity warning. Do not indiscriminately log raw Redis exceptions because they can contain operational or request details.

### 3. Resolved — Scripts did not declare accessed keys through KEYS

Location: [RedisPlayerStore.cs](src/PlayerService.Infrastructure.Redis/RedisPlayerStore.cs), lines 37 and 61; [Common.lua](src/PlayerService.Infrastructure.Redis/Scripts/Common.lua), lines 7–9 and 38–48.

Both script calls provide an empty key array, while Lua derives key names from arguments and session contents. The implementation works in its tested standalone-primary environment, but this departs from Redis's explicit-key scripting guidance, which applies to standalone as well as clustered deployments. It prevents reliable key routing/declaration and is not made best practice merely by excluding Redis Cluster.

Resolution: `RedisKeys` constructs names in C#, and every operational script declares its accessed keys through `KEYS`. A preliminary primary read discovers session/device identity; Lua revalidates it atomically before writes. Login rejects a competing mapping instead of touching an undeclared new session. Test interception forces mapping changes between discovery and execution, and a Lua guard checks declared-key access on exercised paths. Warmup explicitly returns before any key access. This introduces one discovery round trip per request. Arbitrary-player gifts remain incompatible with Redis Cluster cross-slot restrictions.

Reference: [Redis script parameterization](https://redis.io/docs/latest/develop/programmability/eval-intro/#script-parameterization).

### 4. P3 — Business outcomes still use unchecked string codes and a permissive fallback

Location: [Outcome.cs](src/PlayerService.Application/Models/Outcome.cs), line 3; [PlayerEndpoints.cs](src/PlayerService.Api/Endpoints/PlayerEndpoints.cs), lines 39–45.

An unknown or misspelled outcome code falls through to HTTP 409. This masks an internal contract error as a client business conflict. Also, `Outcome<T>` can represent success with a null value because success only depends on `Error` being null.

Recommendation: parse Lua error codes into a typed application error representation, map known outcomes explicitly, and reject unknown codes as internal protocol failures. Use success/failure factories that enforce a valid payload. Keep the Lua wire strings stable at the serialization boundary.

### 5. P3 — The script response protocol is assumed rather than validated

Location: [RedisPlayerStore.cs](src/PlayerService.Infrastructure.Redis/RedisPlayerStore.cs), lines 64–70.

The adapter assumes three result elements, valid element types, a replay value of 0/1, and a non-null deserialized payload. Null-forgiving operators do not enforce these assumptions. A future script/schema mismatch would fail through a cast, index, or JSON exception with little operation context. The enum also does not associate an operation with its expected generic result type.

Recommendation: centralize response validation and raise an explicit protocol exception containing the operation name but not the session token or receipt contents. Consider typed script descriptors only if the script collection grows; they are optional for the current five private call sites.

### 6. P3 — Test responses are not consistently disposed, and outage targeting is environment-dependent

Location: [ConcurrencyTests.cs](tests/PlayerService.Tests/ConcurrencyTests.cs), response batches throughout the file and the unavailable-Redis test around line 261.

Many `HttpResponseMessage` objects returned by Score/Gift/Race remain undisposed, including ignored mutation responses. This makes resource ownership unclear and can inflate memory consumption as the suite grows. The outage test assumes port 6398 is unused; a real local Redis on that port invalidates its setup.

Recommendation: dispose responses after all assertions, including batches in a finally block. Test transport failure through a controlled fixture or injected failing transport, with a separate real connectivity integration check if needed.

### 7. P3 — Load results do not separate cold-request failures from hot-request failures

Location: [load harness](tools/PlayerService.LoadHarness/Program.cs), lines 60–80.

Latency samples are separated into hot and cold groups, but status counts and throughput combine successful and failed requests across both groups. Fast hot-player conflicts can inflate apparent throughput, and the report cannot show whether cold reads were the requests failing. The recorded earlier sample had only HTTP 200 responses, so this does not invalidate that sample's status interpretation.

Recommendation: record statuses and successful throughput separately for each group, and label latency distributions as successful-only or all attempts. Retain total attempted throughput as a distinct measurement.

## Deliberate design choices, not newly discovered defects

- A single Redis primary cannot provide strict hot/cold latency isolation. This is already documented and measured.
- Authentication hardening, persistence, and rate limiting were explicitly outside the chosen assignment scope.
- Receipt retention and original score-response snapshots are documented product-policy choices.
- The Prometheus exporter is explicitly pinned to a beta version; changing to stable OTLP export changes the monitoring topology.
- Session-index correctness depends on service-exclusive keys and no independent eviction. Capacity planning remains necessary: TTL bounds retention time, not memory usage under arbitrary traffic.
- Merging the small domain into Application, sharing one multiplexer per API process, using operation-specific interfaces, and keeping gift changes in one Lua script are appropriate for this scope.

## Validation

The enum refactor passed all 16 real-Redis tests. A metric regression assertion additionally checks that `operation="Login"` and `operation="Score"` remain strings with their previous names. NuGet's direct/transitive vulnerability query reported no known vulnerable packages for all five solution projects against the current NuGet.org advisory data. This result is time- and source-dependent and does not assess Docker image vulnerabilities.

After resolving findings 1 and 3, the expanded suite passes **22 tests, with no failures or skips**. See `VERIFICATION.md` for the corrected coverage description and the limitation of the historical load measurement.
