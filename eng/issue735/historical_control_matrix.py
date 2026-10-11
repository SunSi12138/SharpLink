#!/usr/bin/env python3
"""Fixed four-pair, same-host historical H/G full-suite attribution experiment.

H = byte-unchanged 8ac4c849 source, all 2061 historical tests.
G = revision-2 causal overlay, all 2065 tests, including the four new cases.
Order is H/G, G/H, H/G, G/H. There is no retry-until-green loop. Preserve every
failure and distinguish an inconclusive matrix from a causally explained defect.
The script never adjusts a test timeout, runtime option, or test parallelism.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time

SUBJECT = "8ac4c84938d193c9a9659e9facd71f1fff82968a"
ORDER = (("control", "green"), ("green", "control"),
         ("control", "green"), ("green", "control"))
ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True)
    args = parser.parse_args()
    repo, out, scratch = args.repo.resolve(), args.output.resolve(), args.scratch.resolve()
    out.mkdir(parents=True, exist_ok=False)
    scratch.mkdir(parents=True, exist_ok=True)
    scripts = Path(__file__).resolve().parent / "historical"
    if os.environ.get("TUNIT_MAX_PARALLEL_TESTS") != "8":
        raise RuntimeError("Expected the unchanged historical CI parallelism 8; do not override it here")
    rows = []
    execution = {
        "subjectSha": SUBJECT,
        "harnessSha": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip(),
        "harnessFiles": {p.name: sha(p) for p in scripts.iterdir()
            if p.name in ("apply.py", "client-tests.cs.in", "session-tests.cs.in", "control-matrix.py")},
        "collectorSha256": sha(Path(__file__)),
        "plannedOrder": ORDER, "plannedFullRunsPerArm": 4,
        "expectedTestCount": {"control": 2061, "green": 2065},
        "testParallelism": os.environ["TUNIT_MAX_PARALLEL_TESTS"],
        "cpuAffinity": sorted(os.sched_getaffinity(0)), "cpuCount": os.cpu_count(),
        "runtimeEnvironment": {k: v for k, v in os.environ.items()
            if k.startswith(("DOTNET_", "COMPlus_", "TUNIT_", "TESTINGPLATFORM_"))},
        "interpretation": "Historical attribution only; no production acceptance; all prior failed runs remain evidence",
    }
    (out / "provenance.json").write_text(json.dumps(execution, indent=2) + "\n")

    def run(command, name, cwd=repo, limit=600):
        began = datetime.now(timezone.utc).isoformat()
        before = time.monotonic()
        with (out / f"{name}.log").open("w") as log:
            try:
                code = subprocess.run(command, cwd=cwd, stdout=log,
                    stderr=subprocess.STDOUT, timeout=limit).returncode
            except subprocess.TimeoutExpired:
                code = 124
        rows.append({"name": name, "command": command, "cwd": str(cwd),
            "startedUtc": began, "elapsedSeconds": time.monotonic() - before, "exitCode": code})
        (out / "exits.json").write_text(json.dumps(rows, indent=2) + "\n")
        print(json.dumps(rows[-1]), flush=True)
        return code

    for command, name in ((["dotnet", "--info"], "dotnet-info"),
        (["lscpu"], "cpu"), (["git", "fetch", "--no-tags", "--depth=1", "origin", SUBJECT], "fetch")):
        if run(command, name) != 0:
            return 1

    roots = {}
    binary_hashes = {}
    for mode in ("control", "green"):
        work = scratch / ("historical-control-matrix-" + mode)
        roots[mode] = work
        if run(["git", "worktree", "add", "--detach", str(work), SUBJECT], "worktree-" + mode) != 0:
            return 1
        assert subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=work, text=True).strip() == SUBJECT
        if mode == "green":
            if run(["python3", str(scripts / "apply.py"), "--checkout", str(work),
                "--mode", "green", "--manifest", str(out / "green-overlay.json")], "overlay-green") != 0:
                return 1
            assert json.loads((out / "green-overlay.json").read_text())["overlayRevision"] == 2
        patch = subprocess.check_output(["git", "diff", "--binary"], cwd=work)
        (out / f"{mode}.patch").write_bytes(patch)
        if mode == "control":
            assert not patch, "H must remain the exact historical source, without added tests"
        if run(["dotnet", "build", "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj",
            "-c", "Release", "-p:Platform=AnyCPU", "-v", "minimal"], "build-" + mode, work) != 0:
            return 1
        bin_path = work / "test/SharpLink.UnitTests/bin/Release/net10.0"
        hashes = {str(p.relative_to(work)): sha(p) for p in bin_path.rglob("*")
            if p.is_file() and (p.suffix in (".dll", ".pdb") or p.name.endswith((".deps.json", ".runtimeconfig.json")))}
        assert "test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll" in hashes
        binary_hashes[mode] = hashes
        (out / f"{mode}-binaries.sha256.json").write_text(json.dumps(hashes, indent=2) + "\n")

    summaries = []
    for pair, order in enumerate(ORDER, 1):
        for mode in order:
            work = roots[mode]
            for path, expected in binary_hashes[mode].items():
                assert sha(work / path) == expected, "Actual test binary drifted after build: " + path
            name = f"pair{pair}-{mode}-full"
            code = run(["dotnet", "test", "--project", "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj",
                "-c", "Release", "--no-build"], name, work)
            text = ANSI.sub("", (out / f"{name}.log").read_text())
            totals = re.findall(r"^\s*total:\s*(\d+)", text, re.MULTILINE)
            failed = re.findall(r"^\s*failed:\s*(\d+)", text, re.MULTILINE)
            skipped = re.findall(r"^\s*skipped:\s*(\d+)", text, re.MULTILINE)
            summary = {"pair": pair, "arm": mode, "exitCode": code,
                "total": int(totals[-1]) if totals else None,
                "failed": int(failed[-1]) if failed else None,
                "skipped": int(skipped[-1]) if skipped else None,
                "failureHeadings": re.findall(r"^failed (.+)$", text, re.MULTILINE),
                "producerEnteredDecodeFailure": "producer must enter decode" in text}
            summary["completeExpectedSuite"] = (summary["total"] == execution["expectedTestCount"][mode]
                and summary["skipped"] == 0)
            summaries.append(summary)
            (out / "matrix-summary.json").write_text(json.dumps(summaries, indent=2) + "\n")
            # TUnit's full HTML contains per-case detail omitted from successful stdout.
            results = work / "test/SharpLink.UnitTests/bin/Release/net10.0/TestResults"
            if results.exists():
                shutil.copytree(results, out / (name + "-TestResults"), dirs_exist_ok=False)
            # Always execute the entire pre-registered order, even after a failure.

    failed_runs = [row for row in summaries if row["exitCode"] != 0 or not row["completeExpectedSuite"]]
    conclusion = {
        "plannedRuns": 8, "completedRuns": len(summaries),
        "controlFailedRuns": sum(row["exitCode"] != 0 for row in summaries if row["arm"] == "control"),
        "greenFailedRuns": sum(row["exitCode"] != 0 for row in summaries if row["arm"] == "green"),
        "decodeEntryFailuresByArm": {mode: sum(row["producerEnteredDecodeFailure"]
            for row in summaries if row["arm"] == mode) for mode in ("control", "green")},
        "productionGo": False,
        "interpretation": "Any failure remains a failure. Absence in this finite sample does not explain or erase earlier failure.",
    }
    (out / "conclusion.json").write_text(json.dumps(conclusion, indent=2) + "\n")
    print(json.dumps(conclusion), flush=True)
    return 1 if failed_runs else 0


if __name__ == "__main__":
    raise SystemExit(main())
