#!/usr/bin/env python3
"""Fixed source controls for the zero-allocation full-suite failure in run 38170017685.

This diagnostic never overlays a test, edits production, retries a sample, or
changes the original allocation test's warmup, iterations, timeout or threshold.
Eight full-suite processes and 32 single-case processes are declared before build.
Every declared process is visited even if another process fails. The final exit is
nonzero for any failed test, missing result, source/binary drift or preparation error.
"""

import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import time
import traceback


OLD = "f8ba89121c87a6410a0139c44d7db30eab877f41"
CURRENT = "9b6951536d6d1451c694fcf49a1fc73b4994d9d8"
PROJECT = "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj"
BIN = "test/SharpLink.UnitTests/bin/Release/net10.0"
BRIDGE = "src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs"
SNAPSHOT_TEST = "test/SharpLink.UnitTests/Runtime/GeneratedStreamSnapshotOwnershipTests.cs"
TARGET_SOURCE = "test/SharpLink.UnitTests/Runtime/ReadOwnershipPipeReaderLifetimeTests.cs"
TARGET_CLASS = "ReadOwnershipPipeReaderLifetimeTests"
TARGET_METHOD = "SuccessfulSuspensionsShouldNotAllocateAfterWarmup"
FILTER = f"/*/*/{TARGET_CLASS}/{TARGET_METHOD}"
WORKFLOW = ".github/workflows/generated-stream-snapshot-controls.yml"
FULL_COUNTS = {"old": 2082, "current": 2084}
BRIDGE_HASHES = {
    "old": "af0683f3547a515bcf598c29fc534d32dc671d027468c03c0db489bdc42fd55c",
    "current": "531753a3f2fb54f1a596b854d3af30cd4f3625eef3c2bcfc955794eb1dd64075",
}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def git_bytes(repo, *arguments):
    return subprocess.check_output(["git", *arguments], cwd=repo)


def plan_runs():
    plan = []
    # Four complete pairs: AB, BA, AB, BA. A is the untouched fixed old commit.
    for kind, pairs in (("full", 4), ("allocation", 16)):
        for pair in range(pairs):
            order = ("old", "current") if pair % 2 == 0 else ("current", "old")
            for within_pair, arm in enumerate(order):
                plan.append({"position": len(plan), "kind": kind, "pair": pair,
                             "order": "AB" if pair % 2 == 0 else "BA", "withinPair": within_pair,
                             "arm": arm, "name": f"{kind}-p{pair:02d}-{within_pair}-{arm}",
                             "subjectSha": OLD if arm == "old" else CURRENT,
                             "expectedTests": FULL_COUNTS[arm] if kind == "full" else 1,
                             "status": "not-run"})
    return plan


def binary_hashes(work):
    root = work / BIN
    return {str(path.relative_to(root)): digest(path) for path in sorted(root.rglob("*"))
            if path.is_file() and path.suffix in (".dll", ".pdb", ".json")
            and "TestResults" not in path.relative_to(root).parts}


def verify_checkout_bytes(repo, work, revision):
    """Compare every tracked file byte-for-byte with its fixed commit, not only git status."""
    records, entries = {}, []
    for entry in git_bytes(repo, "ls-tree", "-r", "-z", revision).split(b"\0"):
        if not entry:
            continue
        metadata, encoded_path = entry.split(b"\t", 1)
        mode, kind, blob = metadata.decode().split()
        path = encoded_path.decode()
        if kind != "blob" or mode not in ("100644", "100755"):
            raise ValueError("Unexpected non-file source entry: " + path)
        entries.append((path, blob))
    batch = subprocess.check_output(["git", "cat-file", "--batch"], cwd=repo,
                                    input="".join(blob + "\n" for _, blob in entries).encode())
    position = 0
    for path, blob in entries:
        end = batch.index(b"\n", position)
        actual_blob, kind, size = batch[position:end].decode().split()
        if actual_blob != blob or kind != "blob":
            raise ValueError("Git object identity changed during source verification")
        position = end + 1
        committed = batch[position:position + int(size)]
        position += int(size) + 1
        actual = (work / path).read_bytes()
        if actual != committed:
            raise ValueError("Checkout bytes differ from fixed commit: " + path)
        records[path] = {"gitBlob": blob, "bytes": len(actual),
                         "sha256": hashlib.sha256(actual).hexdigest()}
    if position != len(batch):
        raise ValueError("Unexpected trailing data in fixed source object batch")
    return records


def assert_sources_unchanged(work, identities):
    for path, identity in identities.items():
        if digest(work / path) != identity["sha256"]:
            raise ValueError("Tracked source changed after verification: " + path)


def read_counts(text):
    clean = re.sub(r"\x1b\[[0-9;]*m", "", text)
    counts = {}
    for key in ("total", "failed", "succeeded", "skipped"):
        matches = re.findall(r"\b" + key + r":\s*(\d+)\b", clean)
        counts[key] = int(matches[-1]) if matches else (0 if key == "failed" else None)
    return counts


def inspect_reports(directory, row, log_text, seen_generations):
    errors = []
    html = sorted(directory.glob("*.html"))
    reports = sorted(directory.glob("*.tunit-report.json"))
    if len(html) != 1 or len(reports) != 1:
        raise ValueError("Exactly one fresh HTML and one TUnit JSON report are required")
    report = json.loads(reports[0].read_text())
    generation = report.get("publicationGeneration")
    if not generation or generation in seen_generations:
        errors.append("Missing or reused TUnit report publication generation")
    seen_generations.add(generation)
    row["reports"] = {"htmlSha256": digest(html[0]), "jsonSha256": digest(reports[0]),
                      "publicationGeneration": generation,
                      "reportedCommitSha": report.get("commitSha"),
                      "runtimeVersion": report.get("runtimeVersion"),
                      "tunitVersion": report.get("tunitVersion")}
    # A report may inherit the workflow's GITHUB_SHA. It is retained as reported,
    # never substituted for the separately verified subject checkout and binary hash.
    tests = [test for group in report.get("groups", []) for test in group.get("tests", [])]
    ids = [test.get("id") for test in tests]
    if len(tests) != row["expectedTests"] or len(set(ids)) != len(ids) or None in ids:
        errors.append("TUnit case count/unique case identity does not match the declared population")
    summary = report.get("summary", {})
    row["summary"] = summary
    row["consoleCounts"] = read_counts(log_text)
    row["testStatusCounts"] = dict(Counter(test.get("status") for test in tests))
    expected = {"total": row["expectedTests"], "passed": row["expectedTests"], "failed": 0,
                "skipped": 0, "cancelled": 0, "timedOut": 0, "flaky": 0}
    if summary != expected or any(test.get("status") != "passed" for test in tests):
        errors.append("The unchanged test assertions did not all pass with the exact declared count")
    console_expected = {"total": row["expectedTests"], "failed": 0,
                        "succeeded": row["expectedTests"], "skipped": 0}
    if row["consoleCounts"] != console_expected:
        errors.append("Console result does not report the complete declared passing population")
    target = [test for test in tests if test.get("className") == TARGET_CLASS and
              test.get("methodName") == TARGET_METHOD]
    if len(target) != 1:
        errors.append("The exact allocation regression was not executed once")
    else:
        row["allocationTarget"] = target[0]
        message = target[0].get("exception", {}).get("message", "")
        match = re.search(r"allocate 0 bytes, got (\d+)", message)
        row["observedAllocationFailureBytes"] = int(match.group(1)) if match else None
    row["failedCases"] = [test for test in tests if test.get("status") != "passed"]
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    repo, out = Path.cwd(), args.out.resolve()
    if out.exists():
        raise ValueError("Refuse to overwrite prior control evidence")
    out.mkdir(parents=True)
    plan, operations = plan_runs(), []
    tool_sha = git_bytes(repo, "rev-parse", "HEAD").decode().strip()
    env = os.environ.copy()
    evidence = {"schema": "sharplink-snapshot-allocation-controls-v1", "toolSourceSha": tool_sha,
                "oldSubjectSha": OLD, "currentSubjectSha": CURRENT,
                "scriptSha256": digest(Path(__file__)), "workflowSha256": digest(repo / WORKFLOW),
                "productionPerformanceEvidence": False, "productionOrTestOverlays": False,
                "testParallelism": env.get("TUNIT_MAX_PARALLEL_TESTS"),
                "environment": {key: value for key, value in sorted(env.items())
                                if key.startswith(("DOTNET_", "COMPlus_", "TUNIT_", "TESTINGPLATFORM_"))
                                or key in ("ImageOS", "ImageVersion", "RUNNER_OS", "RUNNER_ARCH")},
                "plannedFullRuns": 8, "plannedAllocationPairs": 16,
                "plannedAllocationProcesses": 32, "plannedProcesses": 40,
                "processTimeoutSeconds": 600, "automaticRetries": 0, "arms": {},
                "reportMetadataIsNotBinaryProvenance": True}
    started = time.time()
    run_key = env.get("GITHUB_RUN_ID", str(os.getpid())) + "-" + env.get("GITHUB_RUN_ATTEMPT", "1")
    temporary = Path(env["RUNNER_TEMP"]) / ("snapshot-allocation-controls-" + run_key)
    temporary.mkdir(parents=True, exist_ok=False)
    result = {"status": "incomplete", "productionPerformanceEvidence": False}

    def save():
        write_json(out / "provenance.json", evidence)
        write_json(out / "planned-and-actual-runs.json", plan)
        write_json(out / "exits.json", operations)

    def execute(command, name, cwd=repo):
        operation = {"name": name, "command": command, "cwd": str(cwd), "exitCode": None,
                     "utcStartEpochSeconds": time.time(), "timeoutSeconds": 600,
                     "timedOut": False, "launchError": None}
        operations.append(operation)
        save()
        with (out / (name + ".stdout.log")).open("w") as stdout, \
                (out / (name + ".stderr.log")).open("w") as stderr:
            try:
                process = subprocess.Popen(command, cwd=cwd, stdout=stdout, stderr=stderr,
                                           env=env, start_new_session=True)
                try:
                    operation["exitCode"] = process.wait(timeout=600)
                except subprocess.TimeoutExpired:
                    operation["timedOut"] = True
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait(timeout=3)
                    operation["exitCode"] = 124
                    operation["terminationReturnCode"] = process.returncode
            except OSError as error:
                operation["launchError"] = repr(error)
        operation["utcEndEpochSeconds"] = time.time()
        operation["wallSeconds"] = operation["utcEndEpochSeconds"] - operation["utcStartEpochSeconds"]
        operation["stdoutSha256"] = digest(out / (name + ".stdout.log"))
        operation["stderrSha256"] = digest(out / (name + ".stderr.log"))
        save()
        print(json.dumps({"name": name, "exitCode": operation["exitCode"]}), flush=True)
        return operation

    def require(command, name, cwd=repo):
        operation = execute(command, name, cwd)
        if operation["exitCode"] != 0:
            raise RuntimeError("Control preparation failed: " + name)

    save()
    write_json(out / "predeclared-plan.json", [{k: v for k, v in row.items() if k != "status"} for row in plan])
    evidence["predeclaredPlanSha256"] = digest(out / "predeclared-plan.json")
    try:
        if env.get("TUNIT_MAX_PARALLEL_TESTS") != "8":
            raise ValueError("This fixed experiment requires TUNIT_MAX_PARALLEL_TESTS=8")
        require(["dotnet", "--info"], "dotnet-info")
        require(["uname", "-a"], "uname")
        for arm, revision in (("old", OLD), ("current", CURRENT)):
            require(["git", "fetch", "--no-tags", "--depth=1", "origin", revision], "fetch-" + arm)
        changed_product = git_bytes(repo, "diff", "--name-only", OLD, CURRENT, "--", "src").decode().splitlines()
        changed_tests = git_bytes(repo, "diff", "--name-only", OLD, CURRENT, "--", "test").decode().splitlines()
        if changed_product != [BRIDGE] or changed_tests != [SNAPSHOT_TEST]:
            raise ValueError("Fixed subjects differ beyond the one bridge fix and its two existing regressions")
        critical = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json",
                    "NuGet.config", "nuget.config", "SharpLink.sln", "SharpLink.slnx", ".github/actions/setup-dotnet"]
        if git_bytes(repo, "diff", "--name-only", OLD, CURRENT, "--", *critical).strip():
            raise ValueError("Build or runtime setup differs between fixed subjects")
        (out / "old-to-current-product.patch").write_bytes(git_bytes(repo, "diff", "--binary", OLD, CURRENT, "--", "src"))
        (out / "old-to-current-subject.patch").write_bytes(git_bytes(repo, "diff", "--binary", OLD, CURRENT))
        works, identities, binaries = {}, {}, {}
        for arm, revision in (("old", OLD), ("current", CURRENT)):
            work = temporary / arm
            try:
                require(["git", "worktree", "add", "--detach", str(work), revision], "worktree-" + arm)
                identity = verify_checkout_bytes(repo, work, revision)
                if digest(work / BRIDGE) != BRIDGE_HASHES[arm]:
                    raise ValueError("The bridge does not match the audited fixed subject")
                write_json(out / (arm + "-tracked-sources.sha256.json"), identity)
                evidence["arms"][arm] = {"subjectSha": revision, "trackedFilesVerifiedByteForByte": len(identity),
                    "bridgeSha256": digest(work / BRIDGE), "targetSourceSha256": digest(work / TARGET_SOURCE),
                    "productTreeGit": git_bytes(repo, "rev-parse", revision + ":src").decode().strip()}
                require(["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal"], "build-" + arm, work)
                assert_sources_unchanged(work, identity)
                hashes = binary_hashes(work)
                if not all(name in hashes for name in ("SharpLink.UnitTests.dll", "SharpLink.Runtime.dll")):
                    raise ValueError("The complete test/runtime subject binary was not built")
                write_json(out / (arm + "-binaries.sha256.json"), hashes)
                evidence["arms"][arm]["binaryHashesManifestSha256"] = digest(out / (arm + "-binaries.sha256.json"))
                evidence["arms"][arm]["runtimeAssemblySha256"] = hashes["SharpLink.Runtime.dll"]
                evidence["arms"][arm]["testAssemblySha256"] = hashes["SharpLink.UnitTests.dll"]
                works[arm], identities[arm], binaries[arm] = work, identity, hashes
            except Exception as error:
                evidence["arms"].setdefault(arm, {"subjectSha": revision})["preparationError"] = repr(error)
                (out / (arm + "-preparation-error.log")).write_text(traceback.format_exc())
            save()
        if len(identities) == 2:
            shared = set(identities["old"]) & set(identities["current"])
            differing = sorted(path for path in shared if path.startswith(("src/", "test/")) and
                               identities["old"][path] != identities["current"][path])
            if differing != [BRIDGE]:
                raise ValueError("Byte-verified common production/test sources differ beyond the bridge")
        generations = set()
        for row in plan:
            arm, name = row["arm"], row["name"]
            if arm not in works:
                row["notRunReason"] = "fixed subject did not build or verify; no substitute binary used"
                save()
                continue
            work = works[arm]
            row["validationErrors"] = []
            reports = work / BIN / "TestResults"
            try:
                assert_sources_unchanged(work, identities[arm])
                if binary_hashes(work) != binaries[arm]:
                    raise ValueError("Compiled subject drifted before process launch")
                if reports.exists():
                    # Earlier reports have already been copied under their unique process name.
                    shutil.rmtree(reports)
                command = ["dotnet", "test", "--project", PROJECT, "-c", "Release", "--no-build"]
                if row["kind"] == "allocation":
                    command += ["--treenode-filter", FILTER]
                row["status"] = "running"
                row["binaryHashesManifestSha256"] = evidence["arms"][arm]["binaryHashesManifestSha256"]
                save()
                operation = execute(command, name, work)
                row["exitCode"], row["timedOut"] = operation["exitCode"], operation["timedOut"]
                if reports.exists():
                    archive = out / (name + "-TestResults")
                    shutil.copytree(reports, archive)
                    text = (out / (name + ".stdout.log")).read_text() + (out / (name + ".stderr.log")).read_text()
                    row["validationErrors"] += inspect_reports(archive, row, text, generations)
                else:
                    row["validationErrors"].append("No fresh TUnit report was produced")
                if operation["exitCode"] != 0:
                    row["validationErrors"].append("Test process failed, timed out, or could not launch")
                assert_sources_unchanged(work, identities[arm])
                row["binaryHashesUnchanged"] = binary_hashes(work) == binaries[arm]
                if not row["binaryHashesUnchanged"]:
                    row["validationErrors"].append("Compiled subject changed during the process")
                row["status"] = "failed" if row["validationErrors"] else "passed"
            except Exception as error:
                row["status"] = "failed"
                row["validationErrors"].append(repr(error))
                (out / (name + ".collector-error.log")).write_text(traceback.format_exc())
            save()  # A failure never truncates the remaining declared A/B/BA schedule.
        counts = dict(Counter(row["status"] for row in plan))
        passed = counts == {"passed": 40}
        result = {"status": "all-fixed-controls-passed" if passed else "failed-controls-retained",
                  "processStatusCounts": counts, "declaredProcesses": 40,
                  "completedProcesses": sum("exitCode" in row for row in plan),
                  "automaticRetries": 0, "allFailuresRetained": True,
                  "productionPerformanceEvidence": False, "elapsedSeconds": time.time() - started}
    except Exception as error:
        result = {"status": "preparation-or-collector-failed", "error": repr(error),
                  "productionPerformanceEvidence": False, "allDeclaredPositionsRetained": True}
        (out / "collector-error.log").write_text(traceback.format_exc())
    finally:
        save()
        write_json(out / "result.json", result)
        files = {str(path.relative_to(out)): {"bytes": path.stat().st_size, "sha256": digest(path)}
                 for path in sorted(out.rglob("*")) if path.is_file() and path.name != "files.sha256.json"}
        write_json(out / "files.sha256.json", files)
    return 0 if result["status"] == "all-fixed-controls-passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
