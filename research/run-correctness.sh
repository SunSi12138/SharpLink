#!/usr/bin/env bash
set -uo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT=/workspace/scratch/9e5903e2fbfb/review-dotnet
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="$PWD/artifacts/cli-home"
export NUGET_PACKAGES="$PWD/artifacts/nuget-packages"
export NUGET_HTTP_CACHE_PATH="$PWD/artifacts/nuget-http"
export XDG_DATA_HOME="$PWD/artifacts/xdg"
export UseSharedCompilation=false
export MSBUILDDISABLENODEREUSE=1
mkdir -p artifacts/evidence
printf '%s\n' 'Non-timed correctness/native-instruction evidence only; natural runtime settings.' > artifacts/evidence/scope.txt
dotnet --info > artifacts/evidence/dotnet-info.txt 2>&1
python3 eng/check-project-reference-boundaries.py > artifacts/evidence/architecture.log 2>&1
printf '%s\n' "$?" > artifacts/evidence/architecture.exit
bash eng/check-maintainability.sh > artifacts/evidence/maintainability.log 2>&1
printf '%s\n' "$?" > artifacts/evidence/maintainability.exit
dotnet build test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false > artifacts/evidence/build.log 2>&1
build_exit=$?
printf '%s\n' "$build_exit" > artifacts/evidence/build.exit
if [ "$build_exit" -ne 0 ]; then tail -60 artifacts/evidence/build.log; exit "$build_exit"; fi
failure=0
for test_class in WakeupSignalTests SendPumpTests SendPumpTimedWaitStopTests SendPumpIdleShutdownTests SendPumpProgressIsolationTests SendPumpTimeBudgetPublicationTests SendPumpUnobservedTaskTests RpcSessionRuntimeFlushPolicyTests BackpressuredSendOwnershipTests; do
  if [ "$test_class" = WakeupSignalTests ]; then
    export DOTNET_JitDisasm='SharpLink.Runtime.WakeupSignal:Signal'
    export DOTNET_JitStdOutFile="$PWD/artifacts/evidence/wakeup-jit.txt"
  else
    unset DOTNET_JitDisasm DOTNET_JitStdOutFile
  fi
  dotnet test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll --treenode-filter "/*/*/$test_class/*" > "artifacts/evidence/$test_class.log" 2>&1
  test_exit=$?
  printf '%s\n' "$test_exit" > "artifacts/evidence/$test_class.exit"
  tail -14 "artifacts/evidence/$test_class.log"
  if [ "$test_exit" -ne 0 ]; then failure=1; fi
done
exit "$failure"
