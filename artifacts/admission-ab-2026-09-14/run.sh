#!/bin/bash
set -euo pipefail
cd /Users/omersorrell/Projects/player-service
out=/private/tmp/player-ab.DTz9cF
redis_pid= api_a= api_b=
cleanup() {
  for pid in "$api_a" "$api_b" "$redis_pid"; do
    if [ -n "$pid" ]; then kill -TERM "$pid" 2>/dev/null || true; fi
  done
  wait 2>/dev/null || true
  redis_pid= api_a= api_b=
}
trap cleanup EXIT
dotnet build PlayerService.slnx -c Release --no-restore --verbosity quiet
dotnet build "$out/PlayerService.LoadHarness.csproj" -c Release --verbosity quiet
run=0
for enabled in false true true false false true; do
  run=$((run+1))
  echo "START run $run admission=$enabled"
  redis-server --bind 127.0.0.1 --port 6381 --save '' --appendonly no --maxmemory 512mb --maxmemory-policy noeviction > "$out/redis-$run.log" 2>&1 &
  redis_pid=$!
  sleep 1
  for port in 5082 5083; do
    dotnet bin/Release/net10.0/PlayerService.Api.dll --urls "http://127.0.0.1:$port" --Redis:ConnectionString '127.0.0.1:6381,abortConnect=false,asyncTimeout=2000,connectTimeout=2000' --Admission:Enabled "$enabled" > "$out/api-$run-$port.log" 2>&1 &
    if [ "$port" = 5082 ]; then api_a=$!; else api_b=$!; fi
  done
  for port in 5082 5083; do
    for attempt in {1..30}; do
      if curl -fsS "http://127.0.0.1:$port/health/ready" >/dev/null; then break; fi
      sleep 1
    done
    curl -fsS "http://127.0.0.1:$port/health/ready" >/dev/null
  done
  dotnet "$out/bin/Release/net10.0/PlayerService.LoadHarness.dll" http://127.0.0.1:5082,http://127.0.0.1:5083 30 32 "$out/run-$run-$enabled.json" > "$out/run-$run-$enabled.stdout"
  echo "DONE run $run admission=$enabled"
  cleanup
done
