#!/usr/bin/env python3
"""Run fixed-old versus the checked-out product's send-credit terminal accounting regression.

The current arm is built from the actual checkout commit. Instrumentation adds
only the same regression fixture to both arms; it cannot fix either product.
There are no production hooks or algorithm transformations. All runs, including an unexpected failure, remain in artifacts.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
import traceback


BASELINE = "f8ba89121c87a6410a0139c44d7db30eab877f41"
PROJECT = "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj"
TEST = "test/SharpLink.UnitTests/Runtime/SendCreditTerminalAccountingTests.cs"
BIN = "test/SharpLink.UnitTests/bin/Release/net10.0"
FOCUSED_FILTER = "/*/*/SendCreditTerminalAccountingTests/*"
FOCUSED_COUNT = 1
FULL_COUNT = 2412  # Exact inventory includes 18 absolute Stop-budget controls; historical focused/negative counts unchanged.


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def binary_hashes(work):
    root = work / BIN
    return {str(path.relative_to(root)): digest(path) for path in sorted(root.rglob("*"))
            if path.is_file() and path.suffix in (".dll", ".pdb", ".json")
            and "TestResults" not in path.relative_to(root).parts}


def inspect_test_log(text, focused, old):
    text = re.sub(r"\x1b\[[0-9;]*m", "", text)
    counts = {}
    for key in ("total", "failed", "succeeded", "skipped"):
        found = re.findall(r"\b" + key + r":\s*(\d+)\b", text)
        # Successful TUnit summaries omit the 'failed' line.
        counts[key] = int(found[-1]) if found else (0 if key == "failed" else None)
    expected = {"total": FOCUSED_COUNT if focused else FULL_COUNT,
                "failed": 1 if old else 0,
                "succeeded": 0 if old else (FOCUSED_COUNT if focused else FULL_COUNT),
                "skipped": 0}
    if counts != expected:
        raise ValueError(f"Unexpected complete-suite result: {counts}, expected {expected}")
    if old:
        required = (
            "failed ErrorCompletionMustAcceptUnsentRefundWhileFullAbortMustIgnoreItsLateRefund",
            "completeBefore=56, completeAfter=56, completeRetained=1, abortBefore=64, abortAfter=64, abortRetained=0",
        )
        for marker in required:
            if marker not in text:
                raise ValueError("Old control did not reproduce the exact expected failure: " + marker)
    return counts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    repo = Path.cwd()
    out = args.out.resolve()
    if out.exists():
        raise ValueError("Refuse to overwrite prior regression evidence")
    out.mkdir(parents=True)
    tools = Path(__file__).resolve().parent
    current = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()
    run_key = os.environ.get("GITHUB_RUN_ID", str(os.getpid())) + "-" + os.environ.get("GITHUB_RUN_ATTEMPT", "1")
    temporary = Path(os.environ["RUNNER_TEMP"]) / ("send-credit-terminal-regression-" + run_key)
    temporary.mkdir(parents=True, exist_ok=False)
    rows, checks = [], []
    provenance = {
        "schema": "sharplink-send-credit-terminal-regression-run-v1",
        "oldControlSha": BASELINE, "currentSubjectSha": current,
        "toolSourceSha": current, "productionPerformanceEvidence": False,
        "workflowSha256": digest(repo / ".github/workflows/send-credit-terminal-regression.yml"),
        "fixtureSha256": digest(repo / TEST),
        "testParallelism": os.environ.get("TUNIT_MAX_PARALLEL_TESTS"),
        "toolFiles": {p.name: digest(p) for p in sorted(tools.iterdir()) if p.is_file()},
        "plannedRuns": {"oldFocused": 1, "currentFocused": 4, "currentFull": 4},
        "expectedCounts": {"focused": FOCUSED_COUNT, "full": FULL_COUNT},
        "arms": {},
    }

    def save():
        (out / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
        (out / "exits.json").write_text(json.dumps(rows, indent=2) + "\n")
        (out / "test-checks.json").write_text(json.dumps(checks, indent=2) + "\n")

    def execute(command, name, cwd=repo, timeout=600):
        row = {"name": name, "command": command, "cwd": str(cwd),
               "utcStartEpochSeconds": time.time(), "timeoutSeconds": timeout,
               "exitCode": None, "timedOut": False, "launchError": None}
        rows.append(row)
        save()
        started = time.monotonic()
        with (out / (name + ".stdout.log")).open("w") as stdout, \
                (out / (name + ".stderr.log")).open("w") as stderr:
            try:
                row["exitCode"] = subprocess.run(command, cwd=cwd, stdout=stdout,
                                                  stderr=stderr, timeout=timeout).returncode
            except subprocess.TimeoutExpired:
                row["exitCode"], row["timedOut"] = 124, True
            except OSError as error:
                row["launchError"] = repr(error)
        row["wallSeconds"] = time.monotonic() - started
        save()
        print(json.dumps({"name": name, "exitCode": row["exitCode"]}), flush=True)
        return row["exitCode"]

    def require(command, name, cwd=repo):
        if execute(command, name, cwd) != 0:
            raise RuntimeError("Regression preparation failed: " + name)

    save()
    try:
        require(["dotnet", "--info"], "dotnet-info")
        require(["git", "fetch", "--no-tags", "--depth=1", "origin", BASELINE], "fetch-old")
        (out / "old-to-current-subject.patch").write_bytes(subprocess.check_output(
            ["git", "diff", "--binary", BASELINE, current], cwd=repo))
        # Preserve the complete actual product difference, independent of instrumentation.
        (out / "old-to-current-product.patch").write_bytes(subprocess.check_output(
            ["git", "diff", "--binary", BASELINE, current, "--", "src"], cwd=repo))
        provenance["subjectPatchSha256"] = digest(out / "old-to-current-subject.patch")
        provenance["productPatchSha256"] = digest(out / "old-to-current-product.patch")
        save()
        for arm, revision in (("old", BASELINE), ("current", current)):
            work = temporary / arm
            require(["git", "worktree", "add", "--detach", str(work), revision], "worktree-" + arm)
            tracked_sources = subprocess.check_output(
                ["git", "ls-tree", "-r", revision, "--", "src"], cwd=repo)
            (out / (arm + "-production-tree.txt")).write_bytes(tracked_sources)
            source = work / "src/SharpLink.Runtime/StreamFlowController.cs"
            source_before = digest(source)
            if arm == "old" and source_before != "7694add9ffc1d858c80f0aa8f1f8815717068b8ff7181b86998672e68bfe60e2":
                raise RuntimeError("Old product does not match the audited source")
            target = work / TEST
            if target.exists() and target.read_bytes() != (repo / TEST).read_bytes():
                raise RuntimeError("Current checkout regression test differs from the supplied fixture")
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((repo / TEST).read_bytes())
            if digest(source) != source_before:
                raise RuntimeError("Installing the fixture mutated production code")
            overlay = {"subjectSha": revision, "sourceSha256": source_before,
                       "testSha256": digest(target), "productionCodeChangedByTool": False}
            (out / (arm + "-overlay.json")).write_text(json.dumps(overlay, indent=2) + "\n")
            patch = subprocess.check_output(["git", "diff", "--binary", "HEAD"], cwd=work)
            if arm == "old":
                added = subprocess.run(["git", "diff", "--no-index", "--binary", "/dev/null", TEST],
                                       cwd=work, capture_output=True)
                if added.returncode != 1 or added.stderr:
                    raise RuntimeError("Unable to record the exact added regression source")
                patch += added.stdout
            (out / (arm + "-instrumentation.patch")).write_bytes(patch)
            shutil.copy2(target, out / (arm + "-SendCreditTerminalAccountingTests.cs"))
            require(["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal"],
                    "build-" + arm, work)
            hashes = binary_hashes(work)
            for required in ("SharpLink.UnitTests.dll", "SharpLink.Runtime.dll"):
                if required not in hashes:
                    raise RuntimeError("Missing compiled subject binary: " + required)
            (out / (arm + "-binaries.sha256.json")).write_text(json.dumps(hashes, indent=2) + "\n")
            provenance["arms"][arm] = {"subjectSha": revision, "binaryFileCount": len(hashes),
                "testAssemblySha256": hashes["SharpLink.UnitTests.dll"],
                "runtimeAssemblySha256": hashes["SharpLink.Runtime.dll"],
                "instrumentationPatchSha256": digest(out / (arm + "-instrumentation.patch"))}
            save()
            planned = [(f"focused-r{i}", True) for i in range(1 if arm == "old" else 4)]
            if arm == "current":
                planned.extend((f"full-r{i}", False) for i in range(4))
            for suffix, focused in planned:
                name = arm + "-" + suffix
                if binary_hashes(work) != hashes:
                    raise RuntimeError("Compiled binaries changed before " + name)
                reports = work / BIN / "TestResults"
                if reports.exists():
                    shutil.rmtree(reports)  # Prior run's reports are already archived.
                command = ["dotnet", "test", "--project", PROJECT, "-c", "Release", "--no-build"]
                if focused:
                    command += ["--treenode-filter", FOCUSED_FILTER]
                code = execute(command, name, work)
                if reports.exists():
                    shutil.copytree(reports, out / (name + "-TestResults"))
                if not reports.exists() or not list(reports.glob("*.html")):
                    raise RuntimeError("Run did not produce a fresh complete TUnit HTML report: " + name)
                text = (out / (name + ".stdout.log")).read_text() + (out / (name + ".stderr.log")).read_text()
                counts = inspect_test_log(text, focused, arm == "old")
                if code != (2 if arm == "old" else 0):
                    raise RuntimeError("Unexpected process exit: " + name)
                if binary_hashes(work) != hashes:
                    raise RuntimeError("Compiled binaries changed during " + name)
                checks.append({"name": name, "counts": counts, "exitCode": code,
                               "expectedOldFailureVerified": arm == "old", "binaryHashesUnchanged": True})
                save()
        result = {"status": "expected-old-red-and-current-green-passed", "oldExpectedFailures": 1,
                  "currentFocusedRuns": 4, "currentFullRuns": 4, "fullTestsPerRun": FULL_COUNT,
                  "productionAlgorithmSuppliedByTool": False, "productionPerformanceEvidence": False}
    except Exception as error:
        result = {"status": "failed", "error": repr(error), "completedChecks": len(checks),
                  "remainingRunsNotRetried": True, "productionPerformanceEvidence": False}
        (out / "collector-error.log").write_text(traceback.format_exc())
        raise
    finally:
        (out / "result.json").write_text(json.dumps(result, indent=2) + "\n")
        files = {str(p.relative_to(out)): {"bytes": p.stat().st_size, "sha256": digest(p)}
                 for p in sorted(out.rglob("*")) if p.is_file() and p.name != "files.sha256.json"}
        (out / "files.sha256.json").write_text(json.dumps(files, indent=2) + "\n")


if __name__ == "__main__":
    main()
