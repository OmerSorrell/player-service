# Hot/cold traffic comparison — 2026-09-14

Historical fair-scheduler experiment; protection disabled (OFF) versus enabled (ON). The implementation has been reverted. The current service cannot reproduce ON behavior by setting Traffic.Enabled. The final decision was to retain the simpler implementation: a 1–2 ms cold-p99 reduction did not justify 97–99% hot rejections and approximately 150 ms hot p99, with no demonstrated failure-prevention benefit. All 12 final runs used the same Release API and load-harness binaries. The [manifest](manifest.json) records hashes and configuration. Superseded pilot runs are stored separately and are not included.

## Successful-request latency and failure rates

Latency entries are **medians of three per-run percentiles**, in milliseconds; they are not pooled percentiles. Failure/rejection rates aggregate all mixed-phase attempts in each group. Non-success includes intentional 429 responses; server/transport failures mean HTTP 5xx or a transport timeout/error.

| Offered hot RPS | Protection | Cold p50 | Cold p95 | Cold p99 | Hot p50 | Hot p95 | Hot p99 | Cold non-success | Hot non-success / 429 | Cold / hot server or transport failure |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 3,200 | OFF | 1.041 | 2.213 | 4.465 | 1.700 | 2.760 | 3.815 | 0.000% | 0.000% / 0.000% | 0.000% / 0.000% |
| 3,200 | ON | 1.359 | 2.118 | 3.406 | 145.443 | 150.913 | 151.308 | 0.000% | 97.459% / 97.459% | 0.000% / 0.000% |
| 12,800 | OFF | 1.389 | 2.376 | 5.893 | 3.170 | 4.453 | 7.248 | 0.000% | 0.000% / 0.000% | 0.000% / 0.000% |
| 12,800 | ON | 1.890 | 3.024 | 3.931 | 146.677 | 152.194 | 152.711 | 0.000% | 99.365% / 99.365% | 0.000% / 0.000% |

## Baseline, throughput, and rejection latency

| Offered hot RPS | Protection | Cold-only p99 | Mixed cold p99 | Median per-run mixed / baseline cold p99 | Hot successful RPS | Hot 429 p50 / p99 |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 3,200 | OFF | 3.303 | 4.465 | 1.37× | 3198.40 | — |
| 3,200 | ON | 2.954 | 3.406 | 1.04× | 81.30 | 0.317 / 151.318 |
| 12,800 | OFF | 2.638 | 5.893 | 2.47× | 12800.00 | — |
| 12,800 | ON | 3.536 | 3.931 | 1.12× | 81.20 | 0.518 / 123.495 |

The rate setting is 40 admitted attempts/sec/player with an 8-request burst, shared across both API instances. There are two hot players, so long-run aggregate hot capacity is 80/sec. Counts divided by the nominal 20-second offer window can slightly exceed 80 because the initial bucket contains burst tokens and queued requests drain after the final offered tick. Each queue holds up to 8 requests, with a 150 ms deadline; slow successful hot responses are intentional queueing. Queue-deadline checks and HTTP processing can push observed response latency slightly beyond 150 ms. Most overflow rejections return much sooner.

## Per-run evidence

| Run | Cold baseline p99 | Cold mixed p99 | Hot successful p99 | Cold 200 / 429 / other | Hot 200 / 429 / other | Skipped cold / hot launches |
| --- | ---: | ---: | ---: | --- | --- | --- |
| [run-01-rps-3200-false](run-01-rps-3200-false.json) | 2.455 | 3.355 | 3.815 | 1600 / 0 / 0 | 64000 / 0 / 0 | 0 / 0 |
| [run-02-rps-3200-true](run-02-rps-3200-true.json) | 5.863 | 3.664 | 151.631 | 1600 / 0 / 0 | 1626 / 62374 / 0 | 0 / 0 |
| [run-03-rps-3200-true](run-03-rps-3200-true.json) | 2.954 | 3.080 | 151.268 | 1600 / 0 / 0 | 1626 / 62342 / 0 | 0 / 32 |
| [run-04-rps-3200-false](run-04-rps-3200-false.json) | 3.574 | 7.169 | 6.309 | 1600 / 0 / 0 | 63968 / 0 / 0 | 0 / 32 |
| [run-05-rps-3200-false](run-05-rps-3200-false.json) | 3.303 | 4.465 | 3.481 | 1596 / 0 / 0 | 63936 / 0 / 0 | 4 / 64 |
| [run-06-rps-3200-true](run-06-rps-3200-true.json) | 2.822 | 3.406 | 151.308 | 1600 / 0 / 0 | 1626 / 62342 / 0 | 0 / 32 |
| [run-07-rps-12800-false](run-07-rps-12800-false.json) | 2.381 | 5.893 | 7.248 | 1596 / 0 / 0 | 255744 / 0 / 0 | 4 / 256 |
| [run-08-rps-12800-true](run-08-rps-12800-true.json) | 3.536 | 3.828 | 152.711 | 1600 / 0 / 0 | 1624 / 254376 / 0 | 0 / 0 |
| [run-09-rps-12800-true](run-09-rps-12800-true.json) | 3.599 | 4.013 | 152.390 | 1600 / 0 / 0 | 1626 / 254374 / 0 | 0 / 0 |
| [run-10-rps-12800-false](run-10-rps-12800-false.json) | 2.638 | 9.351 | 9.194 | 1600 / 0 / 0 | 256000 / 0 / 0 | 0 / 0 |
| [run-11-rps-12800-false](run-11-rps-12800-false.json) | 3.038 | 3.875 | 5.807 | 1600 / 0 / 0 | 256000 / 0 / 0 | 0 / 0 |
| [run-12-rps-12800-true](run-12-rps-12800-true.json) | 2.923 | 3.931 | 152.724 | 1600 / 0 / 0 | 1624 / 254376 / 0 | 0 / 0 |

## Interpretation

- At 3,200 offered hot requests/sec, median per-run cold p99 changed from **4.465 ms OFF to 3.406 ms ON** (-23.7%).
- At 12,800 offered hot requests/sec, median per-run cold p99 changed from **5.893 ms OFF to 3.931 ms ON** (-33.3%).
- All mixed phases together: cold statuses `{'200': 19192}`; hot statuses `{'200': 969400, '429': 950184}`.
- All 12 final runs conserved **34,000 points**, with no negative balance at final verification.
- This is evidence of the measured tradeoff, not a zero-interference guarantee or proof of a production p99 SLO. Successful cold requests still share API CPU, networking and a Redis primary with hot traffic. The added rate-check round trip can increase baseline latency.
- The benchmark exercises two hot players with cached identities and cold stats reads. It does not prove protection against many hot players, unknown-token/login floods, hot-to-cold gifts, or infrastructure failure. Reserved-slot behavior and persistent-backlog fairness were checked by six traffic-specific tests, which were subsequently removed with the implementation.
- Three runs per setting and 1,600 scheduled cold requests per mixed phase provide a local diagnostic. Differences in p99 may reflect machine scheduling and other shared-machine noise; no statistical significance claim is made.

## Historical method and saved-result analysis

- Dedicated temporary Redis at 127.0.0.1:6397, no persistence. Two Kestrel processes at ports 5180/5181. Fresh processes and Redis for every trial; identical traffic settings except `Traffic.Enabled`.
- Three OFF/ON pairs at each load; order OFF/ON, ON/OFF, OFF/ON. The manifest records the effective default limits. During the experiment, the original correctness suite disabled admission and six additional traffic tests enabled it. Those extra tests and the admission override have now been removed.
- 34 players start with 1,000 points each. Both hot players send one-point gifts to each other through both API instances. Four cold players each request stats 20 times/sec. All session identities are warmed on both APIs before measurement.
- Three-second mixed warmup, two-second recovery, twenty-second cold-only phase, two-second recovery, twenty-second mixed phase, then final conservation verification. There is no client retry/backoff during measured phases.
- Open-loop hot batches every 10 ms, with four cold requests every fifth tick. Responses do not gate subsequent launches. The generator reports ticks more than 10 ms late and skipped launches; an 8,192 in-flight cap prevents unbounded generator memory. Latency starts when launched and includes connection waiting and full HTTP body download. Skipped scheduled requests are not HTTP failures, so inspect skipped counts alongside rates.
- Generator, API processes, and Redis share one Mac. No artificial Redis delays were injected and the primary was not claimed to be saturated. Do not extrapolate these rates to production.

```sh
python3 artifacts/traffic-isolation-2026-09-14/summarize.py
```

The command above regenerates this report from saved JSON; it does not run a new load test. The historical comparison runner is now blocked because the protection implementation has been removed. Its original procedure required the local SDK under `.dotnet/` and Homebrew Redis at `/opt/homebrew/bin/redis-server`; adjust those paths on other systems. The archived open-loop harness source is in `LoadHarness.Program.cs`; the active project harness has been restored to its original version. Raw JSON and API/Redis/harness logs are kept in this directory. The [README decision](../../README.md#hot-player-protection-experiment-and-final-decision) records the revert rationale and retains the future production recommendation.
