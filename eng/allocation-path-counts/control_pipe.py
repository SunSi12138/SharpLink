#!/usr/bin/env python3
"""One bounded public PipeStream timing calibration; never an RPC gate result."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys

spec = importlib.util.spec_from_file_location("paths", Path(__file__).with_name("run.py"))
paths = importlib.util.module_from_spec(spec)
spec.loader.exec_module(paths)
gate = paths.gate
OVERLAY = paths.ALLOWED_OVERLAY | {"test/SharpLink.Benchmarks/ControlPipeAllocationCalibration.cs"}


def validate(report):
    gate.check(report.get("diagnosticOnly") is True and report.get("rpcGateReproduction") is False,
               "Calibration must not claim RPC acceptance")
    gate.check(report.get("passed") is True and report.get("failure") is None and
               report.get("runtime") == gate.RUNTIME and report.get("operations") == 4000 and
               report.get("warmup") == 512, "Invalid calibration or runtime")
    rows = report["samples"]
    gate.check([(r["api"], r["pendingPercent"]) for r in rows] ==
               [(api, percent) for api in ("memory-valuetask", "array-task") for percent in (0, 25, 50, 75, 100)],
               "Fixed complete sample population changed")
    for row in rows:
        count = row["operations"]
        pending = row["incomplete"]
        before, after = row["before"], row["after"]
        left = next(p for p in before["paths"] if p["name"] == "ControlPipeRead")
        right = next(p for p in after["paths"] if p["name"] == "ControlPipeRead")
        gate.check(before["stable"] and after["stable"] and
                   right["calls"] - left["calls"] == right["rootCalls"] - left["rootCalls"] == count and
                   right["incomplete"] - left["incomplete"] == pending and
                   right["completed"] - left["completed"] == count - pending and
                   right["throws"] == left["throws"] and
                   right["rootBytes"] - left["rootBytes"] == row["observedCallBytes"] and
                   after["firstThreadTouches"] - before["firstThreadTouches"] == row["newObserverThreadTouches"],
                   "Raw snapshot identity failed")
        gate.check(count == 4000 and pending == count * row["pendingPercent"] // 100 and
                   row["rawCallBytes"] == row["observedCallBytes"] and row["rawCallBytes"] >= 0 and
                   row["stableSnapshots"] is True and row["newObserverThreadTouches"] >= 0 and
                   row["processBytes"] - row["observedCallBytes"] == row["unattributedResidualBytes"],
                   "Call accounting or forced completion identity failed")
    lifecycle = report["lifecycle"]
    gate.check([row["api"] for row in lifecycle] == ["memory-valuetask", "array-task"], "Missing lifecycle API")
    for row in lifecycle:
        gate.check(all(row.get(k) is True for k in
                   ("cancellationPreserved", "reuseAfterCancellation", "disposalJoinedPendingRead")), "Lifecycle control failed")
        gate.check(row["disposalOutcome"] in ("end-of-stream", "ObjectDisposedException", "IOException", "OperationCanceledException", "TaskCanceledException"),
                   "Unexpected disposal outcome")
    result = []
    for api in ("memory-valuetask", "array-task"):
        samples = [row for row in rows if row["api"] == api]
        values = [row["rawCallBytes"] / row["operations"] for row in samples]
        result.append({"api": api, "fixedMixCallSpreadBytesPerRead": max(values) - min(values),
                       "allReadyBytesPerRead": values[0], "allPendingBytesPerRead": values[-1],
                       "supportsPredictedZeroOr144ByteModel": all(row["rawCallBytes"] == row["incomplete"] * 144 for row in samples),
                       "rpcGateReproduction": False})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    baseline = Path(args.baseline).resolve()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    executions, failures = [], []
    summary = {"diagnosticOnly": True, "rpcGateReproduction": False, "complete": False, "failures": failures}
    try:
        gate.check(paths.git(baseline, "rev-parse", "HEAD") == paths.BASE, "Unexpected baseline")
        gate.check(not paths.git(root, "status", "--porcelain", "--untracked-files=no") and
                   not paths.git(baseline, "status", "--porcelain", "--untracked-files=no"), "Dirty source")
        changed = set(paths.git(root, "diff", "--name-only", paths.BASE, "HEAD", "--", *gate.PROTECTED).splitlines())
        gate.check(changed == OVERLAY, "Unexpected source overlay")
        gate.write(output / "identity.json", {"baseline": paths.BASE, "candidate": paths.git(root, "rev-parse", "HEAD"),
            "compileProperty": "AllocationPathObservation=true", "overlayPaths": sorted(changed),
            "baselineFiles": {p: hashlib.sha256((baseline / p).read_bytes()).hexdigest()
                              for p in paths.git(baseline, "ls-files", "--", *gate.PROTECTED).splitlines()},
            "candidateFiles": {p: hashlib.sha256((root / p).read_bytes()).hexdigest()
                               for p in paths.git(root, "ls-files", "--", *gate.PROTECTED).splitlines()}})
        (output / "observer-source.patch").write_text(paths.git(root, "diff", paths.BASE, "HEAD", "--", *sorted(changed)) + "\n")
        dll = "test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"
        launch = ["dotnet", "exec", "--fx-version", gate.RUNTIME, str(root / dll)]
        for label, command in [("dotnet-info", ["dotnet", "--info"]), ("cpu", ["lscpu"]),
            ("counter-self-test", [*launch, "--allocation-path-self-test", str(output / "counter-self-test.json"), str(baseline / dll)]),
            ("control-pipe", [*launch, "--control-pipe-allocation-calibration", str(output / "control-pipe.json")])]:
            gate.check(gate.run(command, output, label, executions) == 0, label + " failed; no repeat")
            if label == "counter-self-test":
                gate.check(paths.read(output / "counter-self-test.json")["passed"] is True, "Counter self-test failed")
        summary["calibration"] = validate(paths.read(output / "control-pipe.json"))
        summary["complete"] = True
    except Exception as error:
        failures.append(type(error).__name__ + ": " + str(error))
    finally:
        summary["conclusion"] = "Public API timing calibration only. Historical RPC failure and safe production repair remain unproven; no further run scheduled."
        gate.write(output / "summary.json", summary)
        gate.write(output / "files.json", {str(p.relative_to(output)): {"sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
            "bytes": p.stat().st_size} for p in output.rglob("*") if p.is_file()})
        print(json.dumps(summary, indent=2))
    return 0 if summary["complete"] else 1


if __name__ == "__main__":
    sys.exit(main())
