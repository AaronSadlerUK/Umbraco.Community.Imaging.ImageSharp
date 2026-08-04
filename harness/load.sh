#!/usr/bin/env bash
# Simulates browsing the Umbraco media section page by page: every request is a distinct crop and
# resize of a distinct source image, so every request is a cache miss that decodes, crops, resizes
# and re-encodes a full-size image.
#
# usage: load.sh <base-url> <requests> <concurrency> <image-count> [seed]
set -u

BASE="${1:-http://localhost:8099}"
REQUESTS="${2:-200}"
CONCURRENCY="${3:-16}"
IMAGES="${4:-40}"
SEED="${5:-1}"

urls=$(awk -v n="$REQUESTS" -v imgs="$IMAGES" -v base="$BASE" -v seed="$SEED" '
BEGIN {
  srand(seed);
  for (i = 0; i < n; i++) {
    img   = int(rand() * imgs);
    w     = 200 + int(rand() * 1000);
    h     = 200 + int(rand() * 1000);
    l     = int(rand() * 200) / 1000;
    t     = int(rand() * 200) / 1000;
    r     = int(rand() * 200) / 1000;
    b     = int(rand() * 200) / 1000;
    printf "%s/img%03d.jpg?width=%d&height=%d&mode=crop&cc=%.3f,%.3f,%.3f,%.3f\n", base, img, w, h, l, t, r, b;
  }
}')

echo "$urls" | xargs -P "$CONCURRENCY" -I{} curl -s -o /dev/null -w '' "{}"
