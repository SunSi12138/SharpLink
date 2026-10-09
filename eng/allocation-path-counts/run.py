#!/usr/bin/env python3
"""One controlled path-observation contrast; no full cost-model or repair claim."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import statistics
import subprocess
import sys

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("gate", HERE.parent / "allocation-spread-diagnostics/run.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
BASE = gate.SOURCE
ALLOWED_OVERLAY = {
    "src/SharpLink.Runtime/SharpLink.Runtime.csproj",
    "src/SharpLink.Runtime/Transport/AllocationPathObservation.cs",
    "src/SharpLink.Runtime/Transport/SharedMemoryControlChannel.cs",
    "test/SharpLink.Benchmarks/AllocationReadProbe.cs",
    "src/SharpLink.Runtime/Transport/SharedMemoryTransport.cs",
    "test/SharpLink.Benchmarks/AllocationGateRunner.cs",
    "test/SharpLink.Benchmarks/AllocationPathCalibration.cs",
    "test/SharpLink.Benchmarks/Program.cs",
    "test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj",
}
KINDS = {"ClientRead", "ServerRead", "ControlPipeRead", "RpcStart", "CalibrationOuter", "CalibrationInner"}


def measured_paths(report):
    gate.check(report.get("observationCompiled") is True, "Observation symbol not present in measured build")
    results = []
    for case in report["cases"]:
        samples = []
        for sample in case["samples"]:
            before, after = sample["pathsBefore"], sample["pathsAfter"]
            left = {p["name"]: p for p in before["paths"]}
            right = {p["name"]: p for p in after["paths"]}
            gate.check(set(left) == set(right) == KINDS and len(before["paths"]) == len(after["paths"]) == len(KINDS),
                       "Missing or duplicate observation categories")
            delta = {}
            for name in KINDS:
                row = {k: right[name][k] - left[name][k] for k in
                       ("calls", "completed", "incomplete", "throws", "rootCalls", "rootBytes")}
                gate.check(all(isinstance(v, int) and v >= 0 for v in row.values()), "Counter regression")
                gate.check(row["completed"] + row["incomplete"] + row["throws"] == row["calls"] and
                           row["rootCalls"] <= row["calls"] and
                           (row["rootCalls"] > 0 or row["rootBytes"] == 0), "Counter identity broken")
                delta[name] = row
            gate.check(delta["RpcStart"]["calls"] == sample["completedOperations"], "RPC counter denominator mismatch")
            observed = sum(row["rootBytes"] for row in delta.values())
            operations = sample["completedOperations"]
            # Do not clamp a negative residual. Snapshots are wider than the original
            # process counter window and straddling work can make attribution unusable.
            residual = sample["allocatedBytes"] - observed
            stable = before["stable"] and after["stable"]
            samples.append({"index": sample["index"], "operations": operations,
                "processBytes": sample["allocatedBytes"], "callBoundaryBytes": observed,
                "unattributedResidualBytes": residual, "processBytesPerOperation": sample["bytesPerOperation"],
                "callBoundaryBytesPerOperation": observed / operations,
                "unattributedResidualBytesPerOperation": residual / operations,
                "stableSnapshots": stable, "nonnegativeResidual": residual >= 0,
                "newObserverThreadTouches": after["firstThreadTouches"] - before["firstThreadTouches"],
                "paths": delta})
        low = min(samples, key=lambda row: row["processBytes"])
        high = max(samples, key=lambda row: row["processBytes"])
        results.append({"name": case["name"], "samples": samples,
            "withinProcessLowHighContrast": {"lowSample": low["index"], "highSample": high["index"],
                "processDeltaBytesPerOperation": high["processBytesPerOperation"] - low["processBytesPerOperation"],
                "callBoundaryDeltaBytesPerOperation": high["callBoundaryBytesPerOperation"] - low["callBoundaryBytesPerOperation"],
                "unattributedDeltaBytesPerOperation": high["unattributedResidualBytesPerOperation"] - low["unattributedResidualBytesPerOperation"]}})
    return results


def read(path):
    return json.loads(path.read_text())


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    root = HERE.parents[1]
    baseline = Path(args.baseline).resolve()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    executions, failures = [], []
    summary = {"diagnosticOnly": True, "complete": False, "sourceCommit": BASE, "failures": failures,
        "fixedPlan": ["counter-self-test", "forced-sync-and-suspend-controls", "plain", "observed", "observed-negative"],
        "limitations": "Only synchronous same-thread setup inside observed call boundaries is attributed. Later async work, other threads, observer initialization, and boundary-straddling allocation remain explicit residual. Instrumented results are not budget certificates or a complete cost model."}
    try:
        gate.check(git(baseline, "rev-parse", "HEAD") == BASE, "Baseline is not the fixed source revision")
        gate.check(not git(root, "status", "--porcelain", "--untracked-files=no") and
                   not git(baseline, "status", "--porcelain", "--untracked-files=no"), "Dirty source tree")
        changed = set(git(root, "diff", "--name-only", BASE, "HEAD", "--", *gate.PROTECTED).splitlines())
        gate.check(changed == ALLOWED_OVERLAY, "Unexpected diagnostic runtime/test overlay")
        source_files = git(root, "ls-files", "--", *gate.PROTECTED).splitlines()
        baseline_files = git(baseline, "ls-files", "--", *gate.PROTECTED).splitlines()
        gate.write(output / "identity.json", {"baseline": BASE, "candidate": git(root, "rev-parse", "HEAD"),
            "compileProperty": "AllocationPathObservation=true", "overlayPaths": sorted(changed),
            "baselineFiles": {p: hashlib.sha256((baseline / p).read_bytes()).hexdigest() for p in baseline_files},
            "candidateFiles": {p: hashlib.sha256((root / p).read_bytes()).hexdigest() for p in source_files}})
        (output / "observer-source.patch").write_text(git(root, "diff", BASE, "HEAD", "--", *sorted(changed)) + "\n")
        dotnet = ["dotnet", "exec", "--fx-version", gate.RUNTIME]
        relative = "test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"
        observed = [*dotnet, str(root / relative)]
        plain = [*dotnet, str(baseline / relative)]
        for label, command in [("dotnet-info", ["dotnet", "--info"]), ("cpu", ["lscpu"])]:
            gate.check(gate.run(command, output, label, executions) == 0, label + " failed")
        for label, option in [("counter-self-test", "--allocation-path-self-test"),
                              ("forced-paths", "--allocation-path-calibration")]:
            path = output / (label + ".json")
            command = [*observed, option, str(path)]
            if label == "counter-self-test":
                command.append(str(baseline / relative))
            gate.check(gate.run(command, output, label, executions) == 0,
                       label + " failed; stop before contrast")
            gate.check(read(path).get("passed") is True, "Invalid calibration report")
        reports = {}
        for label, launcher, injection in [("plain", plain, 0), ("observed", observed, 0),
                                            ("observed-negative", observed, 512)]:
            path = output / (label + ".json")
            command = [*launcher, "--allocation-gate", "--budgets", str(root / "eng/perf/allocation-budgets.json"),
                       "--filter", ",".join(gate.CASES), "--output", str(path)]
            if injection:
                command += ["--inject-bytes-per-operation", str(injection)]
            status = gate.run(command, output, label, executions)
            try:
                report = read(path)
                passed = gate.validate_gate(report, status, injection)
                if label == "plain":
                    gate.check(not report.get("observationCompiled", False), "Baseline unexpectedly instrumented")
                else:
                    gate.write(output / (label + "-paths.json"), measured_paths(report))
                reports[label] = report
                summary[label] = {"valid": True, "passed": passed, "exitCode": status}
            except (ValueError, KeyError, TypeError, OSError) as error:
                failures.append(label + ": " + str(error))
        if {"plain", "observed"} <= reports.keys():
            summary["observerContrast"] = [{"name": a["name"], "plainMedian": a["medianBytesPerOperation"],
                "observedMedian": b["medianBytesPerOperation"],
                "medianDifference": b["medianBytesPerOperation"] - a["medianBytesPerOperation"],
                "plainSpread": a["spreadBytesPerOperation"], "observedSpread": b["spreadBytesPerOperation"],
                "plainMedianElapsedMilliseconds": statistics.median(s["elapsedMilliseconds"] for s in a["samples"]),
                "observedMedianElapsedMilliseconds": statistics.median(s["elapsedMilliseconds"] for s in b["samples"])}
                for a, b in zip(reports["plain"]["cases"], reports["observed"]["cases"])]
        summary["complete"] = not failures
    except Exception as error:
        failures.append(type(error).__name__ + ": " + str(error))
    finally:
        summary["conclusion"] = "Fixed diagnostic contrast only. Causal sufficiency and production repair remain unproven. No repeat is scheduled."
        gate.write(output / "summary.json", summary)
        gate.write(output / "files.json", {str(p.relative_to(output)): {"sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
                   "bytes": p.stat().st_size} for p in output.rglob("*") if p.is_file()})
        print(json.dumps(summary, indent=2))
    return 0 if summary["complete"] and summary.get("plain", {}).get("passed", False) else 1


if __name__ == "__main__":
    sys.exit(main())
