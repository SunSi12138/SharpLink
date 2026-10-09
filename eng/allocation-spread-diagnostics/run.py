#!/usr/bin/env python3
"""One fixed diagnostic sequence. A traced pass never replaces an untraced failure."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import signal
import statistics
import subprocess
import sys
import time

SOURCE = "e91f82118bb4d67997bc76b8a579d51460a28c1b"
RUNTIME = "10.0.12"
CASES = {"rpc-add-sharedmemory-c1": (1, 4000, 1450, 50),
         "rpc-add-sharedmemory-c8": (8, 4096, 650, 90)}
PROVIDERS = "Microsoft-Windows-DotNETRuntime:0x1:5,SharpLink-Allocation-Calibration:0xFFFFFFFFFFFFFFFF:5"
PROTECTED = ["src", "test", "eng/perf/allocation-budgets.json", "eng/run-allocation-gate.sh",
             "Directory.Build.props", "Directory.Packages.props", "global.json"]


def write(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def check(condition, message):
    if not condition:
        raise ValueError(message)


def finite(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def validate_gate(report, status, injected):
    check(status in (0, 1), "Gate failed to execute normally")
    check(report["runtimeVersion"] == RUNTIME and report["runtimeMajor"] == 10, "Unexpected runtime")
    check(report["mode"] == "gate" and report["configuration"] == "Release", "Unexpected gate mode/build")
    check(report["injectedBytesPerOperation"] == injected, "Wrong negative-control input")
    check(report["filter"] == ",".join(CASES), "Wrong case filter")
    check(len(report["cases"]) == 2 and {c["name"] for c in report["cases"]} == set(CASES), "Missing or duplicate cases")
    for case in report["cases"]:
        concurrency, operations, maximum, spread_max = CASES[case["name"]]
        check(case["concurrency"] == concurrency and case["warmupOperations"] == 512 and
              case["operationsPerSample"] == operations, "Changed measurement workload")
        check(case["sampleCount"] == 5 and len(case["samples"]) == 5, "Changed sample count")
        check(case["maxBytesPerOperation"] == maximum and case["maxSpreadBytesPerOperation"] == spread_max,
              "Changed budget")
        points = []
        for index, sample in enumerate(case["samples"]):
            check(sample["index"] == index and sample["completedOperations"] == operations,
                  "Incomplete or reordered sample")
            check(finite(sample["allocatedBytes"]) and sample["allocatedBytes"] >= 0 and
                  finite(sample["bytesPerOperation"]) and
                  sample["bytesPerOperation"] == sample["allocatedBytes"] / operations, "Invalid allocation denominator")
            check(finite(sample["elapsedMilliseconds"]) and sample["elapsedMilliseconds"] > 0, "Invalid elapsed time")
            before, after = sample["diagnosticsBefore"], sample["diagnosticsAfter"]
            check(before["timestamp"] < after["timestamp"], "Nonmonotonic diagnostics")
            for name in ("gen0Collections", "gen1Collections", "gen2Collections", "completedWorkItems"):
                check(0 <= before[name] <= after[name], "Invalid cumulative diagnostic")
            for snapshot in (before, after):
                check(snapshot["threadCount"] >= 0 and snapshot["pendingWorkItems"] >= 0 and
                      isinstance(snapshot["timestampUtc"], str), "Missing scheduling diagnostics")
            points.append(sample["bytesPerOperation"])
        median, spread = statistics.median(points), max(points) - min(points)
        check(case["medianBytesPerOperation"] == median and case["spreadBytesPerOperation"] == spread and
              case["minBytesPerOperation"] == min(points) and case["maxBytesPerOperationObserved"] == max(points),
              "Summary disagrees with all samples")
        passed = median <= maximum and spread <= spread_max
        check(case["passed"] is passed, "Budget decision changed")
        if injected:
            check(median > maximum and not passed, "Injected allocation must exceed each median budget")
    passed = all(case["passed"] for case in report["cases"])
    check(report["passed"] is passed and status == (0 if passed else 1), "Exit status disagrees with gate")
    return passed


def run(command, directory, label, executions, timeout=300):
    log = directory / (label + ".log")
    started = time.time()
    with log.open("w", encoding="utf-8") as stream:
        process = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            status = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
            status = 124
    executions.append({"label": label, "command": command, "exitCode": status,
                       "elapsedSeconds": time.time() - started, "launcherPid": process.pid})
    write(directory / "execution-index.json", executions)
    return status


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trace", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    executions, failures = [], []
    summary = {"diagnosticOnly": True, "fixedPlan": ["self-test", "untraced", "negative-control", "calibration", "traced"],
               "sourceCommit": SOURCE, "runtime": RUNTIME, "complete": False, "failures": failures}
    try:
        head = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
        subprocess.run(["git", "diff", "--exit-code", SOURCE, "--", *PROTECTED], check=True)
        check(not subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], text=True),
              "Working tree must be clean")
        tracked = subprocess.check_output(["git", "ls-files", "--", *PROTECTED], text=True).splitlines()
        write(output / "identity.json", {"sourceCommit": SOURCE, "validationCommit": head,
              "platform": platform.platform(), "cpuCount": os.cpu_count(),
              "workflowRunId": os.environ.get("GITHUB_RUN_ID"), "workflowRunAttempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
              "files": {name: hashlib.sha256(Path(name).read_bytes()).hexdigest() for name in tracked}})
        for label, command in [("dotnet-info", ["dotnet", "--info"]), ("trace-version", [args.trace, "--version"]),
                               ("cpu", ["lscpu"])]:
            check(run(command, output, label, executions) == 0, label + " failed")
        benchmark = str(Path("test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll").resolve())
        probe = str(Path("eng/allocation-spread-diagnostics/bin/Release/net10.0/TraceProbe.dll").resolve())
        dotnet = ["dotnet", "exec", "--fx-version", RUNTIME]
        check(run([*dotnet, benchmark, "--allocation-gate-self-test", "--output", str(output / "self-test.json")],
                  output, "self-test", executions) == 0, "Gate self-test failed")
        check(json.loads((output / "self-test.json").read_text())["passed"] is True, "Invalid self-test result")
        gate_args = [*dotnet, benchmark, "--allocation-gate", "--filter", ",".join(CASES)]
        # No conditional repeats. Both controls use fresh processes and unchanged workloads.
        for label, injected in [("untraced", 0), ("negative-control", 512)]:
            path = output / (label + ".json")
            command = [*gate_args, "--output", str(path)]
            if injected:
                command += ["--inject-bytes-per-operation", str(injected)]
            status = run(command, output, label, executions)
            try:
                passed = validate_gate(json.loads(path.read_text()), status, injected)
                summary[label] = {"valid": True, "passed": passed, "exitCode": status}
            except (KeyError, ValueError, OSError, TypeError) as error:
                failures.append(label + ": " + str(error))
        trace = [args.trace, "collect", "--show-child-io", "--providers", PROVIDERS, "--buffersize", "256"]
        calibration_trace = output / "calibration.nettrace"
        status = run([*trace, "--output", str(calibration_trace), "--", *dotnet, probe, "calibrate"],
                     output, "calibration", executions)
        check(status == 0, "Calibration collection failed")
        check(run([*dotnet, probe, "parse", str(calibration_trace), "calibration", str(output / "calibration-parsed.json")],
                  output, "parse-calibration", executions) == 0, "Trace calibration failed; stop before RPC trace")
        path = output / "traced.json"
        trace_path = output / "rpc.nettrace"
        status = run([*trace, "--output", str(trace_path), "--", *gate_args, "--output", str(path)],
                     output, "traced", executions)
        passed = validate_gate(json.loads(path.read_text()), status, 0)
        summary["traced"] = {"valid": True, "passed": passed, "exitCode": status}
        check(run([*dotnet, probe, "parse", str(trace_path), str(path), str(output / "rpc-parsed.json")],
                  output, "parse-traced", executions) == 0, "RPC trace attribution invalid/inconclusive")
        summary["complete"] = not failures
    except Exception as error:
        failures.append(type(error).__name__ + ": " + str(error))
    finally:
        summary["untracedGatePassed"] = summary.get("untraced", {}).get("passed", False)
        summary["conclusion"] = "Diagnostic evidence only; root cause and repair remain unproven."
        write(output / "summary.json", summary)
        files = {str(path.relative_to(output)): {"sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                 "bytes": path.stat().st_size} for path in output.rglob("*") if path.is_file()}
        write(output / "files.json", files)
        print(json.dumps(summary, indent=2))
    # Preserve a baseline failure even if the traced population happens to pass.
    return 0 if summary["complete"] and summary["untracedGatePassed"] else 1


if __name__ == "__main__":
    sys.exit(main())
