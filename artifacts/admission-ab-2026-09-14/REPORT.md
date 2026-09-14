# Admission OFF versus ON — 2026-09-14

Historical experiment: admission controls were subsequently removed and excluded from the assignment. The saved ON results describe the earlier build. Running the saved harness against the current build cannot reproduce ON behavior merely by setting `Admission:Enabled`.

At this tested load, admission controls did **not** improve cold-player p99. The median of the three per-run p99 values was 3.6401 ms OFF and 3.9772 ms ON (9.3% higher). Cold p95 improved, but tails varied; this is not evidence of a statistically significant regression or improvement.

| Mixed-load measurement | OFF | ON |
| --- | ---: | ---: |
| Median per-run cold p50 | 2.1381 ms | 2.0608 ms |
| Median per-run cold p95 | 3.1113 ms | 2.6724 ms |
| Median per-run cold p99 | 3.6401 ms | 3.9772 ms |
| Range of per-run cold p99 | 3.5566–4.9640 ms | 3.8696–8.1087 ms |
| Median per-run hot success p99 | 3.3872 ms | 4.8445 ms |
| Successful hot requests/sec, aggregate | 3,199.76 | 85.06 |
| Successful cold requests, all three mixed phases | 1,800 | 1,800 |
| Cold rejections/errors | 0 | 0 |
| Successful hot requests | 287,989 | 7,656 |
| Hot 429 responses | 0 | 280,243 (97.34%) |

All six runs conserved all 34,000 points. No mixed-phase statuses other than 200 and 429 occurred. Limits ON has an approximately 80 requests/sec aggregate steady-state player budget for two hot players across two replicas, plus bursts; observed successful throughput is consistent with that budget.

## Method

- Identical current Release build, default limits, toggling only `Admission:Enabled` between runs. OFF also bypasses the extra admission authentication precheck; this measures the complete feature's cost/benefit, not limiter primitives in isolation.
- Two temporary API processes on 5082/5083; a fresh standalone Redis on 6381 for every run, memory-only, noeviction, 512 MB maximum. Existing service data was not used or modified.
- Order: OFF, ON, ON, OFF, OFF, ON. Both processes and Redis restarted for each run.
- 34 unique players provisioned per run. Four cold callers read four players, each targeting one request per 200 ms (20/sec combined).
- 32 hot callers send one-point gifts between two players, each targeting one request per 10 ms (3,200/sec combined), distributed across both API instances. They do not back off on 429 during measurement.
- One in-flight request per worker; timer ticks coalesce under delays, so offered traffic is approximately fixed, not an unrestricted open-loop generator. Actual mixed-phase hot totals were 287,989 OFF and 287,899 ON across 90 seconds each.
- Five-second mixed warmup, three-second recovery, ten-second cold-only baseline, three-second recovery, thirty-second mixed measurement. Setup and final conservation reads retry 429 only.
- Percentiles measure HTTP 200 response latency only, including body download. Summary values above are medians of per-run percentiles, not pooled percentiles. Each mixed phase has 600 cold samples; p99 is sensitive to a small number of outliers.
- Cold-only p99 per run: OFF 2.8862, 3.3391, 4.4610 ms; ON 4.3637, 4.2346, 3.3192 ms. Mixed p99: OFF 4.9640, 3.6401, 3.5566 ms; ON 3.9772, 8.1087, 3.8696 ms.

## Interpretation and limits

The service successfully handles this offered load without admission controls. Limits substantially cap admitted hot work, but rejected authenticated requests still perform Redis identity discovery and admission authentication. Those costs, and the shared Redis/CPU, remain. That is a plausible explanation for the lack of cold-p99 benefit, not a measured attribution of the latency difference.

These localhost runs do not establish saturation capacity, production latency, or strict isolation. More severe loads may change the outcome; an arrival-rate sweep would be needed to test that. Do not compare these results directly to earlier unpaced load measurements.

Raw run JSON and the diagnostic harness are saved alongside this report. `run.sh` records the executed orchestration and its original absolute temporary paths; adjust paths and verify ports are unused before replaying it. All temporary API and Redis processes were stopped after the test. No application implementation was changed.
