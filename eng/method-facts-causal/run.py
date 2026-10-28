#!/usr/bin/env python3
"""Run one fixed campaign; never build, retry, or replace samples."""
import datetime
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import threading
import time
from runtime_contract import validate_metadata
from spec import CALLS, CELLS, CONTROLLED, SAMPLES, validate_schedule


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def capture(command):
    result = subprocess.run(command, text=True, capture_output=True)
    return {"command": command, "exitCode": result.returncode, "stdout": result.stdout, "stderr": result.stderr}


def snapshot():
    result = {"utc": utc(), "loadAverage": os.getloadavg(),
              "processes": capture(["ps", "-axo", "pid,ppid,%cpu,etime,comm"])}
    if platform.system() == "Darwin":
        result.update(battery=capture(["pmset", "-g", "batt"]), powerMode=capture(["pmset", "-g", "custom"]))
    elif platform.system() == "Linux":
        result["linux"] = {str(p): p.read_text() for p in
                           [Path("/proc/loadavg"), Path("/proc/stat"), Path("/proc/pressure/cpu"),
                            Path("/sys/fs/cgroup/cpu.stat")] if p.exists()}
    return result


def run_campaign(root):
    manifest = json.loads((root / "manifest.json").read_text())
    profile, _ = validate_schedule(manifest)
    environment = None
    if profile == CONTROLLED:
        if not (root / "preparation-complete.json").is_file():
            raise ValueError("All builds, source checks and non-timing preflights must complete before timing")
        for path, frozen in manifest["runtimeConfigs"].items():
            if json.loads(Path(path).read_text()) != frozen:
                raise ValueError("Runtime configuration changed after preparation")
        environment = dict(os.environ, **manifest["executionEnvironment"])
    ledger = (root / "execution.jsonl").open("x")
    monitor_file = (root / "environment-during.jsonl").open("x")
    stop = threading.Event()
    monitoring_errors = []

    def monitor():
        while not stop.is_set():
            try:
                observation = snapshot()
            except Exception as error:
                observation = {"utc": utc(), "monitoringFailure": str(error)}
                monitoring_errors.append(observation)
            monitor_file.write(json.dumps(observation) + "\n")
            monitor_file.flush()
            stop.wait(5)

    worker = threading.Thread(target=monitor, daemon=True)
    worker.start()
    all_complete = False
    try:
        for process in manifest["processes"]:
            prefix = root / process["name"]
            record = {"name": process["name"], "arm": process["arm"], "command": process["command"],
                      "cwd": process["cwd"], "startedUtc": utc()}
            start = time.monotonic()
            with prefix.with_suffix(".stdout").open("x") as out, prefix.with_suffix(".stderr").open("x") as err:
                try:
                    result = subprocess.run(process["command"], cwd=process["cwd"], env=environment,
                                            stdout=out, stderr=err, timeout=180)
                    record["exitCode"] = result.returncode
                except OSError as error:
                    record["exitCode"] = None
                    record["failure"] = f"Process could not start: {error}"
                except subprocess.TimeoutExpired:
                    record["exitCode"] = None
                    record["failure"] = "180-second process watchdog expired; no replacement permitted"
            record["endedUtc"] = utc()
            record["durationSeconds"] = time.monotonic() - start
            rows = []
            try:
                sample_path = Path(process["samples"])
                if sample_path.exists():
                    rows = [json.loads(line) for line in sample_path.read_text().splitlines()]
                expected = [(cell, sample) for cell in CELLS for sample in range(SAMPLES)]
                valid = (record["exitCode"] == 0 and [(r["cell"], r["sample"]) for r in rows] == expected
                         and all(r["completed"] == CALLS and len(r["ticks"]) == CALLS and r["failure"] is None
                                 and r["frequency"] > 0 and r["elapsedTicks"] > 0 and all(t > 0 for t in r["ticks"]) for r in rows))
                if profile == CONTROLLED:
                    messages = [json.loads(line) for line in prefix.with_suffix(".stdout").read_text().splitlines()]
                    validate_metadata(messages[0]["metadata"], manifest)
                    if messages[0]["metadata"] != manifest["metadata"][process["arm"]]:
                        raise ValueError("Actual loaded modules/runtime differ from frozen describe record")
                    if messages[-1] != {"complete": True, "samples": 20, "completedCalls": 40000}:
                        raise ValueError("Process did not report complete inventory")
            except (ValueError, KeyError, IndexError, TypeError) as error:
                valid = False
                record["validationFailure"] = str(error)
            if monitoring_errors:
                valid = False
                record["monitoringFailure"] = monitoring_errors[0]
            record["samplesPresent"] = len(rows)
            record["completedCalls"] = sum(row.get("completed", 0) for row in rows)
            record["valid"] = valid
            ledger.write(json.dumps(record) + "\n")
            ledger.flush()
            print(json.dumps({key: record[key] for key in ["name", "exitCode", "valid", "completedCalls"]}), flush=True)
            if not valid:
                break
        else:
            all_complete = True
    finally:
        stop.set()
        worker.join()
        monitor_file.close()
        ledger.close()
        (root / "completion.json").write_text(json.dumps({"complete": all_complete, "endedUtc": utc()}, indent=2))
    return all_complete


if __name__ == "__main__":
    sys.exit(0 if run_campaign(Path(sys.argv[1]).resolve()) else 1)
