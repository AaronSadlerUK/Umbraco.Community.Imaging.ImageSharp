#!/usr/bin/env bash
# Fills the shared media volume with large source JPEGs. Run once; the volume is reused by
# every subsequent run. Defaults to 40 images at 4000x3000, which encode to roughly 9 MB each.
set -euo pipefail
cd "$(dirname "$0")"

COUNT="${1:-40}"
WIDTH="${2:-4000}"
HEIGHT="${3:-3000}"
VOLUME="${MEDIA_VOLUME:-imaging-harness-media}"

docker volume create "$VOLUME" >/dev/null
docker run --rm -v "$VOLUME":/media imaging-harness:imagesharp generate "$COUNT" "$WIDTH" "$HEIGHT"

echo "media volume '$VOLUME' ready"
