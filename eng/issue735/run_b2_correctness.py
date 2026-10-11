#!/usr/bin/env python3
"""B2 production-path correctness collector; no performance or NativeAOT claim.

The planned mutant is a diagnostic alteration of this candidate, not historical
baseline red evidence. Every step is planned before execution; unexpected failure
stops the sequence and leaves the remaining steps explicitly NotRun.
"""
from __future__ import annotations

import argparse
import base64
import collections
import datetime as dt
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import time
import traceback

PARTIAL = "src/SharpLink.Runtime/StreamFlowController.SendGrants.cs"
# Reviewed controller freeze. A future production correction requires a reviewed update.
REVIEWED_PARTIAL_SHA256 = "a5e84e028b181a056f580f818595f6e647af13db4e5350f5da16b2496dae917a"
UNIT = "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj"
GENERATOR = "test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj"
LOAD = "test/SharpLink.LoadTest.Tests/SharpLink.LoadTest.Tests.csproj"
INTEGRATION = "test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj"
DISABLE_CASE = "DisableGrantMustReturnUnusedCreditWithoutClosingTheLease"
DISPOSE_CASE = "DisposeMustCloseBeforeReturningGrantAndStillAllowTheOwnedItemRefund"
ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path: Path, value: object) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")
    temporary.replace(path)


def now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def git(work: Path, *arguments: str) -> str:
    return subprocess.check_output(["git", *arguments], cwd=work, text=True).strip()


def source_inventory(work: Path) -> dict:
    paths = subprocess.check_output(["git", "ls-files", "-z"], cwd=work).decode().split("\0")
    hashes = {p: sha256(work / p) for p in sorted(paths) if p}
    digest = hashlib.sha256(json.dumps(hashes, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    return {"commit": git(work, "rev-parse", "HEAD"), "gitSourceTree": git(work, "rev-parse", "HEAD:src"),
            "trackedContentSha256": digest, "fileSha256": hashes}


def project_output(work: Path, project: str) -> Path:
    return work / Path(project).parent / "bin/Release/net10.0"


def binary_inventory(work: Path, project: str) -> dict:
    output = project_output(work, project)
    assembly = Path(project).stem
    required = [assembly + ".dll", assembly + ".deps.json", assembly + ".runtimeconfig.json"]
    if project in (UNIT, INTEGRATION):
        required.append("SharpLink.Runtime.dll")
    for filename in required:
        if not (output / filename).is_file():
            raise ValueError(f"missing built executable input: {output / filename}")
    # Hash all top-level dependencies too; artifact copies retain the selected assemblies.
    hashes = {p.name: sha256(p) for p in sorted(output.iterdir()) if p.is_file() and
              (p.suffix in (".dll", ".json", ".pdb") or p.name == assembly)}
    return {"project": project, "outputDirectory": str(output), "fileSha256": hashes}


def parse_html_report(path: Path) -> dict:
    match = re.search(r'<script\b(?=[^>]*\bid="report-data")[^>]*>(.*?)</script>', path.read_text(), re.S)
    if match is None:
        raise ValueError(f"missing embedded TUnit report: {path}")
    raw = match[1].strip()
    report = json.loads(raw if raw.startswith("{") else gzip.decompress(base64.b64decode(raw, validate=True)))
    tests = report.get("tests")
    if not isinstance(tests, list):
        raise ValueError("TUnit report has no test list")
    ids = [t.get("id") for t in tests]
    if any(not isinstance(i, str) or not i for i in ids) or len(set(ids)) != len(ids):
        raise ValueError("TUnit report has missing or duplicate test identities")
    return report


def validate_test(log: str, report: dict, expected: dict, exit_code: int) -> dict:
    plain = ANSI.sub("", log)
    summary = {key: int(value) for key, value in re.findall(r"\b(total|failed|succeeded|skipped):\s*(\d+)\b", plain)}
    # Current MTP omits only the zero-failed line in a successful run; HTML still
    # independently proves zero failures. Do not synthesize any other count.
    summary.setdefault("failed", 0)
    wanted = {"total": expected["total"], "failed": expected.get("failed", 0),
              "succeeded": expected["total"] - expected.get("failed", 0), "skipped": 0}
    if exit_code != expected.get("exitCode", 0):
        raise ValueError(f"unexpected exit: {exit_code}; expected {expected.get('exitCode', 0)}")
    if summary != wanted:
        raise ValueError(f"console summary {summary} differs from predeclared {wanted}")
    tests = report["tests"]
    counts = collections.Counter(t["status"] for t in tests)
    html_counts = {"total": len(tests), "failed": counts["fail"], "succeeded": counts["pass"], "skipped": counts["skip"]}
    if html_counts != wanted or set(counts) - {"pass", "fail", "skip"}:
        raise ValueError(f"HTML outcomes {dict(counts)} differ from predeclared {wanted}")
    if expected.get("classPrefix") and any(not t.get("cls", "").startswith(expected["classPrefix"]) for t in tests):
        raise ValueError("focused HTML includes a class outside the declared selector")
    if expected.get("case") and any(t.get("name") != expected["case"] for t in tests):
        raise ValueError("single-case report does not contain exactly the requested test")
    if expected.get("symptom") and expected["symptom"] not in plain:
        raise ValueError("mutant failed without the declared credit-balance symptom")
    return {"consoleCounts": summary, "htmlCounts": html_counts,
            "classCounts": dict(collections.Counter(t.get("cls", "") for t in tests)),
            "failedTests": [{"id": t["id"], "name": t.get("name"), "status": t["status"]} for t in tests if t["status"] != "pass"]}


def apply_disable_mutant(work: Path, expected_source: dict, output: Path) -> dict:
    before = source_inventory(work)
    if before != expected_source:
        raise ValueError("mutant checkout does not exactly match the built candidate source")
    source = work / PARTIAL
    if sha256(source) != REVIEWED_PARTIAL_SHA256:
        raise ValueError("candidate controller differs from the reviewed mutant source guard")
    old = "                if (_sendStates.TryGetValue(lease.Key, out var state) && lease.IsCurrent(state))\n                    RecallSendGrant(state);"
    new = "                if (dispose && _sendStates.TryGetValue(lease.Key, out var state) && lease.IsCurrent(state))\n                    RecallSendGrant(state);"
    text = source.read_text()
    if text.count(old) != 1:
        raise ValueError("DisableGrant-only mutant anchor is missing or ambiguous")
    source.write_text(text.replace(old, new, 1))
    after = source_inventory(work)
    changed = [p for p in before["fileSha256"] if before["fileSha256"][p] != after["fileSha256"][p]]
    if changed != [PARTIAL]:
        raise ValueError(f"mutant changed unexpected files: {changed}")
    (output / "mutant.patch").write_text(git(work, "diff", "--binary") + "\n")
    record = {"classification": "candidate-disable-grant-refund-mutant-not-baseline-red",
              "changedFile": PARTIAL, "beforeSha256": before["fileSha256"][PARTIAL],
              "afterSha256": after["fileSha256"][PARTIAL],
              "change": "Require dispose for the existing Gi recall; DisableGrant omits recall, Dispose keeps recall.",
              "expectedSource": before, "mutantSource": after}
    write_json(output / "mutant-provenance.json", record)
    return after


class Collector:
    def __init__(self, output: Path, rows: list[dict], provenance: dict):
        self.output = output
        self.rows = rows
        self.provenance = provenance
        self.source: dict[str, dict] = {}
        self.binaries: dict[str, dict] = {}
        self.checkpoint("Running")

    def checkpoint(self, status: str) -> None:
        write_json(self.output / "exits.json", self.rows)
        write_json(self.output / "provenance.json", self.provenance)
        write_json(self.output / "result.json", {"status": status,
                   "classification": "production-candidate-correctness-plus-diagnostic-mutant",
                   "performanceEvidence": False, "explicitNativeAotPublishAndRun": False,
                   "steps": {state: [r["name"] for r in self.rows if r["status"] == state]
                             for state in ("NotRun", "Running", "Passed", "Failed")},
                   "candidateCorrectnessPassed": all(r["status"] == "Passed" for r in self.rows if r["arm"] == "candidate"),
                   "mutantIsHistoricalBaseline": False, "updatedUtc": now()})

    def command(self, row: dict) -> int:
        with (self.output / (row["name"] + ".log")).open("w") as log:
            process = subprocess.Popen(row["command"], cwd=row["cwd"], stdout=log,
                                       stderr=subprocess.STDOUT, start_new_session=True)
            row["pid"] = process.pid
            self.checkpoint("Running")
            try:
                return process.wait(timeout=row["timeoutSeconds"])
            except subprocess.TimeoutExpired:
                row["watchdogExpired"] = True
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
                return 124
            except BaseException:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
                raise

    def run(self, row: dict, action=None) -> None:
        row["status"] = "Running"
        row["startedUtc"] = now()
        self.checkpoint("Running")
        started = time.monotonic()
        try:
            if action is not None:
                row["details"] = action()
                row["exitCode"] = 0
            else:
                work = Path(row["cwd"])
                expected = row.get("testExpected")
                before_binary = None
                if expected is not None:
                    # Archive every previous report under its own step before removing a stale
                    # copy from the test output; a crashing process cannot inherit an old green.
                    result_dir = project_output(work, row["project"]) / "TestResults"
                    for path in result_dir.glob("*.html"):
                        path.unlink()
                    before_binary = binary_inventory(work, row["project"])
                    if before_binary != self.binaries[row["arm"]][row["project"]]:
                        raise ValueError("built dependency identity changed before test execution")
                    row["binaryBefore"] = before_binary
                row["exitCode"] = self.command(row)
                if expected is None:
                    if row["exitCode"] != 0:
                        raise ValueError(f"command exited {row['exitCode']}")
                else:
                    reports = list((project_output(work, row["project"]) / "TestResults").glob("*.html"))
                    row["reports"] = []
                    report_data = []
                    for report in reports:
                        target = self.output / (row["name"] + "-" + report.name)
                        shutil.copyfile(report, target)
                        row["reports"].append({"file": target.name, "sha256": sha256(target)})
                    for retained in row["reports"]:
                        target = self.output / retained["file"]
                        decoded = parse_html_report(target)
                        write_json(target.with_suffix(".json"), decoded)
                        report_data.append(decoded)
                    row["binaryAfter"] = binary_inventory(work, row["project"])
                    if row["binaryAfter"] != before_binary:
                        raise ValueError("built dependency identity changed during test execution")
                    current_source = source_inventory(work)
                    row["sourceContentSha256"] = current_source["trackedContentSha256"]
                    if current_source != self.source[row["arm"]]:
                        raise ValueError("tracked source changed after compilation")
                    if len(report_data) != 1:
                        raise ValueError(f"expected one fresh TUnit HTML report, found {len(report_data)}")
                    row["testResult"] = validate_test((self.output / (row["name"] + ".log")).read_text(),
                                                       report_data[0], expected, row["exitCode"])
            row["status"] = "Passed"
        except BaseException as error:
            row.setdefault("exitCode", None)
            row["status"] = "Failed"
            row["error"] = f"{type(error).__name__}: {error}"
            (self.output / (row["name"] + ".validation-error.txt")).write_text(traceback.format_exc())
            raise
        finally:
            row["elapsedSeconds"] = time.monotonic() - started
            row["finishedUtc"] = now()
            log = self.output / (row["name"] + ".log")
            if log.exists():
                row["logFile"] = log.name
                row["logSha256"] = sha256(log)
            self.checkpoint("Running")
            print(json.dumps({k: row[k] for k in ("name", "status", "exitCode")}, ensure_ascii=False), flush=True)

    def record_binaries(self, arm: str, work: Path, projects: tuple[str, ...]) -> dict:
        if source_inventory(work) != self.source[arm]:
            raise ValueError("tracked source changed during build")
        identities = {p: binary_inventory(work, p) for p in projects}
        destination = self.output / "binaries" / arm
        destination.mkdir(parents=True)
        for project, identity in identities.items():
            directory = destination / Path(project).stem
            directory.mkdir()
            selected = {Path(project).stem + suffix for suffix in (".dll", ".pdb", ".deps.json", ".runtimeconfig.json", "")}
            selected.update(("SharpLink.Runtime.dll", "SharpLink.Runtime.pdb"))
            for filename in sorted(selected & identity["fileSha256"].keys()):
                shutil.copyfile(project_output(work, project) / filename, directory / filename)
        if arm == "mutant" and identities[UNIT]["fileSha256"]["SharpLink.Runtime.dll"] == self.binaries["candidate"][UNIT]["fileSha256"]["SharpLink.Runtime.dll"]:
            raise ValueError("mutant executed the unchanged candidate Runtime DLL")
        self.binaries[arm] = identities
        self.provenance.setdefault("binaryIdentities", {})[arm] = identities
        return identities


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-sha", required=True)
    parser.add_argument("--expected-focused", type=int, default=41)
    parser.add_argument("--expected-unit", type=int, default=2123)
    parser.add_argument("--expected-generator", type=int, default=271)
    parser.add_argument("--expected-load", type=int, default=77)
    parser.add_argument("--expected-integration", type=int, default=454)
    return parser.parse_args()


def main(args: argparse.Namespace) -> int:
    repo, output = args.root.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    mutant = Path(os.environ.get("RUNNER_TEMP", str(output.parent))).resolve() / ("flow-b2-mutant-" + args.expected_sha[:12])
    rows: list[dict] = []

    def plan(name, command=None, *, arm="candidate", work=repo, timeout=600, project=None, expected=None):
        row = {"name": name, "arm": arm, "cwd": str(work), "command": command,
               "timeoutSeconds": timeout, "status": "NotRun", "exitCode": None}
        if expected is not None:
            row.update(project=project, testExpected=expected)
        rows.append(row)
        return row

    plan("source-and-plan-validation")
    plan("dotnet-info", ["dotnet", "--info"], timeout=60)
    plan("restore", ["dotnet", "restore", "Sharplink.slnx"])
    plan("build-candidate", ["dotnet", "build", "Sharplink.slnx", "--no-restore", "-c", "Release", "-v", "minimal"])
    plan("candidate-binary-identities")

    def test(name, project, total, selector=None, *, arm="candidate", work=repo, failure=False):
        command = ["dotnet", "test", "--project", project, "-c", "Release", "--no-build"]
        expected = {"total": total, "exitCode": 2 if failure else 0, "failed": 1 if failure else 0}
        if selector:
            command += ["--treenode-filter", "/*/*/" + selector]
            if selector == "StreamFlowGrant*/*":
                expected["classPrefix"] = "StreamFlowGrant"
            else:
                expected["case"] = selector.split("/")[-1]
        if failure:
            expected["symptom"] = "expected F/C/G=120/24/0; actual=108/24/12"
        return plan(name, command, arm=arm, work=work, project=project, expected=expected)

    test("candidate-disable-green", UNIT, 1, "StreamFlowGrantTests/" + DISABLE_CASE)
    test("candidate-focused", UNIT, args.expected_focused, "StreamFlowGrant*/*")
    for repetition in range(1, 5):
        test(f"candidate-full-r{repetition}", UNIT, args.expected_unit)
    test("candidate-generator", GENERATOR, args.expected_generator)
    test("candidate-load", LOAD, args.expected_load)
    plan("candidate-integration", ["dotnet", "run", "-c", "Release", "--no-build", "--project", "test/SharpLink.IntegrationTests", "--", "--maximum-parallel-tests", "1", "--timeout", "120s"],
         project=INTEGRATION, expected={"total": args.expected_integration, "exitCode": 0, "failed": 0})
    plan("project-boundaries", ["python3", "eng/check-project-reference-boundaries.py"])
    plan("format-whitespace", ["dotnet", "format", "whitespace", "Sharplink.slnx", "--no-restore", "--verify-no-changes", "--verbosity", "minimal"])
    plan("maintainability-gate-tests", ["python3", "eng/test-verify-maintainability.py"])
    plan("maintainability-gate", ["bash", "eng/check-maintainability.sh"])
    plan("generated-assembly-boundaries", ["bash", "eng/verify-generated-assembly-dependencies.sh"])
    plan("mutant-worktree", ["git", "worktree", "add", "--detach", str(mutant), args.expected_sha], arm="mutant")
    plan("mutant-overlay", arm="mutant", work=mutant)
    plan("build-mutant", ["dotnet", "build", UNIT, "-c", "Release", "-v", "minimal"], arm="mutant", work=mutant)
    plan("mutant-binary-identities", arm="mutant", work=mutant)
    test("mutant-disable-expected-red", UNIT, 1, "StreamFlowGrantTests/" + DISABLE_CASE, arm="mutant", work=mutant, failure=True)
    test("mutant-dispose-control", UNIT, 1, "StreamFlowGrantTests/" + DISPOSE_CASE, arm="mutant", work=mutant)

    provenance = {"expectedCandidateSha": args.expected_sha, "reviewedGrantSourceSha256": REVIEWED_PARTIAL_SHA256,
                  "collectorSha256": sha256(Path(__file__)), "argv": sys.argv, "createdUtc": now(),
                  "environment": {k: os.environ.get(k) for k in ("GITHUB_SHA", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_REPOSITORY", "RUNNER_OS", "RUNNER_ARCH", "TUNIT_MAX_PARALLEL_TESTS", "TESTINGPLATFORM_TELEMETRY_OPTOUT", "DOTNET_CLI_TELEMETRY_OPTOUT")},
                  "testTimeoutPolicy": "Existing per-test settings retained; integration keeps original CI maximum-parallel-tests=1 and timeout=120s. Outer process watchdog=600s.",
                  "performanceGate": "This collector never starts a performance experiment; all required rows must pass first."}
    write_json(output / "plan.json", {"provenance": provenance, "steps": rows})
    collector = Collector(output, rows, provenance)

    def validate_source():
        if any(getattr(args, name) < 1 for name in ("expected_focused", "expected_unit", "expected_generator", "expected_load", "expected_integration")):
            raise ValueError("all suite totals must be positive and predeclared")
        if not re.fullmatch(r"[0-9a-f]{40}", args.expected_sha) or git(repo, "rev-parse", "HEAD") != args.expected_sha:
            raise ValueError("checkout HEAD does not match the predeclared candidate SHA")
        if os.environ.get("GITHUB_SHA") not in (None, args.expected_sha):
            raise ValueError("Actions SHA differs from the executable candidate checkout")
        if os.environ.get("TUNIT_MAX_PARALLEL_TESTS") != "8":
            raise ValueError("TUnit parallelism must equal the existing PR Fast value 8")
        if git(repo, "diff", "HEAD", "--"):
            raise ValueError("candidate tracked sources have uncommitted changes")
        untracked = git(repo, "ls-files", "--others", "--exclude-standard").splitlines()
        if any(Path(p).suffix in (".cs", ".csproj", ".props", ".targets") for p in untracked):
            raise ValueError("untracked compiler inputs are not represented by the candidate commit")
        source = source_inventory(repo)
        if source["fileSha256"].get(PARTIAL) != REVIEWED_PARTIAL_SHA256:
            raise ValueError("candidate grant controller differs from the reviewed freeze")
        if source["fileSha256"].get("eng/issue735/run_b2_correctness.py") != sha256(Path(__file__)):
            raise ValueError("executing collector is not the candidate commit's tracked collector")
        collector.source["candidate"] = source
        provenance["candidateSource"] = source
        write_json(output / "candidate-source.json", source)
        return {k: source[k] for k in ("commit", "gitSourceTree", "trackedContentSha256")}

    def overlay():
        collector.source["mutant"] = apply_disable_mutant(mutant, collector.source["candidate"], output)
        return {"mutantTrackedContentSha256": collector.source["mutant"]["trackedContentSha256"]}

    actions = {"source-and-plan-validation": validate_source,
               "candidate-binary-identities": lambda: collector.record_binaries("candidate", repo, (UNIT, GENERATOR, LOAD, INTEGRATION)),
               "mutant-overlay": overlay,
               "mutant-binary-identities": lambda: collector.record_binaries("mutant", mutant, (UNIT,))}
    failed = None
    try:
        for row in rows:
            collector.run(row, actions.get(row["name"]))
    except BaseException as error:
        failed = f"{type(error).__name__}: {error}"
        (output / "collector-failure.txt").write_text(traceback.format_exc())
        for row in rows:
            if row["status"] == "NotRun":
                row["notRunReason"] = "Stopped after the first unexpected validation failure: " + failed
    finally:
        collector.checkpoint("Failed" if failed else "Passed")
        write_json(output / "artifact-manifest.json", {
            "algorithm": "sha256", "manifestDoesNotHashItself": True,
            "files": {str(p.relative_to(output)): sha256(p) for p in sorted(output.rglob("*"))
                      if p.is_file() and p.name != "artifact-manifest.json"}})
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main(arguments()))
