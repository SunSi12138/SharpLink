#!/usr/bin/env bash
set -euo pipefail
BASE=5560787e64875b7b86389af7cbefe25df7277bd9
ROOT="$PWD/artifacts/issue736"
mkdir -p "$ROOT/builds" "$ROOT/logs"
python3 eng/perf/issue736/test_summarize.py > "$ROOT/logs/validator-tests.log"
printf '%s\n' "$BASE" > "$ROOT/base.txt"
git rev-parse HEAD > "$ROOT/candidate.txt"
dotnet --info > "$ROOT/dotnet-info.txt"
lscpu > "$ROOT/cpu.txt"
BASE_DIR="${RUNNER_TEMP:-/tmp}/issue736-baseline"
LAZY_DIR="${RUNNER_TEMP:-/tmp}/issue736-lazy"
git fetch --no-tags --depth=1 origin "$BASE"
git worktree add --detach "$BASE_DIR" "$BASE"
git worktree add --detach "$LAZY_DIR" "$BASE"
for target in "$BASE_DIR" "$LAZY_DIR"; do mkdir -p "$target/eng/perf/issue736"; cp eng/perf/issue736/{Probe.csproj,Program.cs} "$target/eng/perf/issue736/"; done
python3 eng/perf/issue736/make_lazy_control.py "$LAZY_DIR"
git -C "$LAZY_DIR" diff > "$ROOT/lazy-control.patch"
for arm in baseline candidate lazy; do
  if [[ "$arm" == baseline ]]; then source="$BASE_DIR"; elif [[ "$arm" == lazy ]]; then source="$LAZY_DIR"; else source="$PWD"; fi
  sha256sum "$source/src/SharpLink.Runtime/StreamManager.cs" "$source/eng/perf/issue736/Program.cs" >> "$ROOT/source-hashes.txt"
  if [[ -f "$source/src/SharpLink.Runtime/SmallStreamRouteTable.cs" ]]; then sha256sum "$source/src/SharpLink.Runtime/SmallStreamRouteTable.cs" >> "$ROOT/source-hashes.txt"; fi
  for kind in jit native; do
    if [[ "$kind" == native ]]; then aot=true; selfcontained=true; else aot=false; selfcontained=false; fi
    dotnet publish "$source/eng/perf/issue736/Probe.csproj" -c Release -r linux-x64 -p:PublishAot="$aot" --self-contained "$selfcontained" -o "$ROOT/builds/$kind-$arm" -v minimal > "$ROOT/logs/build-$kind-$arm.log" 2>&1 || { cat "$ROOT/logs/build-$kind-$arm.log"; exit 1; }
    if [[ "$kind" == native ]]; then sha256sum "$ROOT/builds/$kind-$arm/SharpLink.UnitTests" >> "$ROOT/binary-hashes.txt"; else sha256sum "$ROOT/builds/$kind-$arm/SharpLink.Runtime.dll" "$ROOT/builds/$kind-$arm/SharpLink.UnitTests.dll" >> "$ROOT/binary-hashes.txt"; fi
  done
done
CPU=$(python3 -c 'import os; print(min(os.sched_getaffinity(0)))')
echo "$CPU" > "$ROOT/affinity.txt"
python3 - "$ROOT" <<'PYVERIFY'
import pathlib, sys, hashlib
root=pathlib.Path(sys.argv[1])
source_lines=(root/'source-hashes.txt').read_text().splitlines()
probe=[line.split()[0] for line in source_lines if line.endswith('/Program.cs')]
assert len(probe)==3 and len(set(probe))==1, 'Harness source differs across arms'
for kind, name in [('jit','SharpLink.Runtime.dll'),('native','SharpLink.UnitTests')]:
    digests=[hashlib.sha256((root/'builds'/f'{kind}-{arm}'/name).read_bytes()).hexdigest() for arm in ['baseline','candidate','lazy']]
    assert len(set(digests))==3, f'Unexpected identical {kind} arm binaries'
PYVERIFY
for mode in fullopt pgo native; do
  mkdir -p "$ROOT/$mode"
  for pair in 1 2 3 4 5 6; do
    case "$pair" in
      1) arms="baseline candidate lazy";;
      2) arms="candidate lazy baseline";;
      3) arms="lazy baseline candidate";;
      4) arms="baseline lazy candidate";;
      5) arms="candidate baseline lazy";;
      6) arms="lazy candidate baseline";;
    esac
    echo "$mode $pair $arms" >> "$ROOT/execution-order.txt"
    for arm in $arms; do
      if [[ "$mode" == native ]]; then
        taskset -c "$CPU" "$ROOT/builds/native-$arm/SharpLink.UnitTests" > "$ROOT/$mode/$arm-$pair.json"
      elif [[ "$mode" == pgo ]]; then
        ISSUE736_WARMUP_MS=1000 DOTNET_TieredCompilation=1 DOTNET_TieredPGO=1 taskset -c "$CPU" dotnet "$ROOT/builds/jit-$arm/SharpLink.UnitTests.dll" > "$ROOT/$mode/$arm-$pair.json"
      else
        DOTNET_TieredCompilation=0 taskset -c "$CPU" dotnet "$ROOT/builds/jit-$arm/SharpLink.UnitTests.dll" > "$ROOT/$mode/$arm-$pair.json"
      fi
    done
  done
  python3 eng/perf/issue736/summarize.py "$ROOT/$mode" "$mode" > "$ROOT/summary-$mode.txt"
done
