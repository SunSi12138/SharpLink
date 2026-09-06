#!/usr/bin/env python3
"""Isolated baseline characterization OR correct-invariant checks. No production fixes.

Build first: dotnet build test/SharpLink.UnitTests -c Release
Run: python3 eng/validate-pending-lifecycle.py --mode characterize
Correctness gate (expected to fail on the issue baseline): --mode regression
"""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
SCENARIOS = ("no-listener", "metric-control", "deadline-response", "deadline-cancel",
             "deadline-disconnect", "metric-minus", "metric-plus", "logger-control", "logger-throw")


def run_probe(scenario, output_dir):
    result_path = output_dir / (scenario + ".json")
    log_path = output_dir / (scenario + ".log")
    result_path.unlink(missing_ok=True)
    environment = dict(os.environ, SHARPLINK_VALIDATION_SCENARIO=scenario,
                       SHARPLINK_VALIDATION_OUTPUT=str(result_path))
    worker = "AdmissionDiagnosticsValidationProbe" if scenario.startswith("logger-") else "PendingLifecycleValidationProbe"
    command = ["dotnet", "run", "-c", "Release", "--no-build", "--no-launch-profile",
               "--project", "test/SharpLink.UnitTests", "--", "--treenode-filter",
               f"/*/*/{worker}/Run", "--maximum-parallel-tests", "1"]
    with log_path.open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=log,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        started = time.monotonic()
        armed_at = None
        report = None
        timed_out = False
        try:
            while process.poll() is None:
                if result_path.exists():
                    report = json.loads(result_path.read_text(encoding="utf-8"))
                    if report.get("phase") == "dispose-enter" and armed_at is None:
                        armed_at = time.monotonic()
                now = time.monotonic()
                if (armed_at is not None and now - armed_at > 15) or now - started > 120:
                    timed_out = True
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=10)
                    break
                time.sleep(0.05)  # watchdog polling, NEVER race coordination
            process.wait(timeout=10)
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
    if result_path.exists():
        report = json.loads(result_path.read_text(encoding="utf-8"))
    if timed_out:
        armed = (scenario == "metric-plus" and report is not None and
                 report.get("phase") == "dispose-enter" and
                 report.get("registered") == 0 and report.get("countBefore") == 1 and
                 report.get("activeBefore") == 1 and report.get("positiveHits") == 1 and
                 report.get("ownerRegistered") == 0 and report.get("escaped") == "ProbeCallbackException")
        if not armed:
            raise RuntimeError(f"{scenario}: timeout without the exact published/unregistered evidence; see {log_path}")
        report.update(invariant=False, watchdog="killed-after-15s-in-dispose", exitCode=process.returncode)
    elif process.returncode != 0 or report is None or report.get("phase") != "complete":
        raise RuntimeError(f"{scenario}: worker/harness failure (exit {process.returncode}); see {log_path}")
    else:
        report["exitCode"] = process.returncode
    result_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def baseline_matches(name, report):
    if name in ("no-listener", "metric-control"):
        return (report["invariant"] and report["escaped"] is None and
                report["operationError"] is None and
                (report["hits"] == 0 if name == "no-listener" else
                 report["positiveHits"] == 1 and report["negativeHits"] == 1))
    if name.startswith("deadline-"):
        return (report["sameReference"] and report["futureBefore"] and report["futureAfter"] and
                not report["invariant"] and report["oldReason"] == "DeadlineExceeded" and
                report["reasonAfterScan"] == "DeadlineExceeded" and
                report["secondError"] == "DeadlineExceeded" and report["active"] == 0 and report["count"] == 0)
    if name == "metric-minus":
        return (not report["invariant"] and report["escaped"] == "ProbeCallbackException" and
                report["countBefore"] == 0 and report["activeBefore"] == 1 and
                report["operationError"] is None and not report["nextSucceeded"] and
                report["nextError"] == "ResourceExhausted" and
                report["positiveHits"] == 1 and report["negativeHits"] == 1)
    if name.startswith("logger-"):
        common = (report["policyAcquires"] == report["policyReports"] == report["loggerReports"] == 1 and
                  report["countAfter"] == report["activeAfter"] == 0 and report["nextSucceeded"] and
                  report["connectionsOpened"] == 0)
        if name == "logger-control":
            return common and report["invariant"] and report["completed"] and report["returned"]
        return (common and not report["invariant"] and not report["completed"] and not report["returned"] and
                report["escaped"] == "ProbeLoggerException")
    return report.get("watchdog") == "killed-after-15s-in-dispose" and not report["invariant"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("characterize", "regression"), default="regression")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/validation/pending")
    args = parser.parse_args()
    if os.name != "posix":
        parser.error("The process-tree watchdog currently requires POSIX; use the Linux CI job.")
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    reports = []
    failed = False
    for scenario in SCENARIOS:
        try:
            report = run_probe(scenario, args.output)
            matched = baseline_matches(scenario, report)
            passed = matched if args.mode == "characterize" else report["invariant"]
            report.update(baselineMatched=matched, selectedModePassed=passed)
            reports.append(report)
            print(json.dumps(report), flush=True)
            failed |= not passed
        except Exception as error:
            failed = True
            reports.append(dict(scenario=scenario, infrastructureError=str(error)))
            print(f"INFRASTRUCTURE FAILURE: {error}", file=sys.stderr, flush=True)
    summary = dict(mode=args.mode, baseline="acb160faa72a07835b01d049a2fbcf9070b061df",
                   note="Characterization PASS means exact baseline bugs reproduced, NOT correctness PASS.",
                   reports=reports)
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    return int(failed)


if __name__ == "__main__":
    sys.exit(main())
