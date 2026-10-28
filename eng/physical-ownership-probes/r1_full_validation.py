"""Offline full-product integrity gate; never mutates raw evidence or thresholds."""
import json
import math
from pathlib import Path

from paired_931 import BASELINE, verify_process, write
from client_ablation import validate_ticks

CANDIDATE = "94adeae886c4ddfd0c02ec2dcb700cd97e9cad80"
SCENES = {(mode, count, 1) for mode in ("static-intercepted", "dynamic-intercepted") for count in (1, 100)}
METRICS = ("operationsPerSecond", "p50Us", "p99Us", "p999Us", "allocatedBytesPerOperation", "cpuUsPerOperation")


def validate_process(data, arm, prepared, hashes, runtime):
    verify_process(data, runtime.parent)
    if (data["processorCount"], data["tieredCompilation"], data["tieredPgo"], data["readyToRun"]) != (2, "default", "default", "default"):
        raise ValueError("Unexpected affinity/default tiering/PGO")
    if (data["mode"], data["itemCount"], data["workers"]) not in SCENES:
        raise ValueError("Unexpected original scene")
    if (data["warmupSeconds"], data["requestedSeconds"], data["readyConnections"], data["connectionPoolMin"], data["connectionPoolMax"]) != (3, 6, 1, 1, 1):
        raise ValueError("Timing/connection contract changed")
    if (data["transport"], data["requestTimeout"], data["heartbeatIntervalSeconds"], data["serverInterceptors"], data["clientInterceptors"]) != ("tcp-loopback", "disabled", 3600, 0, 1):
        raise ValueError("Transport/interceptor contract changed")
    if data["validationFailures"] != 0 or type(data["operations"]) is not int or data["operations"] <= 0:
        raise ValueError("Missing successful operations")
    if data["binaryHashes"] != hashes[arm]:
        raise ValueError("Executing DLL inventory differs from frozen snapshot")
    expected = BASELINE if arm == "baseline" else CANDIDATE
    if data["sourceRevision"] != expected:
        raise ValueError("Wrong declared source")
    for metric in METRICS:
        value = data[metric]
        if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
            raise ValueError(f"Invalid metric {metric}")
    loaded = data["loadedAssemblies"]
    names = [a["name"] for a in loaded]
    required = {"SharpLink.Client", "SharpLink.Runtime", "SharpLink.Server", "SharpLink.Abstractions", "PhysicalTcpProbe", "ScalarStreamModule"}
    if not required <= set(names) or len(names) != len(set(names)):
        raise ValueError("Loaded assembly identities incomplete or duplicated")
    for assembly in loaded:
        path = Path(assembly["location"])
        if path.parent != prepared / arm or path.name not in hashes[arm] or not assembly["mvid"]:
            raise ValueError("Assembly escaped frozen snapshot or missing MVID")
        if assembly["name"].startswith("SharpLink.") and assembly["productVersion"].split("+")[-1] != expected:
            raise ValueError("Loaded full-product source differs")
    if not data["interceptorObserved"] or not any("InterceptedAsyncEnumerable" in t for t in data["wrapperTypes"]):
        raise ValueError("Actual intercepted path not observed")
    dynamic = data["mode"] == "dynamic-intercepted"
    if dynamic != ("ModuleRpcChannel" in data["channelType"]) or dynamic != ("InvokeServerStreamingDeferred" in data["streamType"]):
        raise ValueError("Wrong static/dynamic path")


def validate_measurements(results, prepared, hashes, runtime):
    expected = {f"{mode}-n{count}-c1-r{round_number}-{arm}"
                for mode, count, _ in SCENES for round_number in range(1, 7) for arm in ("baseline", "candidate")}
    inventories = {
        "raw": {p.stem: p for p in (results / "runs").glob("*.json") if not p.name.endswith(".host.json")},
        "host": {p.name.removesuffix(".host.json"): p for p in (results / "runs").glob("*.host.json")},
        "ticks": {p.stem: p for p in (results / "ticks").glob("*.json")},
    }
    report = {"status": "Passed", "inventoryErrors": [], "samples": {}}
    for kind, paths in inventories.items():
        if paths.keys() != expected:
            report["inventoryErrors"].append({"kind": kind, "missing": sorted(expected-paths.keys()), "unexpected": sorted(paths.keys()-expected)})
    for name in sorted(expected):
        try:
            if any(name not in paths for paths in inventories.values()):
                raise ValueError("Missing planned raw/host/tick evidence (never replaced)")
            arm = name.rsplit("-", 1)[1]
            data = json.loads(inventories["raw"][name].read_text())
            mode, count_workers = name.split("-r", 1)[0].rsplit("-n", 1)
            if (data["mode"], data["itemCount"], data["workers"]) != (mode, int(count_workers.split("-c")[0]), 1):
                raise ValueError("Scenario differs from scheduled identity")
            validate_process(data, arm, prepared, hashes, runtime)
            validate_ticks(inventories["ticks"][name], data)
            host = json.loads(inventories["host"][name].read_text())
            if host.get("status") != "success" or host.get("exitCode") != 0:
                raise ValueError("Process did not complete successfully, including shutdown")
            report["samples"][name] = {"status": "Passed"}
        except (OSError, KeyError, TypeError, ValueError) as error:
            report["samples"][name] = {"status": "Failed", "reason": str(error)}
    if report["inventoryErrors"] or any(r["status"] != "Passed" for r in report["samples"].values()):
        report["status"] = "Failed"
    results.mkdir(exist_ok=True)
    write(results / "sample-validation.json", report)
    return report


def validate_summary(results):
    integrity = json.loads((results / "summary-integrity.json").read_text())
    rows = json.loads((results / "summary.json").read_text())
    if integrity["evidenceValidation"] != "Passed" or integrity["inventoryErrors"] or integrity["failedSamples"] or integrity["incompletePairs"] or integrity["completedRawSamples"] != 48:
        raise ValueError("All48 original schedule inputs are required")
    if len(rows) != 4 or {(r["mode"], r["items"], r["workers"]) for r in rows} != SCENES or any(r["pairs"] != 6 for r in rows):
        raise ValueError("All four original scenes require six complete pairs")
