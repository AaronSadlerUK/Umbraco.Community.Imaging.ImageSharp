#!/usr/bin/env bash
# Publishes both harnesses and builds their container images.
#   ./build.sh            -> imaging-harness:imagesharp and imaging-harness:imagesharp2
set -euo pipefail
cd "$(dirname "$0")"

publish() {
  local project="$1" out="$2"
  echo "== publishing $project"
  dotnet publish "src/$project/$project.csproj" -c Release -o "$out" --nologo
}

publish Harness.ImageSharp  out/imagesharp
publish Harness.ImageSharp2 out/imagesharp2

docker build --build-arg PUBLISH_DIR=out/imagesharp  -t imaging-harness:imagesharp  .
docker build --build-arg PUBLISH_DIR=out/imagesharp2 -t imaging-harness:imagesharp2 .

echo
echo "Built imaging-harness:imagesharp and imaging-harness:imagesharp2"
echo "Next: ./generate-media.sh   then   ./run.sh <name> <image> <limit> <requests> <concurrency>"
