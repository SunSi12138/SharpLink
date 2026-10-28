#!/usr/bin/env python3
"""Client-only PR931 diagnostic: gated real-TCP native preflight, then 12 fixed samples."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys

from paired_931 import BASELINE, CANDIDATE, pin_snapshot, run, verify_process, write
from tcp.run_matrix import execute_sample

SCENE = "dynamic-intercepted"
ITEMS = 100
ROUNDS = 6
PREFLIGHT_OPERATIONS = 256
PGO_PREFLIGHT_OPERATIONS = 4352
JIT_METHODS = ("*InterceptedAsyncEnumerable*:* *SharpLinkModuleRpcChannel*:* "
               "*PumpGeneratedOutboundStreamAsync*:* *PumpOutboundStreamAsync*:*")


def dll_hashes(root):
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.glob("*.dll"))}


def compose_client_pair(product_snapshots, destination):
    """Only Client may vary; the old Client runs on the new Runtime, never vice versa."""
    destination.mkdir()
    for arm in ("baseline", "candidate"):
        shutil.copytree(product_snapshots / "candidate", destination / arm)
    old_client = product_snapshots / "baseline" / "SharpLink.Client.dll"
    shutil.copy2(old_client, destination / "baseline" / old_client.name)
    old_symbols = old_client.with_suffix(".pdb")
    if old_symbols.is_file():
        shutil.copy2(old_symbols, destination / "baseline" / old_symbols.name)
    for arm in ("baseline", "candidate"):
        pin_snapshot(destination / arm)
    hashes = {arm: dll_hashes(destination / arm) for arm in ("baseline", "candidate")}
    if hashes["baseline"].keys() != hashes["candidate"].keys():
        raise ValueError("Hybrid DLL inventory differs")
    different = [name for name in hashes["baseline"] if hashes["baseline"][name] != hashes["candidate"][name]]
    if different != ["SharpLink.Client.dll"]:
        raise ValueError(f"Exactly Client must differ; actual differing DLLs: {different}")
    if hashes["baseline"]["SharpLink.Client.dll"] != dll_hashes(product_snapshots / "baseline")["SharpLink.Client.dll"]:
        raise ValueError("Old Client origin was not preserved")
    original = json.loads((product_snapshots / "prepared-manifest.json").read_text())
    manifest = {
        "experiment": "client-only hybrid diagnostic, not baseline-vs-candidate product acceptance",
        "productPreparation": original,
        "armSources": {"baseline": {"SharpLink.Client.dll": BASELINE, "otherProductDlls": CANDIDATE},
                       "candidate": {"SharpLink.Client.dll": CANDIDATE, "otherProductDlls": CANDIDATE}},
        "baselineBinaryHashes": hashes["baseline"], "candidateBinaryHashes": hashes["candidate"],
    }
    write(destination / "prepared-manifest.json", manifest)
    return manifest


def cpu_topology(affinity, sysfs=Path("/sys/devices/system/cpu")):
    selected = [int(n) for n in affinity.split(",")]
    available = sorted(os.sched_getaffinity(0))
    if len(selected) != 2 or len(set(selected)) != 2 or not set(selected) <= set(available):
        raise ValueError("Ablation requires two distinct currently allowed logical CPUs")
    rows = []
    for cpu in available:
        root = sysfs / f"cpu{cpu}" / "topology"
        rows.append({"cpu": cpu, "packageId": int((root / "physical_package_id").read_text()),
                     "coreId": int((root / "core_id").read_text()),
                     "threadSiblingsList": (root / "thread_siblings_list").read_text().strip()})
    if any(r["packageId"] < 0 or r["coreId"] < 0 for r in rows if r["cpu"] in selected):
        raise ValueError("Selected CPUs have unknown package/core topology")
    keys = {(r["packageId"], r["coreId"]) for r in rows if r["cpu"] in selected}
    return {"available": rows, "selectedLogicalCpus": selected,
            "reportedTopology": "same-core SMT siblings" if len(keys) == 1 else "distinct reported cores",
            "meaning": "Guest/runner-reported topology, not a guarantee of dedicated physical hardware."}


def clean_runtime_environment(runtime_dotnet, profile="fullopts"):
    environment = dict(os.environ, DOTNET_ROOT=str(runtime_dotnet.parent), DOTNET_MULTILEVEL_LOOKUP="0")
    for key in list(environment):
        if key.startswith(("DOTNET_Tiered", "COMPlus_Tiered", "DOTNET_Jit", "COMPlus_Jit",
                           "DOTNET_TC_", "COMPlus_TC_", "DOTNET_ReadyToRun", "COMPlus_ReadyToRun",
                           "DOTNET_ZapDisable", "COMPlus_ZapDisable")):
            del environment[key]
    if profile == "fullopts":
        environment["DOTNET_TieredCompilation"] = "0"
    return environment


def validate_process_identity(data, arm, prepared, manifest, runtime_dotnet, profile="fullopts"):
    verify_process(data, runtime_dotnet.parent)
    tiering = "0" if profile == "fullopts" else "default"
    if data["processorCount"] != 2 or data["tieredCompilation"] != tiering:
        raise ValueError("Process CPU count or tiering differs from the selected profile")
    if profile == "default-pgo" and data["tieredPgo"] != "default":
        raise ValueError("Process did not use default PGO")
    if (data["mode"], data["itemCount"], data["workers"], data["readyConnections"]) != (SCENE, ITEMS, 1, 1):
        raise ValueError("Process changed the fixed scene or connection count")
    expected = manifest[arm + "BinaryHashes"]
    # The ablation probe reports every DLL, not only product assemblies.
    if data["binaryHashes"] != expected:
        raise ValueError("Executing snapshot DLL identities differ from frozen composition")
    loaded = data["loadedAssemblies"]
    names = [assembly["name"] for assembly in loaded]
    required = {"SharpLink.Client", "SharpLink.Runtime", "SharpLink.Server", "SharpLink.Abstractions",
                "PhysicalTcpProbe", "ScalarStreamModule"}
    if not required <= set(names) or len(names) != len(set(names)):
        raise ValueError("Required assemblies were not uniquely loaded")
    for assembly in loaded:
        path = Path(assembly["location"])
        if path.parent != prepared / arm or path.name not in expected:
            raise ValueError(f"Assembly loaded outside frozen snapshot: {assembly}")
        if assembly["name"].startswith("SharpLink."):
            revision = BASELINE if arm == "baseline" and assembly["name"] == "SharpLink.Client" else CANDIDATE
            if assembly["productVersion"].split("+")[-1] != revision:
                raise ValueError(f"Loaded product source revision differs: {assembly}")
    wrappers = data["wrapperTypes"]
    if not data["interceptorObserved"] or not any("InterceptedAsyncEnumerable" in name for name in wrappers) or not any("PooledAsyncStreamDispatcher" in name for name in wrappers):
        raise ValueError("The real intercepted TCP dispatcher path was not observed")
    request = re.search(r"PhysicalTcp\.__IScalarStream_SharpLinkRequest_[A-F0-9]+", data["streamType"])
    if "InvokeServerStreamingDeferred" not in data["streamType"] or not request:
        raise ValueError("Dynamic stream did not use the generated TCP request type")
    return request[0]


def validate_native(path, request_type, profile="fullopts"):
    listing = path.read_text()
    targets = {
        "intercepted-iterator": lambda name: "InterceptedAsyncEnumerable`1+<GetAsyncEnumerator>" in name and "[int]" in name,
        "dynamic-iterator": lambda name: "SharpLinkModuleRpcChannel+<InvokeServerStreamingDeferred>" in name and f"[{request_type},int]" in name,
        "runtime-stream-pump": lambda name: "RpcSession+<PumpGeneratedOutboundStreamAsync>" in name and "[int]" in name,
        "server-stream-bridge": lambda name: "ServerGeneratedBridge+<PumpOutboundStreamAsync>" in name and "[int]" in name,
    }
    rows = []
    observed = set()
    for match in re.finditer(r"; Assembly listing for method (.*?) \(([^\n]+)\)\n(.*?); Total bytes of code (\d+)", listing, re.DOTALL):
        method, tier, body, size = match.groups()
        if not method.endswith(":MoveNext():this"):
            continue
        for target, predicate in targets.items():
            if not predicate(method):
                continue
            accepted = tier == "FullOpts" if profile == "fullopts" else tier in ("Tier1", "Tier1-OSR")
            if int(size) <= 0 or (profile == "fullopts" and not accepted):
                raise ValueError(f"Required target was not positive-size optimized native: {method}/{tier}/{size}")
            if accepted:
                observed.add(target)
            rows.append({"target": target, "method": method, "tier": tier, "bytes": int(size),
                         "acceptedOptimizedTier": accepted, "listingLine": listing.count("\n", 0, match.start()) + 1})
    if observed != set(targets):
        raise ValueError(f"Missing real-path native targets (never treated as zero): {sorted(set(targets) - observed)}")
    return rows


def validate_ticks(path, sample):
    data = json.loads(path.read_text())
    if data["stopwatchFrequency"] <= 0 or data["operations"] != sample["operations"] or data["workers"] != 1:
        raise ValueError("Raw tick metadata differs from the measured sample")
    rows = data["samples"]
    if len(rows) != 1 or rows[0]["worker"] != 0 or len(rows[0]["durationTicks"]) != sample["operations"]:
        raise ValueError("Raw per-request tick count is incomplete")
    if any(type(value) is not int or value < 0 for value in rows[0]["durationTicks"]):
        raise ValueError("Raw durations must be nonnegative integer Stopwatch ticks")


def validate_measurements(results, prepared, manifest, runtime_dotnet, profile="fullopts"):
    """Write immutable-input validation evidence before any sample enters a summary."""
    expected = {f"{SCENE}-n100-c1-r{r}-{arm}" for r in range(1, ROUNDS + 1) for arm in ("baseline", "candidate")}
    inventories = {
        "raw": {p.stem: p for p in (results / "runs").glob("*.json") if not p.name.endswith(".host.json")},
        "host": {p.name.removesuffix(".host.json"): p for p in (results / "runs").glob("*.host.json")},
        "ticks": {p.stem: p for p in (results / "ticks").glob("*.json")},
    }
    report = {"status": "Passed", "inventoryErrors": [], "samples": {}}
    for kind, paths in inventories.items():
        if paths.keys() != expected:
            report["inventoryErrors"].append({"kind": kind, "missing": sorted(expected - paths.keys()),
                                               "unexpected": sorted(paths.keys() - expected)})
    for name in sorted(expected):
        try:
            missing = [kind for kind, paths in inventories.items() if name not in paths]
            if missing:
                raise ValueError(f"Missing evidence: {', '.join(missing)}")
            sample = json.loads(inventories["raw"][name].read_text())
            validate_process_identity(sample, name.rsplit("-", 1)[1], prepared, manifest, runtime_dotnet, profile)
            validate_ticks(inventories["ticks"][name], sample)
            host = json.loads(inventories["host"][name].read_text())
            if host.get("exitCode") != 0 or host.get("status") != "success":
                raise ValueError(f"Process did not complete successfully: {host.get('status')}/{host.get('exitCode')}")
            report["samples"][name] = {"status": "Passed"}
        except (OSError, ValueError, KeyError, TypeError) as error:
            report["samples"][name] = {"status": "Failed", "reason": str(error)}
    if report["inventoryErrors"] or any(row["status"] != "Passed" for row in report["samples"].values()):
        report["status"] = "Failed"
    results.mkdir(exist_ok=True)
    write(results / "sample-validation.json", report)
    return report


def validate_summary(results):
    integrity = json.loads((results / "summary-integrity.json").read_text())
    summary = json.loads((results / "summary.json").read_text())
    if (integrity["failedSamples"] or integrity["incompletePairs"] or
            integrity["completedRawSamples"] != ROUNDS * 2):
        raise ValueError("Summary did not accept all 12 measured samples")
    if len(summary) != 1 or (summary[0]["mode"], summary[0]["items"], summary[0]["workers"], summary[0]["pairs"]) != (SCENE, ITEMS, 1, ROUNDS):
        raise ValueError("Summary does not contain exactly the fixed scene and six pairs")


def run_client_ablation(product_snapshots, output, runtime_dotnet, build_dotnet, affinity, tool_sha, profile="fullopts"):
    if profile not in ("fullopts", "default-pgo"):
        raise ValueError(f"Unknown client runtime profile: {profile}")
    preflight_operations = PREFLIGHT_OPERATIONS if profile == "fullopts" else PGO_PREFLIGHT_OPERATIONS
    source = Path(__file__).resolve().parent
    topology = cpu_topology(affinity)
    write(output / "cpu-topology.json", topology)
    plan = {"toolRevision": tool_sha, "scope": "Client-only increment diagnosis; not complete PR931 acceptance",
            "commonProductRevision": CANDIDATE, "oldClientRevision": BASELINE, "newClientRevision": CANDIDATE,
            "mode": SCENE, "items": ITEMS, "workers": 1, "pool": [1, 1], "runtimeMode": profile,
            "preflightProcesses": 2, "preflightOperationsPerProcess": preflight_operations,
            "preflightFixedPauseAfterOperations": [] if profile == "fullopts" else [1024, 2048, 3072, 4096],
            "preflightPauseMilliseconds": 0 if profile == "fullopts" else 250,
            "nativeRequirement": "FullOpts" if profile == "fullopts" else "Tier1 or Tier1-OSR for all four targets; lower/instrumented tiers retained but not accepted",
            "rounds": ROUNDS, "timedProcesses": 12, "warmupSeconds": 3, "measurementSeconds": 6,
            "armOrderByRound": [["baseline", "candidate"] if r % 2 else ["candidate", "baseline"] for r in range(1, ROUNDS + 1)],
            "affinity": affinity, "reportedCpuTopology": topology["reportedTopology"],
            "failurePolicy": "Failed native preflight prevents all timing. Timed schedule attempted once in full; no retry, replacement or extra samples. Any failure makes the job fail."}
    write(output / "client-ablation-plan.json", plan)
    prepared = output / "client-prepared"
    manifest = compose_client_pair(product_snapshots, prepared)
    run([build_dotnet, "build-server", "shutdown"], output / "build-server-shutdown.log", source.parent.parent)
    environment = clean_runtime_environment(runtime_dotnet, profile)
    preflight = output / "preflight"
    preflight.mkdir()
    gate = {"status": "Started", "arms": {}}
    write(output / "preflight-gate.json", gate)
    try:
        for arm in ("baseline", "candidate"):
            destination = preflight / (arm + ".json")
            listing = preflight / (arm + "-jit.txt")
            current = dict(environment, DOTNET_JitDisasm=JIT_METHODS, DOTNET_JitStdOutFile=str(listing))
            command = ["taskset", "-c", affinity, str(runtime_dotnet), str(prepared / arm / "PhysicalTcpProbe.dll"),
                       SCENE, str(ITEMS), "1", "0", "0", str(destination), "preflight", profile]
            exit_code = execute_sample(command, preflight / (arm + ".log"), preflight / (arm + ".host.json"), arm, 0, current, 90)
            if exit_code:
                raise RuntimeError(f"Native preflight {arm} failed with exit {exit_code}")
            data = json.loads(destination.read_text())
            if data["kind"] != "non-timed-preflight" or data["operations"] != preflight_operations:
                raise ValueError("Preflight did not complete its fixed non-timed workload")
            request_type = validate_process_identity(data, arm, prepared, manifest, runtime_dotnet, profile)
            rows = validate_native(listing, request_type, profile)
            gate["arms"][arm] = {"exitCode": exit_code, "requestType": request_type, "native": rows}
            write(output / "preflight-gate.json", gate)
    except Exception as error:
        gate.update(status="Failed", error=repr(error))
        write(output / "preflight-gate.json", gate)
        raise
    gate["status"] = "Passed"
    write(output / "preflight-gate.json", gate)
    write(output / "measurement-start.json", {"preflightGate": "Passed", "policy": "No builds or native disassembly during timing", "plannedProcesses": 12})
    results = output / "client-tcp"
    command = [sys.executable, source / "tcp/run_matrix.py", "--prepared", prepared, "--output", results,
               "--baseline-revision", f"client={BASELINE};common={CANDIDATE}",
               "--candidate-revision", f"client={CANDIDATE};common={CANDIDATE}",
               "--dotnet", runtime_dotnet, "--runtime-mode", "fullopts" if profile == "fullopts" else "default", "--rounds", str(ROUNDS),
               "--warmup", "3", "--seconds", "6", "--workers", "1", "--items", str(ITEMS),
               "--modes", SCENE, "--affinity", affinity, "--export-ticks", "--keep-going"]
    execution_error = None
    try:
        run(command, output / "client-tcp-run.log", source.parent.parent, environment)
    except Exception as error:
        execution_error = error
    # Successful and failed schedules use the same evidence gate. Raw files and
    # original process exit/status remain untouched; rejected samples are explicit.
    validation = validate_measurements(results, prepared, manifest, runtime_dotnet, profile)
    run([sys.executable, source / "tcp/summarize.py", "--input", results,
         "--validation-manifest", results / "sample-validation.json"],
        output / "client-tcp-summary.log", check=execution_error is None and validation["status"] == "Passed")
    if execution_error is not None:
        raise execution_error
    if validation["status"] != "Passed":
        raise ValueError("Measured evidence validation failed; inspect client-tcp/sample-validation.json")
    validate_summary(results)
    write(output / "complete.json", {"experiment": "client-only", "runtimeProfile": profile, "toolRevision": tool_sha,
                                      "preflightProcesses": 2, "timedProcesses": 12,
                                      "status": "Evidence complete; no performance acceptance threshold applied"})
