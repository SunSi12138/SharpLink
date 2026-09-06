#!/usr/bin/env python3
"""Real-provider DateTime cross-zone checks and DateTimeOffset Release measurements.

Build: dotnet build test/SharpLink.UnitTests -c Release
Default --mode regression rejects scalar/collection semantic disagreement.
--mode characterize expects the explicitly documented baseline behavior instead.
"""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
ZONES = {"Etc/UTC": 0, "Asia/Tokyo": 9 * 3600 * 10_000_000}


def worker(name, method, directory, zone="Etc/UTC", kind="Local", source=None):
    result = directory / (name + ".json")
    result.unlink(missing_ok=True)
    environment = dict(os.environ, TZ=zone, SHARPLINK_DATE_KIND=kind,
                       SHARPLINK_VALIDATION_OUTPUT=str(result))
    environment.pop("SHARPLINK_CODEC_INPUT", None)
    if source is not None:
        environment["SHARPLINK_CODEC_INPUT"] = str(source)
    command = ["dotnet", "run", "-c", "Release", "--no-build", "--no-launch-profile",
               "--project", "test/SharpLink.UnitTests", "--", "--treenode-filter",
               f"/*/*/CodecValidationProbe/{method}", "--maximum-parallel-tests", "1"]
    with (directory / (name + ".log")).open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=log,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        try:
            process.wait(timeout=300 if method == "DateTimeOffsetFragmentation" else 120)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=10)
            raise RuntimeError(f"{name}: worker timeout, NOT positive evidence")
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
    if process.returncode != 0 or not result.exists():
        raise RuntimeError(f"{name}: worker/filter failure, exit={process.returncode}; inspect log")
    report = json.loads(result.read_text(encoding="utf-8"))
    if report.get("phase") != "complete":
        raise RuntimeError(f"{name}: incomplete report")
    if method == "DateTimeCrossZone" and report["offsetTicks"] != ZONES[zone]:
        raise RuntimeError(f"{name}: process did not enter requested timezone: {report}")
    return report, result


def matches_baseline(report, source_zone, target_zone, kind):
    scalar, array, values = report["scalar"], report["array"], report["list"]
    source = report["source"]
    if scalar["kind"] != kind or array["kind"] != kind or values["kind"] != kind or array != values:
        return False
    if source_zone == target_zone or kind != "Local":
        return report["invariant"] and scalar["ticks"] == source["ticks"]
    return (not report["invariant"] and scalar["utcTicks"] == source["utcTicks"] and
            array["ticks"] == source["ticks"] and
            scalar["ticks"] - array["ticks"] == ZONES[target_zone] - ZONES[source_zone])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("characterize", "regression"), default="regression")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/validation/codec")
    args = parser.parse_args()
    if os.name != "posix":
        parser.error("Process TZ isolation/watchdog currently require the Linux CI environment.")
    directory = args.output.resolve()
    directory.mkdir(parents=True, exist_ok=True)
    rows = []
    errors = []
    failed = False
    for source_zone in ZONES:
        for kind in ("Utc", "Local", "Unspecified"):
            prefix = source_zone.replace("/", "-") + "-" + kind
            try:
                produced, source = worker(prefix + "-write", "DateTimeCrossZone", directory, source_zone, kind)
                if not produced["invariant"]:
                    raise RuntimeError(f"{prefix}: same-process roundtrip control failed")
                for target_zone in ZONES:
                    name = prefix + "-to-" + target_zone.replace("/", "-")
                    report, _ = worker(name, "DateTimeCrossZone", directory, target_zone, kind, source)
                    matched = matches_baseline(report, source_zone, target_zone, kind)
                    passed = matched if args.mode == "characterize" else report["invariant"]
                    report.update(baselineMatched=matched, selectedModePassed=passed)
                    failed |= not passed
                    rows.append(report)
                    print(json.dumps(report), flush=True)
            except Exception as error:
                failed = True
                errors.append(str(error))
                print(f"INFRASTRUCTURE FAILURE: {error}", file=sys.stderr, flush=True)
    performance = None
    try:
        performance, _ = worker("datetimeoffset-fragmentation", "DateTimeOffsetFragmentation", directory)
        if len(performance["measurements"]) != 24 or not all(
                row["exactRoundtrip"] and row["medianNanoseconds"] > 0 for row in performance["measurements"]):
            raise RuntimeError("incomplete/invalid DateTimeOffset measurements")
        for row in performance["measurements"]:
            print(json.dumps(row), flush=True)
    except Exception as error:
        failed = True
        errors.append(str(error))
        print(f"INFRASTRUCTURE FAILURE: {error}", file=sys.stderr, flush=True)
    summary = dict(mode=args.mode, baseline="acb160faa72a07835b01d049a2fbcf9070b061df",
                   dateTime=rows, performance=performance, infrastructureErrors=errors,
                   note="Green characterization confirms baseline discrepancies; timings are evidence, not an optimization claim.")
    (directory / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    return int(failed)


if __name__ == "__main__":
    sys.exit(main())
