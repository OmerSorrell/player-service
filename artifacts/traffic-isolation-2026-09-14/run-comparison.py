#!/usr/bin/env python3
"""Run isolated localhost comparisons. Starts/stops only child processes it owns."""
import json, os, pathlib, subprocess, time, urllib.request, sys, socket, hashlib, platform
raise SystemExit("Historical experiment: traffic protection has been removed. This runner cannot reproduce ON behavior and is blocked to avoid misleading results. Use summarize.py to analyze the saved JSON.")

root = pathlib.Path(__file__).resolve().parents[2]
out = pathlib.Path(__file__).resolve().parent
dotnet = str(root / '.dotnet/dotnet')
# 3 paired trials at 3,200 hot RPS; 3 paired trials at 12,800 hot RPS.
# Alternate ON/OFF ordering to reduce systematic warmup/order effects.
plan = [(batch, enabled) for batch in (32, 128) for order in ((False,True),(True,False),(False,True)) for enabled in order]
if '--pilot' in sys.argv: plan = [(32,False),(32,True)]
seconds = 10 if '--pilot' in sys.argv else 20
if '--pilot' not in sys.argv:
    tracked = ['appsettings.json','src/PlayerService.Api/Traffic/TrafficOptions.cs',
        'src/PlayerService.Api/Traffic/FairPlayerScheduler.cs','src/PlayerService.Api/Traffic/TrafficAdmission.cs',
        'bin/Release/net10.0/PlayerService.Api.dll','tools/PlayerService.LoadHarness/Program.cs',
        'tools/PlayerService.LoadHarness/bin/Release/net10.0/PlayerService.LoadHarness.dll']
    manifest = {'platform':platform.platform(),'architecture':platform.machine(),
        'redisVersion':subprocess.check_output(['/opt/homebrew/bin/redis-server','--version'],text=True).strip(),
        'config':json.loads((root/'appsettings.json').read_text()),
        'sha256':{name:hashlib.sha256((root/name).read_bytes()).hexdigest() for name in tracked},
        'notes':'Same Release binaries throughout. Runner overrides Redis connection/prefix, URL, Traffic.Enabled, and log level. Generator, API and Redis share the host.'}
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
for number, (batch, enabled) in enumerate(plan, 1):
    label = f'{"pilot" if "--pilot" in sys.argv else "run"}-{number:02d}-rps-{batch*100}-{str(enabled).lower()}'
    processes, logs = [], []
    try:
        # Refuse to run against any pre-existing listener on the dedicated ports.
        for port in (6397,5180,5181):
            with socket.socket() as probe:
                probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                probe.bind(('127.0.0.1', port))
        redis_log = open(out / f'{label}-redis.log', 'w'); logs.append(redis_log)
        redis = subprocess.Popen(['/opt/homebrew/bin/redis-server','--port','6397','--bind','127.0.0.1','--save','','--appendonly','no'], stdout=redis_log, stderr=subprocess.STDOUT)
        processes.append(redis)
        time.sleep(.3)
        if redis.poll() is not None: raise RuntimeError('Dedicated Redis failed to start; refusing to use another instance.')
        for port in (5180,5181):
            env = os.environ.copy()
            env.update({'ASPNETCORE_URLS':f'http://127.0.0.1:{port}', 'Urls':f'http://127.0.0.1:{port}',
                'Redis__ConnectionString':'127.0.0.1:6397,abortConnect=false,asyncTimeout=2000,connectTimeout=2000',
                'Redis__KeyPrefix':label+':','Traffic__Enabled':str(enabled).lower(),
                'Logging__LogLevel__Default':'Warning'})
            log = open(out / f'{label}-api-{port}.log','w'); logs.append(log)
            proc = subprocess.Popen([dotnet, str(root/'bin/Release/net10.0/PlayerService.Api.dll')], cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
            processes.append(proc)
            for _ in range(100):
                if proc.poll() is not None: raise RuntimeError('API exited; see logs.')
                try:
                    with urllib.request.urlopen(f'http://127.0.0.1:{port}/health/ready', timeout=.3) as r:
                        if r.status == 200: break
                except Exception: time.sleep(.1)
            else: raise RuntimeError('API not ready')
        print(f'Starting {label}: {seconds}s baseline + {seconds}s mixed', flush=True)
        result = out / f'{label}.json'
        with open(out/f'{label}-harness.log','w') as log:
            harness = subprocess.Popen([dotnet, str(root/'tools/PlayerService.LoadHarness/bin/Release/net10.0/PlayerService.LoadHarness.dll'),
                'http://127.0.0.1:5180,http://127.0.0.1:5181',str(seconds),str(batch),str(result)], cwd=root,stdout=log,stderr=subprocess.STDOUT)
            processes.append(harness)
            if harness.wait(timeout=240) != 0: raise RuntimeError('Harness failed; see log.')
        data=json.loads(result.read_text()); mixed=data['mixed']
        print(json.dumps({'run':label,'cold':mixed['cold'],'hot':mixed['hot'],'skippedHot':mixed['skippedHot'],'skippedCold':mixed['skippedCold']}),flush=True)
    finally:
        for process in reversed(processes):
            if process.poll() is None: process.terminate()
        for process in reversed(processes):
            try: process.wait(timeout=5)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
        for log in logs: log.close()
