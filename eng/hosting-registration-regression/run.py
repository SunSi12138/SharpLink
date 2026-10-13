#!/usr/bin/env python3
"""Verify identical Hosting fixtures against fixed old products and the actual PR head.

Only the test fixture is overlaid. Production sources stay untouched in every arm.
Failed attempts, logs, reports, fixture bytes and binary identities are retained.
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

BASELINE = "fbaf3e40a1d36133846f101bf1aacfbec6ec4a3d"
COUPLED = "3c95d436fcfc7dd0f95b3853cf0b3d69e21957c3"
PROJECT = "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj"
FIXTURE = "test/SharpLink.UnitTests/Hosting/HostingBuilderRegistrationTests.cs"
BIN = "test/SharpLink.UnitTests/bin/Release/net10.0"
CORE_FILTER = "/*/*/HostingBuilderRegistrationTests/Repeated*"
HEALTH_FILTER = "/*/*/HostingBuilderRegistrationTests/PreRegistered*"
ALL_FILTER = "/*/*/HostingBuilderRegistrationTests/*"
FULL_COUNT = 2173  # 2170 prior cases + 3 pooled admission-signal lifetime cases.


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def binaries(work):
    root = work / BIN
    return {str(p.relative_to(root)): digest(p) for p in sorted(root.rglob("*"))
            if p.is_file() and p.suffix in (".dll", ".pdb", ".json")
            and "TestResults" not in p.relative_to(root).parts}


def inspect(text, total, failed, markers):
    text = re.sub(r"\x1b\[[0-9;]*m", "", text)
    counts = {}
    for key in ("total", "failed", "succeeded", "skipped"):
        found = re.findall(r"\b" + key + r":\s*(\d+)\b", text)
        counts[key] = int(found[-1]) if found else (0 if key == "failed" else None)
    expected = dict(total=total, failed=failed, succeeded=total - failed, skipped=0)
    if counts != expected:
        raise ValueError(f"Unexpected results: {counts}; expected {expected}")
    for marker in markers:
        if marker not in text:
            raise ValueError("Expected failure reason missing: " + marker)
    return counts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    repo, out = Path.cwd(), args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    current = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    run_key = os.environ.get("GITHUB_RUN_ID", str(os.getpid())) + "-" + os.environ.get("GITHUB_RUN_ATTEMPT", "1")
    temporary = Path(os.environ["RUNNER_TEMP"]) / ("hosting-registration-" + run_key)
    temporary.mkdir(parents=True, exist_ok=False)
    rows, checks = [], []
    provenance = {"baselineSha": BASELINE, "coupledHealthCheckSha": COUPLED,
                  "candidateSha": current, "fixtureSha256": digest(repo / FIXTURE),
                  "workflowSha256": digest(repo / ".github/workflows/hosting-registration-regression.yml"),
                  "collectorSha256": digest(Path(__file__)),
                  "testParallelism": os.environ.get("TUNIT_MAX_PARALLEL_TESTS"),
                  "productionCodeChangedByTool": False, "arms": {}}
    result = {"status": "incomplete"}

    def save():
        for name, value in (("provenance", provenance), ("exits", rows), ("checks", checks)):
            (out / (name + ".json")).write_text(json.dumps(value, indent=2) + "\n")

    def execute(command, name, work=repo):
        row = {"name": name, "command": command, "startEpochSeconds": time.time(),
               "exitCode": None, "timedOut": False}
        rows.append(row)
        save()
        with (out / (name + ".log")).open("w") as log:
            try:
                row["exitCode"] = subprocess.run(command, cwd=work, stdout=log,
                                                 stderr=subprocess.STDOUT, timeout=600).returncode
            except subprocess.TimeoutExpired:
                row.update(exitCode=124, timedOut=True)
        save()
        print(json.dumps(row), flush=True)
        return row["exitCode"]

    def require(command, name, work=repo):
        if execute(command, name, work) != 0:
            raise RuntimeError("Preparation failed: " + name)

    try:
        save()
        require(["dotnet", "--info"], "dotnet-info")
        for revision in (BASELINE, COUPLED):
            require(["git", "fetch", "--no-tags", "--depth=1", "origin", revision], "fetch-" + revision)
        shutil.copy2(repo / FIXTURE, out / "HostingBuilderRegistrationTests.cs")
        for arm, revision in (("baseline", BASELINE), ("coupled", COUPLED), ("candidate", current)):
            work = temporary / arm
            require(["git", "worktree", "add", "--detach", str(work), revision], "worktree-" + arm)
            target = work / FIXTURE
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((repo / FIXTURE).read_bytes())
            if digest(target) != provenance["fixtureSha256"]:
                raise RuntimeError("Fixture bytes differ: " + arm)
            production_diff = subprocess.check_output(["git", "diff", "HEAD", "--", "src"], cwd=work)
            if production_diff:
                raise RuntimeError("Fixture overlay modified production sources: " + arm)
            (out / (arm + "-production-tree.txt")).write_bytes(subprocess.check_output(
                ["git", "ls-tree", "-r", "HEAD", "--", "src"], cwd=work))
            (out / (arm + "-to-candidate-product.patch")).write_bytes(subprocess.check_output(
                ["git", "diff", "--binary", revision, current, "--", "src"], cwd=repo))
            (out / (arm + "-tracked-overlay.patch")).write_bytes(subprocess.check_output(
                ["git", "diff", "--binary", "HEAD"], cwd=work))
            require(["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal"], "build-" + arm, work)
            hashes = binaries(work)
            for required in ("SharpLink.UnitTests.dll", "SharpLink.Hosting.dll"):
                if required not in hashes:
                    raise RuntimeError("Missing binary: " + required)
            (out / (arm + "-binaries.sha256.json")).write_text(json.dumps(hashes, indent=2) + "\n")
            provenance["arms"][arm] = {"subjectSha": revision, "fixtureSha256": digest(target),
                                        "hostingAssemblySha256": hashes["SharpLink.Hosting.dll"],
                                        "testAssemblySha256": hashes["SharpLink.UnitTests.dll"]}
            save()
            if arm == "baseline":
                runs = [("core-red", CORE_FILTER, 3, 3, (
                    "repeated Server setup must configure/return the previously registered Builder",
                    "repeated Client setup must configure/return the previously registered Builder",
                    "multi-cluster repeated configuration must return the registered coordinator Builder"))]
            elif arm == "coupled":
                runs = [("health-red", HEALTH_FILTER, 4, 4, (
                    "a pre-registered Server hosted service must not suppress its readiness health check",
                    "a pre-registered Client hosted service must not suppress its readiness health check"))]
            else:
                runs = [("core-green", CORE_FILTER, 3, 0, ()),
                        ("health-green", HEALTH_FILTER, 4, 0, ()),
                        ("hosting-green", ALL_FILTER, 11, 0, ()),
                        ("full-green", None, FULL_COUNT, 0, ())]
            for suffix, test_filter, total, failed, markers in runs:
                name = arm + "-" + suffix
                if binaries(work) != hashes:
                    raise RuntimeError("Binaries changed before " + name)
                reports = work / BIN / "TestResults"
                if reports.exists():
                    shutil.rmtree(reports)
                command = ["dotnet", "test", "--project", PROJECT, "-c", "Release", "--no-build"]
                if test_filter:
                    command += ["--treenode-filter", test_filter]
                code = execute(command, name, work)
                if reports.exists():
                    shutil.copytree(reports, out / (name + "-TestResults"))
                if not reports.exists() or not list(reports.glob("*.html")):
                    raise RuntimeError("Fresh complete report missing: " + name)
                counts = inspect((out / (name + ".log")).read_text(), total, failed, markers)
                if code != (2 if failed else 0):
                    raise RuntimeError("Unexpected process exit: " + name)
                if binaries(work) != hashes:
                    raise RuntimeError("Binaries changed during " + name)
                checks.append({"name": name, "counts": counts, "exitCode": code,
                               "binaryHashesUnchanged": True})
                save()
        result = {"status": "expected-red-and-green-passed", "candidateSha": current,
                  "baselineCoreFailures": 3, "coupledHealthFailures": 4,
                  "candidateHostingPassed": 11, "candidateFullPassed": FULL_COUNT}
    except Exception as error:
        result = {"status": "failed", "error": repr(error), "completedChecks": len(checks)}
        (out / "collector-error.log").write_text(traceback.format_exc())
        raise
    finally:
        (out / "result.json").write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
