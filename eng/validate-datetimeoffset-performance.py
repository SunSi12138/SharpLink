#!/usr/bin/env python3
"""Run the process-isolated #559 DateTimeOffset collection candidate/baseline A/B probe.

Build first: dotnet build test/SharpLink.UnitTests -c Release
This script validates report completeness/correctness only. Timing ratios are evidence and never pass/fail thresholds.
"""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def measurement_field(row, camel, pascal):
    return row.get(camel, row.get(pascal))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path,
                        default=ROOT / "artifacts/validation/datetimeoffset-performance")
    args = parser.parse_args()
    if os.name != "posix":
        parser.error("The isolated process watchdog currently requires POSIX/Linux CI.")

    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    report_path = output / "report.json"
    log_path = output / "probe.log"
    report_path.unlink(missing_ok=True)
    environment = dict(os.environ, SHARPLINK_VALIDATION_OUTPUT=str(report_path))
    command = [
        "dotnet", "run", "-c", "Release", "--no-build", "--no-launch-profile",
        "--project", "test/SharpLink.UnitTests", "--", "--treenode-filter",
        "/*/*/DateTimeOffsetPerformanceValidationProbe/Run", "--maximum-parallel-tests", "1"
    ]
    with log_path.open("w", encoding="utf-8") as log:
        process = subprocess.Popen(
            command, cwd=ROOT, env=environment, stdout=log,
            stderr=subprocess.STDOUT, start_new_session=True)
        try:
            process.wait(timeout=300)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=10)
            print("DateTimeOffset A/B probe timed out; this is infrastructure failure.", file=sys.stderr)
            return 1
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)

    if process.returncode != 0 or not report_path.exists():
        print(f"DateTimeOffset A/B probe failed, exit={process.returncode}; inspect {log_path}", file=sys.stderr)
        return 1

    report = json.loads(report_path.read_text(encoding="utf-8"))
    candidate = report.get("candidateMeasurements") or []
    baseline = report.get("baselineMeasurements") or []
    comparisons = report.get("comparisons") or []
    valid = (
        report.get("phase") == "complete" and report.get("invariant") is True and
        len(candidate) == 24 and len(baseline) == 24 and len(comparisons) == 24 and
        all(measurement_field(row, "exactRoundtrip", "ExactRoundtrip") is True and
            (measurement_field(row, "medianNanoseconds", "MedianNanoseconds") or 0) > 0
            for row in candidate + baseline)
    )
    if not valid:
        print("DateTimeOffset A/B report is incomplete or failed correctness checks.", file=sys.stderr)
        return 1

    for row in comparisons:
        print(json.dumps(row), flush=True)
    summary = {
        "phase": "complete",
        "invariant": True,
        "report": str(report_path),
        "candidateCells": len(candidate),
        "baselineCells": len(baseline),
        "comparisonCells": len(comparisons),
        "note": "Ratios are same-process evidence only; no timing threshold is enforced."
    }
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
