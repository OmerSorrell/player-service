#!/usr/bin/env python3
import collections, json, pathlib, statistics
root = pathlib.Path(__file__).resolve().parent
files = sorted(root.glob('run-*.json'))
assert len(files) == 12, f'Expected 12 completed trials; found {len(files)}'
data = [(p, json.loads(p.read_text())) for p in files]
groups = collections.defaultdict(list)
for path, run in data:
    groups[(run['targetHotRequestsPerSecond'], path.stem.endswith('true'))].append(run)
assert all(len(runs) == 3 for runs in groups.values())
def med(runs, *keys):
    values=[]
    for run in runs:
        for key in keys: run=run[key]
        values.append(run)
    return statistics.median(values)
def statuses(runs, cohort):
    result=collections.Counter()
    for run in runs: result.update(run['mixed'][cohort]['statuses'])
    return result
def pct(status, predicate):
    return 100 * sum(n for code,n in status.items() if predicate(int(code))) / sum(status.values())
lines=['# Hot/cold traffic comparison — 2026-09-14','',
'Historical fair-scheduler experiment; protection disabled (OFF) versus enabled (ON). The implementation has been reverted. The current service cannot reproduce ON behavior by setting Traffic.Enabled. The final decision was to retain the simpler implementation: a 1–2 ms cold-p99 reduction did not justify 97–99% hot rejections and approximately 150 ms hot p99, with no demonstrated failure-prevention benefit. All 12 final runs used the same Release API and load-harness binaries. The [manifest](manifest.json) records hashes and configuration. Superseded pilot runs are stored separately and are not included.','',
'## Successful-request latency and failure rates','',
'Latency entries are **medians of three per-run percentiles**, in milliseconds; they are not pooled percentiles. Failure/rejection rates aggregate all mixed-phase attempts in each group. Non-success includes intentional 429 responses; server/transport failures mean HTTP 5xx or a transport timeout/error.','',
'| Offered hot RPS | Protection | Cold p50 | Cold p95 | Cold p99 | Hot p50 | Hot p95 | Hot p99 | Cold non-success | Hot non-success / 429 | Cold / hot server or transport failure |',
'| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |']
summary=[]
for (rate,enabled), runs in sorted(groups.items()):
    cold,hot=statuses(runs,'cold'),statuses(runs,'hot')
    values=[med(runs,'mixed',cohort,'success',p+'Ms') for cohort in ('cold','hot') for p in ('p50','p95','p99')]
    cold_fail=pct(cold,lambda x:x!=200); hot_fail=pct(hot,lambda x:x!=200)
    cold_server=pct(cold,lambda x:x==0 or x>=500); hot_server=pct(hot,lambda x:x==0 or x>=500)
    lines.append(f"| {rate:,} | {'ON' if enabled else 'OFF'} | "+' | '.join(f'{v:.3f}' for v in values)+f' | {cold_fail:.3f}% | {hot_fail:.3f}% / {pct(hot,lambda x:x==429):.3f}% | {cold_server:.3f}% / {hot_server:.3f}% |')
    summary.append({'offeredHotRps':rate,'enabled':enabled,'coldP99Ms':values[2],'hotP99Ms':values[5],'coldStatuses':dict(cold),'hotStatuses':dict(hot),'coldNonSuccessPercent':cold_fail,'hotNonSuccessPercent':hot_fail})
lines += ['', '## Baseline, throughput, and rejection latency','',
'| Offered hot RPS | Protection | Cold-only p99 | Mixed cold p99 | Median per-run mixed / baseline cold p99 | Hot successful RPS | Hot 429 p50 / p99 |',
'| ---: | --- | ---: | ---: | ---: | ---: | ---: |']
for (rate,enabled),runs in sorted(groups.items()):
    ratio=statistics.median(r['mixed']['cold']['success']['p99Ms']/r['baseline']['cold']['success']['p99Ms'] for r in runs)
    rejects='—' if not enabled else f"{med(runs,'mixed','hot','rejected','p50Ms'):.3f} / {med(runs,'mixed','hot','rejected','p99Ms'):.3f}"
    lines.append(f"| {rate:,} | {'ON' if enabled else 'OFF'} | {med(runs,'baseline','cold','success','p99Ms'):.3f} | {med(runs,'mixed','cold','success','p99Ms'):.3f} | {ratio:.2f}× | {med(runs,'mixed','hot','successfulRequestsPerSecond'):.2f} | {rejects} |")
lines += ['','The rate setting is 40 admitted attempts/sec/player with an 8-request burst, shared across both API instances. There are two hot players, so long-run aggregate hot capacity is 80/sec. Counts divided by the nominal 20-second offer window can slightly exceed 80 because the initial bucket contains burst tokens and queued requests drain after the final offered tick. Each queue holds up to 8 requests, with a 150 ms deadline; slow successful hot responses are intentional queueing. Queue-deadline checks and HTTP processing can push observed response latency slightly beyond 150 ms. Most overflow rejections return much sooner.','',
'## Per-run evidence','',
'| Run | Cold baseline p99 | Cold mixed p99 | Hot successful p99 | Cold 200 / 429 / other | Hot 200 / 429 / other | Skipped cold / hot launches |',
'| --- | ---: | ---: | ---: | --- | --- | --- |']
for path,run in data:
    cold=run['mixed']['cold']['statuses'];hot=run['mixed']['hot']['statuses']
    def counts(s): return f"{s.get('200',0)} / {s.get('429',0)} / {sum(v for k,v in s.items() if k not in ('200','429'))}"
    lines.append(f"| [{path.stem}]({path.name}) | {run['baseline']['cold']['success']['p99Ms']:.3f} | {run['mixed']['cold']['success']['p99Ms']:.3f} | {run['mixed']['hot']['success']['p99Ms']:.3f} | {counts(cold)} | {counts(hot)} | {run['mixed']['skippedCold']} / {run['mixed']['skippedHot']} |")
lines += ['','## Interpretation','']
for rate in (3200,12800):
    off=med(groups[(rate,False)],'mixed','cold','success','p99Ms');on=med(groups[(rate,True)],'mixed','cold','success','p99Ms')
    lines.append(f'- At {rate:,} offered hot requests/sec, median per-run cold p99 changed from **{off:.3f} ms OFF to {on:.3f} ms ON** ({(on/off-1)*100:+.1f}%).')
allcold=collections.Counter();allhot=collections.Counter()
for _,run in data: allcold.update(run['mixed']['cold']['statuses']);allhot.update(run['mixed']['hot']['statuses'])
lines.append(f"- All mixed phases together: cold statuses `{dict(allcold)}`; hot statuses `{dict(allhot)}`.")
assert all(r['conservedPoints']==34000 for _,r in data)
lines += ['- All 12 final runs conserved **34,000 points**, with no negative balance at final verification.',
'- This is evidence of the measured tradeoff, not a zero-interference guarantee or proof of a production p99 SLO. Successful cold requests still share API CPU, networking and a Redis primary with hot traffic. The added rate-check round trip can increase baseline latency.',
'- The benchmark exercises two hot players with cached identities and cold stats reads. It does not prove protection against many hot players, unknown-token/login floods, hot-to-cold gifts, or infrastructure failure. Reserved-slot behavior and persistent-backlog fairness were checked by six traffic-specific tests, which were subsequently removed with the implementation.',
'- Three runs per setting and 1,600 scheduled cold requests per mixed phase provide a local diagnostic. Differences in p99 may reflect machine scheduling and other shared-machine noise; no statistical significance claim is made.',
'', '## Historical method and saved-result analysis','',
'- Dedicated temporary Redis at 127.0.0.1:6397, no persistence. Two Kestrel processes at ports 5180/5181. Fresh processes and Redis for every trial; identical traffic settings except `Traffic.Enabled`.',
'- Three OFF/ON pairs at each load; order OFF/ON, ON/OFF, OFF/ON. The manifest records the effective default limits. During the experiment, the original correctness suite disabled admission and six additional traffic tests enabled it. Those extra tests and the admission override have now been removed.',
'- 34 players start with 1,000 points each. Both hot players send one-point gifts to each other through both API instances. Four cold players each request stats 20 times/sec. All session identities are warmed on both APIs before measurement.',
'- Three-second mixed warmup, two-second recovery, twenty-second cold-only phase, two-second recovery, twenty-second mixed phase, then final conservation verification. There is no client retry/backoff during measured phases.',
'- Open-loop hot batches every 10 ms, with four cold requests every fifth tick. Responses do not gate subsequent launches. The generator reports ticks more than 10 ms late and skipped launches; an 8,192 in-flight cap prevents unbounded generator memory. Latency starts when launched and includes connection waiting and full HTTP body download. Skipped scheduled requests are not HTTP failures, so inspect skipped counts alongside rates.',
'- Generator, API processes, and Redis share one Mac. No artificial Redis delays were injected and the primary was not claimed to be saturated. Do not extrapolate these rates to production.',
'', '```sh', 'python3 artifacts/traffic-isolation-2026-09-14/summarize.py', '```','',
'The command above regenerates this report from saved JSON; it does not run a new load test. The historical comparison runner is now blocked because the protection implementation has been removed. Its original procedure required the local SDK under `.dotnet/` and Homebrew Redis at `/opt/homebrew/bin/redis-server`; adjust those paths on other systems. The archived open-loop harness source is in `LoadHarness.Program.cs`; the active project harness has been restored to its original version. Raw JSON and API/Redis/harness logs are kept in this directory. The [README decision](../../README.md#hot-player-protection-experiment-and-final-decision) records the revert rationale and retains the future production recommendation.','']
(root/'REPORT.md').write_text('\n'.join(lines))
(root/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps(summary,indent=2))
