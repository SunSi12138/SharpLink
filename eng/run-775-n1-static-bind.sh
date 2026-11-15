#!/usr/bin/env bash
set -euo pipefail
uid=N1-STATIC-BIND-20261115-V2
[[ "${GITHUB_ACTIONS:-}" == true && "${RUNNER_OS:-}" == Linux ]] || { echo 'FIXTURE: GitHub Linux only' >&2; exit 2; }
[[ "${N1_RUN_UID:-}" == "$uid" ]] || { echo 'FIXTURE: explicit reviewed UID required' >&2; exit 2; }
[[ -n "${N1_SOURCE_SHA:-}" && "$(git rev-parse HEAD)" == "$N1_SOURCE_SHA" ]] || { echo 'FIXTURE: source subject mismatch' >&2; exit 2; }
[[ "$(dotnet --version)" == 10.0.115 ]] || { echo 'FIXTURE: SDK must be 10.0.115' >&2; exit 2; }
out="$PWD/artifacts/n1-static-bind-hosted"
mkdir -p "$out"
export N1_RESULT_PATH="$out/result.json"
{
  printf 'uid=%s\nsource=%s\nrun_id=%s\nrun_attempt=%s\n' "$uid" "$N1_SOURCE_SHA" "${GITHUB_RUN_ID:-}" "${GITHUB_RUN_ATTEMPT:-}"
  uname -a
  dotnet --info
  git status --short
} > "$out/environment.txt"
dotnet build test/SharpLink.N1.StaticBindEvidence/SharpLink.N1.StaticBindEvidence.csproj -c Release --nologo 2>&1 | tee "$out/build.log"
dotnet --fx-version 10.0.15 test/SharpLink.N1.StaticBindEvidence/bin/Release/net10.0/SharpLink.N1.StaticBindEvidence.dll rpc "$uid" 2>&1 | tee "$out/run.log"
