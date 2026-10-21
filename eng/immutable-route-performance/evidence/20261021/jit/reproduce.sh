#!/usr/bin/env bash
set -euo pipefail
: "${DOTNET:=/workspace/scratch/9e5903e2fbfb/.sharplink-dotnet/dotnet}"
: "${BASELINE:=/workspace/scratch/9e5903e2fbfb/sharplink-immutable-baseline}"
: "${CANDIDATE:=/workspace/scratch/9e5903e2fbfb/sharplink-immutable-route}"
: "${OUTPUT:=/tmp/immutable-route-jit-reproduction}"
mkdir -p "$OUTPUT"
export DOTNET_TieredCompilation=0
export DOTNET_JitDisasm='*InvokeUnaryCoreAsync* *MoveNext*'
export DOTNET_JitDisasmAssemblies=SharpLink.Client
for kind in baseline candidate; do
  if [[ "$kind" == baseline ]]; then root="$BASELINE"; mode=ordinary; else root="$CANDIDATE"; mode=prepared; fi
  export DOTNET_JitStdOutFile="$OUTPUT/$kind.asm"
  timeout 150 "$DOTNET" "$root/eng/immutable-route-performance/bin/Release/net10.0/SharpLink.UnitTests.dll" \
    --mode "$mode" --label "jit-$kind" --warmup 100 --unary 100 --oneway 1000 --timeout-seconds 120 \
    > "$OUTPUT/$kind.json" 2> "$OUTPUT/$kind.stderr.txt"
done
unset DOTNET_JitDisasm DOTNET_JitDisasmAssemblies DOTNET_JitStdOutFile
# Archived as text so the parent performance project does not compile this probe.
HERE="$(cd -- "$(dirname -- "$0")" && pwd)"
: "${LAYOUT:=$OUTPUT/layout}"
mkdir -p "$LAYOUT"
cp "$HERE/layout-Program.cs.txt" "$LAYOUT/Program.cs"
cp "$HERE/layout.csproj.txt" "$LAYOUT/layout.csproj"
"$DOTNET" build "$LAYOUT/layout.csproj" -c Release -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
for kind in baseline candidate; do
  if [[ "$kind" == baseline ]]; then root="$BASELINE"; else root="$CANDIDATE"; fi
  "$DOTNET" "$LAYOUT/bin/Release/net10.0/layout.dll" "$root/eng/immutable-route-performance/bin/Release/net10.0" > "$OUTPUT/$kind-layout.json"
done
