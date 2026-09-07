#!/usr/bin/env python3
"""Real-provider DateTime cross-zone checks and DateTimeOffset Release measurements.

Build: dotnet build test/SharpLink.UnitTests -c Release
Default --mode regression requires DateTime scalar/nullable/collection paths to preserve
raw ticks + Kind across process time zones. --mode characterize retains the original
pre-fix scalar-vs-collection baseline matcher for historical evidence.
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
DATE_PATHS = ("scalar", "nullable", "array", "list", "memory", "readOnlyMemory", "immutableArray")
COLLECTION_PATHS = ("array", "list", "memory", "readOnlyMemory", "immutableArray")


def worker(name, method, directory, zone="Etc/UTC", kind="Local", source=None, date_case="normal"):
    result = directory / (name + ".json")
    result.unlink(missing_ok=True)
    environment = dict(os.environ, TZ=zone, SHARPLINK_DATE_KIND=kind,
                       SHARPLINK_DATE_CASE=date_case, SHARPLINK_VALIDATION_OUTPUT=str(result))
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


def matches_raw_contract(report):
    source = report["source"]
    values = [report[name] for name in DATE_PATHS]
    return (report["invariant"] and
            all(value["ticks"] == source["ticks"] and value["kind"] == source["kind"]
                for value in values))


def matches_baseline(report, source_zone, target_zone, kind):
    scalar = report["scalar"]
    nullable = report["nullable"]
    collections = [report[name] for name in COLLECTION_PATHS]
    source = report["source"]
    values = [scalar, nullable, *collections]
    if any(value["kind"] != kind for value in values):
        return False
    if any(value != collections[0] for value in collections[1:]):
        return False
    if scalar != nullable:
        return False
    if source_zone == target_zone or kind != "Local":
        return report["invariant"] and all(value["ticks"] == source["ticks"] for value in values)
    return (not report["invariant"] and scalar["utcTicks"] == source["utcTicks"] and
            nullable["utcTicks"] == source["utcTicks"] and
            all(value["ticks"] == source["ticks"] for value in collections) and
            scalar["ticks"] - collections[0]["ticks"] == ZONES[target_zone] - ZONES[source_zone])


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
                    baseline_matched = matches_baseline(report, source_zone, target_zone, kind)
                    raw_matched = matches_raw_contract(report)
                    passed = baseline_matched if args.mode == "characterize" else raw_matched
                    report.update(baselineMatched=baseline_matched,
                                  rawContractMatched=raw_matched,
                                  selectedModePassed=passed)
                    failed |= not passed
                    rows.append(report)
                    print(json.dumps(report), flush=True)
            except Exception as error:
                failed = True
                errors.append(str(error))
                print(f"INFRASTRUCTURE FAILURE: {error}", file=sys.stderr, flush=True)
    boundary_rows = []
    if args.mode == "regression":
        try:
            produced, source = worker(
                "boundary-max-local-write", "DateTimeCrossZone", directory,
                "Etc/UTC", "Local", date_case="max-local")
            if not produced["invariant"]:
                raise RuntimeError("max-local: same-process roundtrip control failed")
            report, _ = worker(
                "boundary-max-local-to-Asia-Tokyo", "DateTimeCrossZone", directory,
                "Asia/Tokyo", "Local", source, date_case="max-local")
            raw_matched = matches_raw_contract(report)
            report.update(boundaryCase="max-local", rawContractMatched=raw_matched,
                          selectedModePassed=raw_matched)
            failed |= not raw_matched
            boundary_rows.append(report)
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
    summary = dict(mode=args.mode, originalBaseline="acb160faa72a07835b01d049a2fbcf9070b061df",
                   dateTime=rows, dateTimeBoundary=boundary_rows, performance=performance,
                   infrastructureErrors=errors,
                   note=("Green regression means DateTime scalar, nullable and built-in collection paths preserve "
                         "raw ticks + Kind across zones, including a Local value one hour below DateTime.MaxValue "
                         "decoded in UTC+9; DateTimeOffset timings remain measurement evidence only."))
    (directory / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    return int(failed)


if __name__ == "__main__":
    sys.exit(main())
