#!/usr/bin/env bash
# Cost of ONE request, measured with VmHWM (peak RSS) so there is no sampling race. This is the
# measurement that shows what a single thumbnail actually costs, independent of concurrency.
#
# usage: single.sh <name> <image-tag> <url-query> [docker -e args...]
#   e.g. single.sh full-decode imaging-harness:imagesharp "width=300&height=300&mode=crop"
set -uo pipefail
cd "$(dirname "$0")"

NAME="$1"; IMAGE="$2"; QUERY="$3"; shift 3
ENVARGS=("$@")

PORT="${PORT:-8097}"
BASE="http://localhost:$PORT"
MEDIA_VOLUME="${MEDIA_VOLUME:-imaging-harness-media}"
CACHE_VOLUME="imaging-harness-cache-single"
CONTAINER="imaging-harness-single"

docker rm -f "$CONTAINER" >/dev/null 2>&1
docker volume rm "$CACHE_VOLUME" >/dev/null 2>&1
docker volume create "$CACHE_VOLUME" >/dev/null

docker run -d --name "$CONTAINER" --memory=3g --memory-swap=3g -p "$PORT":8080 \
  -v "$MEDIA_VOLUME":/media:ro -v "$CACHE_VOLUME":/cache \
  ${ENVARGS[@]+"${ENVARGS[@]}"} "$IMAGE" >/dev/null || exit 1

for _ in $(seq 1 90); do curl -sf "$BASE/health" >/dev/null && break; sleep 1; done

field() { grep -m1 "^$2=" <<<"$1" | cut -d= -f2-; }
hwm() { echo $(( $(field "$(curl -s "$BASE/stats")" procVmHwmBytes) / 1048576 )); }

BEFORE=$(hwm)
OUT=$(curl -s -o /dev/null -w '%{http_code} %{size_download}' "$BASE/img000.jpg?$QUERY")
AFTER=$(hwm)

printf '%-24s http=%s bytes=%s  peakBefore=%sMB peakAfter=%sMB  cost=%sMB\n' \
  "$NAME" ${OUT} "$BEFORE" "$AFTER" "$((AFTER-BEFORE))"

docker rm -f "$CONTAINER" >/dev/null 2>&1
