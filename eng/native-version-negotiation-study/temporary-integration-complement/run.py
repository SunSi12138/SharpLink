
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys

here = Path(__file__).resolve().parent
platform = os.environ["RUNNER_OS"]
capture = Path("artifacts/999-integration-complement").resolve()
capture.mkdir(parents=True, exist_ok=True)
app = Path("test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll").resolve()

manifest = json.loads((here / "scope.json").read_text(encoding="utf-8"))
prior = manifest["platforms"][platform]
with (capture / "discovery.json").open("wb") as out:
    subprocess.run(["dotnet", str(app), "--list-tests", "json"], stdout=out, check=True)
discovered = json.loads((capture / "discovery.json").read_text(encoding="utf-8-sig"))["tests"]
current = {row["uid"] for row in discovered}
known = set(manifest["known"])
retained = set(prior["passed"])
held = set(prior["held"])
if len(current) != len(discovered) or current != known:
    raise SystemExit(f"Discovery differs from the reviewed input: {sorted(current-known)=}; {sorted(known-current)=}")
remaining = sorted(current - retained - held)
if not remaining or set(prior["failed"]) - retained - held:
    raise SystemExit("No normal complement, or a prior failed case was not dispositioned.")
print(f"{platform}: {len(current)} discovered, {len(retained)} retained, "
      f"{len(held-retained)} still held, {len(remaining)} scheduled.", flush=True)

# Windows CreateProcess caps the complete command line at 32,767 characters.
# The other runners can execute the whole scoped complement in one test session.
batch_size = 110 if platform == "Windows" else len(remaining)
plan = [remaining[i:i+batch_size] for i in range(0, len(remaining), batch_size)]
(capture / "plan.json").write_text(json.dumps({
    "source": os.environ["GITHUB_SHA"], "platform": platform, "exactUIDsByBatch": plan,
    "retained": sorted(retained), "heldUnverified": sorted(held-retained),
}, indent=2) + "\n", encoding="utf-8")
for i, batch in enumerate(plan, start=1):
    target = capture / f"batch-{i}"
    target.mkdir()
    cmd = ["dotnet", str(app), "--filter-uid", *batch,
           "--minimum-expected-tests", str(len(batch)), "--zero-tests-policy", "strict",
           "--maximum-parallel-tests", "1", "--timeout", "240s", "--output", "Detailed",
           "--results-directory", str(target)]
    result = subprocess.run(cmd)
    if result.returncode:
        raise SystemExit(result.returncode)
    (report,) = target.glob("*.tunit-report.json")
    run = json.loads(report.read_text(encoding="utf-8-sig"))
    observations = [row for group in run["groups"] for row in group["tests"]]
    if sorted(x["id"] for x in observations) != batch or any(row["status"] != "passed" for row in observations):
        raise SystemExit("Terminal result did not pass all and only the expected test IDs.")
    print(f"Batch {i}: {len(batch)} exact passed IDs", flush=True)
