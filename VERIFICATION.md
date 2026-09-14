# Verification

## Runnable artifacts and monitoring

The Release API publishes successfully with `dotnet publish PlayerService.Api.csproj --configuration Release --no-restore --output artifacts/publish`. The load harness also builds in Release without warnings or errors.

Prometheus configuration passed `promtool check config`. Both API scrape targets (5080 and 5081) reported healthy, and Grafana's dashboard API confirmed the provisioned Player Service dashboard with four panels. Metric names and labels were checked against the API's live `/metrics` output; dashboard visual layout was not browser-tested.

## Integration suite

Latest post-removal verification on 2026-09-14: **22 passed, 0 failed, 0 skipped** using `dotnet test PlayerService.slnx --no-restore --verbosity minimal`. Release solution build also passed with zero warnings and errors.

Original run on 2026-09-12: 16 passed. Follow-up run on 2026-09-13: **22 passed, 0 failed, 0 skipped**, using `dotnet test PlayerService.slnx --no-restore --verbosity minimal` after explicit Redis key declarations and expanded concurrency coverage.

Historical admission-enabled run on 2026-09-14: **29 passed, 0 failed, 0 skipped**. The seven admission-specific tests and the admission implementation were subsequently removed at the user's request; rate limiting is excluded from the assignment. The original 22 correctness tests remain, including expanded concurrency coverage and explicit Redis key checks.

Tests use two independent API hosts and real Redis 8.10.1. They include 1,200 overlapping transfers with live conservation snapshots, concurrent score updates and duplicate requests, lost-response retries, numeric limits, expiry/renewal, authorization, wrong Redis key types, metrics, and unavailable-Redis 503 responses.

Correction to the original coverage claim: the former 1,200-transfer generator only covered six independent bidirectional pairs. The corrected workload covers all 132 directed relationships across 12 players plus seeded additional requests, asserts cycles and counterpart coverage, and verifies each player's expected balance and counters. Additional tests guard declared-key access and force session deletion, device replacement, session identity changes, and a competing login between discovery and Lua execution. Multi-device online behavior is also tested after one session expires.

Two issues found and corrected during verification:

- Initialize the singleton connection and warm script caches before serving requests. This removes cold-start contention during the first large burst.
- Use fail-fast Redis backlog behavior and observe HTTP cancellation while waiting for operations. An outage produces 503 rather than queuing disconnected commands until recovery. Already-submitted commands may still commit, so request IDs remain essential.

The renewal test shortens Redis leases directly before a successful request and checks that all three session indexes are renewed. This avoids relying on very short wall-clock sleeps on a laptop; the separate expiry test exercises actual Redis expiry.

## HTTP load sample

Run on 2026-09-11 against two local Kestrel processes, .NET SDK 10.0.401 and native Redis 8.10.1. Four cold-read workers; then the same four workers plus 32 gift workers targeting two hot players. Each phase lasted five seconds after warmup.

This is a historical measurement from before explicit-key discovery added a Redis round trip. It is not a current performance measurement of the revised implementation.

| Measurement | Cold reads only | Hot gifts plus cold reads |
| --- | ---: | ---: |
| Total throughput | 17,770 requests/s | 33,572 requests/s |
| Cold requests | 88,853 | 18,820 |
| Cold p50 | 0.202 ms | 0.951 ms |
| Cold p95 | 0.370 ms | 1.771 ms |
| Cold p99 | 0.492 ms | 2.216 ms |
| Hot gift requests | 0 | 149,067 |
| Hot p99 | — | 2.241 ms |
| Non-200 responses | 0 | 0 |

All 34,000 points were conserved. The original JSON is in `artifacts/load-results.json`; rerun with the command in README. This closed-loop localhost sample includes HTTP, serialization, metrics, and Redis, but does not measure production capacity, durability, or network failures.

Cold p99 increased about 4.5× under hot traffic. Absolute latency stayed low in this sample, but the result demonstrates interference rather than strict isolation. Rate limiting is excluded from the assignment, and the single-primary isolation limitation remains documented.

## Historical admission experiment

The [six-run OFF/ON comparison](artifacts/admission-ab-2026-09-14/REPORT.md) found median per-run cold p99 of 3.64 ms OFF versus 3.98 ms ON at approximately 3,200 hot requests/sec. ON rejected 97.34% of hot requests; neither setting rejected cold requests. This did not establish a cold-tail latency benefit. Raw results are retained as historical evidence, not as a claim that the removed admission feature is still available.

## Reverted fair-scheduling experiment — 2026-09-14

The later fair scheduler and shared player-budget experiment passed 28 tests before removal and completed 12 OFF/ON load trials. Its implementation, six traffic-specific tests, configuration, and middleware were subsequently reverted at the user's request. The original 22 correctness tests, original load harness, and endpoint cancellation behavior are restored.

The [historical report](artifacts/traffic-isolation-2026-09-14/REPORT.md) retains all results: cold p99 improved by about 1–2 ms, but ON rejected 97–99% of hot requests and raised successful hot p99 to approximately 150 ms. Cold median latency increased, while neither mode had any server/transport failures. All 19,192 measured cold requests succeeded and every trial conserved 34,000 points. This did not justify the cost or demonstrate failure prevention at the tested loads. The [README decision](README.md#hot-player-protection-experiment-and-final-decision) records why the simpler implementation was retained and what evidence would be needed to reconsider controls.

Post-revert verification: Release solution build passed with zero warnings/errors, and the restored suite passed **22 tests, 0 failures, 0 skips** against temporary Redis at `127.0.0.1:6397` (`TEST_REDIS`). The temporary Redis process was stopped afterward. The default Redis at port 6379 was unavailable; an initial temporary-instance attempt used port 6398, which conflicts with the existing outage test. Moving the temporary instance to 6397 resolved that test-environment conflict without changing test code. The [passing test log](artifacts/traffic-isolation-2026-09-14/revert-tests.log) is retained.
