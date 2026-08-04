#!/usr/bin/env bash
# Runs the harness in a memory-limited container, drives bursts of cache-missing crop requests at
# it, and reports memory after each burst and while idle.
#
# usage: run.sh <name> <image-tag> <memory-limit> <requests> <concurrency> [docker -e args...]
#   e.g. run.sh baseline imaging-harness:imagesharp 512m 200 16
#        run.sh fixed    imaging-harness:imagesharp 512m 200 16 -e HARNESS_FIX=1
#
# env: IDLE (seconds to observe after each burst, default 30), BURSTS (default 2)
set -uo pipefail
cd "$(dirname "$0")"

NAME="$1"; IMAGE="$2"; MEM="$3"; REQUESTS="$4"; CONC="$5"; shift 5
ENVARGS=("$@")

PORT="${PORT:-8099}"
BASE="http://localhost:$PORT"
MEDIA_VOLUME="${MEDIA_VOLUME:-imaging-harness-media}"
CACHE_VOLUME="imaging-harness-cache"
CONTAINER="imaging-harness-run"
RESULTS="results/$NAME"

rm -rf "$RESULTS"; mkdir -p "$RESULTS"

docker rm -f "$CONTAINER" >/dev/null 2>&1
docker volume rm "$CACHE_VOLUME" >/dev/null 2>&1
docker volume create "$CACHE_VOLUME" >/dev/null

echo "== $NAME : image=$IMAGE mem=$MEM requests=$REQUESTS concurrency=$CONC env=${ENVARGS[*]:-none}"

docker run -d --name "$CONTAINER" \
  --memory="$MEM" --memory-swap="$MEM" \
  -p "$PORT":8080 \
  -v "$MEDIA_VOLUME":/media:ro -v "$CACHE_VOLUME":/cache \
  ${ENVARGS[@]+"${ENVARGS[@]}"} "$IMAGE" >/dev/null || exit 1

for _ in $(seq 1 90); do
  curl -sf "$BASE/health" >/dev/null && break
  sleep 1
done

field() { grep -m1 "^$2=" <<<"$1" | cut -d= -f2-; }

sample() { # $1 = endpoint, $2 = label
  local body
  body=$(curl -s --max-time 30 "$BASE/$1")
  if [[ -z "$body" ]]; then
    echo -e "$2\tDEAD\t\t\t\t\t\t"
    return 1
  fi
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$2" \
    "$(( $(field "$body" procVmRssBytes) / 1048576 ))" \
    "$(( $(field "$body" procVmHwmBytes) / 1048576 ))" \
    "$(( $(field "$body" cgroupCurrentBytes) / 1048576 ))" \
    "$(( $(field "$body" gcHeapSizeBytes) / 1048576 ))" \
    "$(( $(field "$body" gcCommittedBytes) / 1048576 ))" \
    "$(field "$body" imageSharpOutstandingHandles)" \
    "$(field "$body" gcGen2Count)" \
    "$(field "$body" gcServerMode)"
}

HEADER=$'phase\trssMB\tpeakRssMB\tcgroupMB\tgcHeapMB\tgcCommitMB\tisHandles\tgen2\tserverGC'
{
  echo "$HEADER"
  sample "stats?label=baseline" baseline
} | tee "$RESULTS/samples.tsv"

IDLE="${IDLE:-30}"
BURSTS="${BURSTS:-2}"

for burst in $(seq 1 "$BURSTS"); do
  # A different seed per burst, so every burst is a fresh set of cache misses.
  bash ./load.sh "$BASE" "$REQUESTS" "$CONC" 40 "$burst" &
  LOAD_PID=$!

  i=0
  while kill -0 $LOAD_PID 2>/dev/null; do
    sleep 5
    i=$((i+1))
    sample "stats?label=load" "b${burst}-load+$((i*5))s" | tee -a "$RESULTS/samples.tsv" || break
  done
  wait $LOAD_PID

  # Idle observation: does the process give the memory back on its own?
  for j in $(seq 1 $((IDLE/15))); do
    sleep 15
    sample "stats?label=idle" "b${burst}-idle+$((j*15))s" | tee -a "$RESULTS/samples.tsv" || break
  done
done

{
  sample gc after-gc
  sample malloc-trim after-malloc-trim
} | tee -a "$RESULTS/samples.tsv"

STATE=$(docker inspect -f '{{.State.Status}} oomkilled={{.State.OOMKilled}} exit={{.State.ExitCode}}' "$CONTAINER" 2>/dev/null)
echo "container: $STATE" | tee -a "$RESULTS/samples.tsv"
docker logs "$CONTAINER" > "$RESULTS/container.log" 2>&1
docker rm -f "$CONTAINER" >/dev/null 2>&1
