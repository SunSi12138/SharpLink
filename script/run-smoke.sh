#!/usr/bin/env bash
set -euo pipefail

NO_BUILD=0
declare -a DEMOS=()
declare -a TESTS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-build)
      NO_BUILD=1
      shift
      ;;
    --demo)
      DEMOS+=("$2")
      shift 2
      ;;
    --test)
      TESTS+=("$2")
      shift 2
      ;;
    *)
      echo "Unknown arg: $1" >&2
      exit 2
      ;;
  esac
done

if [[ ${#DEMOS[@]} -eq 0 ]]; then
  DEMOS=(
    "demo/HelloWorld",
    "demo/Streaming",
    "demo/Cancel",
    "demo/Log",
    "demo/OneWay",
    "demo/Timeout",
    "demo/HostApplication"
  )
fi

if [[ ${#TESTS[@]} -eq 0 ]]; then
  TESTS=(
    "test/SharpLink.IntegrationTests"
    "test/SharpLink.UnitTests"
  )
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

declare -a FAILED=()

run_step() {
  local kind="$1"
  local project="$2"

  echo
  echo "==> ${kind} :: ${project}"

  local cmd=(dotnet run --project "$project")
  if [[ "$NO_BUILD" -eq 0 ]]; then
    cmd+=(--no-build)
  fi

  if ! "${cmd[@]}"; then
    FAILED+=("${kind} :: ${project}")
  fi
}

if [[ "$NO_BUILD" -eq 0 ]]; then
  echo "==> build :: Sharplink.slnx"
  dotnet build "Sharplink.slnx" -v minimal
fi

for p in "${DEMOS[@]}"; do
  run_step "demo" "$p"
done

for p in "${TESTS[@]}"; do
  run_step "test" "$p"
done

echo
if [[ ${#FAILED[@]} -eq 0 ]]; then
  echo "Smoke run passed."
  exit 0
fi

echo "Smoke run failed:"
for item in "${FAILED[@]}"; do
  echo "  - ${item}"
done
exit 1

