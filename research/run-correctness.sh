#!/usr/bin/env bash
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
export DOTNET_ROOT="${DOTNET_ROOT:-/workspace/scratch/9e5903e2fbfb/review-dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="$PWD/artifacts/cli-home"
export NUGET_PACKAGES="$PWD/artifacts/nuget-packages"
export NUGET_HTTP_CACHE_PATH="$PWD/artifacts/nuget-http"
export XDG_DATA_HOME="$PWD/artifacts/xdg"
export UseSharedCompilation=false
export MSBUILDDISABLENODEREUSE=1
evidence="${1:-artifacts/correctness-$(date -u +%Y%m%dT%H%M%SZ)}"
if [ -e "$evidence" ]; then
  echo "Refusing to overwrite existing evidence: $evidence" >&2
  exit 2
fi
mkdir -p "$evidence" || exit 2
evidence="$(cd "$evidence" && pwd)" || exit 2
printf '%s\n' 'Non-timed correctness/native-instruction evidence only; natural runtime settings.' > "$evidence/scope.txt"
dotnet --info > "$evidence/dotnet-info.txt" 2>&1
printf '%s\n' "$?" > "$evidence/dotnet-info.exit"
python3 eng/check-project-reference-boundaries.py > "$evidence/architecture.log" 2>&1
printf '%s\n' "$?" > "$evidence/architecture.exit"
bash eng/check-maintainability.sh > "$evidence/maintainability.log" 2>&1
printf '%s\n' "$?" > "$evidence/maintainability.exit"
dotnet build test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false > "$evidence/build.log" 2>&1
build_exit=$?
printf '%s\n' "$build_exit" > "$evidence/build.exit"
if [ "$build_exit" -ne 0 ]; then tail -60 "$evidence/build.log"; exit "$build_exit"; fi
failure=0
for test_class in WakeupSignalTests SendPumpTests SendPumpTimedWaitStopTests SendPumpIdleShutdownTests SendPumpProgressIsolationTests SendPumpTimeBudgetPublicationTests SendPumpUnobservedTaskTests RpcSessionRuntimeFlushPolicyTests BackpressuredSendOwnershipTests; do
  if [ "$test_class" = WakeupSignalTests ]; then
    export DOTNET_JitDisasm='SharpLink.Runtime.WakeupSignal:Signal'
    export DOTNET_JitStdOutFile="$evidence/wakeup-jit.txt"
  else
    unset DOTNET_JitDisasm DOTNET_JitStdOutFile
  fi
  dotnet test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll --treenode-filter "/*/*/$test_class/*" > "$evidence/$test_class.log" 2>&1
  test_exit=$?
  printf '%s\n' "$test_exit" > "$evidence/$test_class.exit"
  tail -14 "$evidence/$test_class.log"
  if [ "$test_exit" -ne 0 ]; then failure=1; fi
done
python3 research/validate_correctness.py "$evidence" > "$evidence/validation.json"
validation_exit=$?
if [ "$validation_exit" -ne 0 ]; then failure=1; fi
cat "$evidence/validation.json"
exit "$failure"
