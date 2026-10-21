#!/usr/bin/env bash
set -euo pipefail

# Run only in an isolated checkout: this applies a test-only counterexample patch.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$root"
evidence="${1:-artifacts/cold-control-queue-repro}"
mkdir -p "$evidence"
git apply --check eng/research/cold-control/queued-replacement-cancellation.patch
git apply eng/research/cold-control/queued-replacement-cancellation.patch
dotnet build test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj \
  -c Release -m:1 -nr:false -p:UseSharedCompilation=false \
  2>&1 | tee "$evidence/build.log"

test_dll=test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll
common=(--maximum-parallel-tests 1 --minimum-expected-tests 1 --timeout 120s \
  --output Detailed --no-ansi --progress off)
dotnet "$test_dll" "${common[@]}" --treenode-filter \
  '/*/*/RuntimeAssemblyIntegrationTests/QueuedCallWithoutReplacementShouldObserveItsModuleCancellation' \
  2>&1 | tee "$evidence/positive-control.log"

set +e
dotnet "$test_dll" "${common[@]}" --treenode-filter \
  '/*/*/RuntimeAssemblyIntegrationTests/QueuedCallRetargetedByCompatibleReplacementShouldObserveNewModuleCancellation' \
  > "$evidence/counterexample.log" 2>&1
status=$?
set -e
cat "$evidence/counterexample.log"
python3 - "$evidence" "$status" <<'PY'
import json
from pathlib import Path
import re
import sys

directory = Path(sys.argv[1])
status = int(sys.argv[2])
text = (directory / "counterexample.log").read_text()
oracle = ("a queued call retargeted to the replacement must observe that replacement "
          "module's cancellation, not the already released old module's token")
assert status != 0, "The baseline bug no longer reproduces; reassess the conclusion."
assert oracle in text, "A different failure is not the expected counterexample."
assert re.search(r"total:\s+1\b", text), "Exactly one negative case must execute."
assert re.search(r"failed:\s+1\b", text), "Exactly one expected assertion must fail."
assert re.search(r"succeeded:\s+0\b", text), "The negative case must not be relabelled a pass."
result = {"positive_control": "passed", "counterexample": "expected assertion failure",
          "counterexample_exit_code": status, "oracle": oracle,
          "note": "This validates a failing production property, not a fixed implementation."}
(directory / "result.json").write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps(result))
PY
