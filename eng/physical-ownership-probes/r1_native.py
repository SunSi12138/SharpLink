#!/usr/bin/env python3
"""Frozen R1 mechanism comparison. Two fixed non-timed processes; no timing path."""
import argparse
import json
import os
from pathlib import Path
import shutil
import sys

from client_ablation import (JIT_METHODS, PGO_PREFLIGHT_OPERATIONS, clean_runtime_environment,
                            cpu_topology, dll_hashes, validate_native, validate_process_identity)
from paired_931 import CANDIDATE, SDK, capture, pin_snapshot, run, validate_runtime, verify_source, write
from tcp.run_matrix import execute_sample

R1 = "94adeae886c4ddfd0c02ec2dcb700cd97e9cad80"
REFERENCE_TOOL = "51f5bd0ec3f600196f2e26ea48a466ec2cea62d8"
REFERENCE_RUN = "40646151477"


def topology_for_host(affinity, sysfs=Path("/sys/devices/system/cpu"), procinfo=Path("/proc/cpuinfo")):
    # Some cloud containers omit sysfs entirely. Never mask partial/malformed sysfs.
    if sysfs.exists():
        return dict(cpu_topology(affinity, sysfs), source="sysfs")
    selected = [int(n) for n in affinity.split(",")]
    available = sorted(os.sched_getaffinity(0))
    if len(selected) != 2 or len(set(selected)) != 2 or not set(selected) <= set(available):
        raise ValueError("Two distinct allowed logical CPUs required")
    records = {}
    for block in procinfo.read_text().strip().split("\n\n"):
        fields = dict(line.split(":", 1) for line in block.splitlines() if ":" in line)
        fields = {k.strip(): v.strip() for k, v in fields.items()}
        cpu = int(fields["processor"])
        if cpu in records:
            raise ValueError("Duplicate procfs processor identity")
        package, core = int(fields["physical id"]), int(fields["core id"])
        if min(cpu, package, core) < 0:
            raise ValueError("Unknown procfs CPU identity")
        records[cpu] = {"cpu": cpu, "packageId": package, "coreId": core,
                        "model": fields.get("model name", "unreported")}
    if not set(available) <= records.keys():
        raise ValueError("Procfs does not identify every allowed CPU")
    rows = [records[cpu] for cpu in available]
    keys = {(records[cpu]["packageId"], records[cpu]["coreId"]) for cpu in selected}
    return {"source": "procfs (sysfs absent)", "available": rows, "selectedLogicalCpus": selected,
            "reportedTopology": "same reported package/core" if len(keys) == 1 else "distinct reported cores",
            "meaning": "Guest/container reported IDs only; not dedicated physical hardware or independently verified SMT topology."}


def compose(reference, r1_client, prepared):
    original = json.loads((reference / "client-prepared/prepared-manifest.json").read_text())
    plan = json.loads((reference / "client-ablation-plan.json").read_text())
    if (plan["toolRevision"], plan["newClientRevision"], plan["commonProductRevision"], plan["runtimeMode"]) != (REFERENCE_TOOL, CANDIDATE, CANDIDATE, "default-pgo"):
        raise ValueError("Wrong frozen reference inputs")
    base = reference / "client-prepared/candidate"
    if dll_hashes(base) != original["candidateBinaryHashes"]:
        raise ValueError("Reference snapshot differs from archived manifest")
    prepared.mkdir()
    for arm in ("baseline", "candidate"):
        shutil.copytree(base, prepared / arm)
        if arm == "candidate":
            shutil.copy2(r1_client, prepared / arm / "SharpLink.Client.dll")
            shutil.copy2(r1_client.with_suffix(".pdb"), prepared / arm / "SharpLink.Client.pdb")
        pin_snapshot(prepared / arm)
    hashes = {a: dll_hashes(prepared / a) for a in ("baseline", "candidate")}
    if hashes["baseline"].keys() != hashes["candidate"].keys():
        raise ValueError("DLL inventories differ")
    differences = [n for n in hashes["baseline"] if hashes["baseline"][n] != hashes["candidate"][n]]
    if differences != ["SharpLink.Client.dll"]:
        raise ValueError(f"Exactly Client must differ: {differences}")
    sources = {a: {Path(n).stem: R1 if a == "candidate" and n == "SharpLink.Client.dll" else CANDIDATE
                   for n in hashes[a] if n.startswith("SharpLink.")} for a in hashes}
    manifest = {"referenceRun": REFERENCE_RUN, "referenceTool": REFERENCE_TOOL,
                "referenceManifest": original, "productRevisions": sources,
                **{a + "BinaryHashes": h for a, h in hashes.items()}}
    write(prepared / "prepared-manifest.json", manifest)
    return manifest


def preflight(prepared, manifest, runtime, affinity, output):
    output.mkdir()
    environment = clean_runtime_environment(runtime, "default-pgo")
    gate = {"status": "Started", "arms": {}, "timedProcesses": 0}
    write(output / "gate.json", gate)
    # Attempt each predeclared arm once, retaining failures rather than selecting a retry.
    for arm in ("baseline", "candidate"):
        try:
            proof, listing = output / (arm + ".json"), output / (arm + "-jit.txt")
            command = ["taskset", "-c", affinity, str(runtime), str(prepared / arm / "PhysicalTcpProbe.dll"),
                       "dynamic-intercepted", "100", "1", "0", "0", str(proof), "preflight", "default-pgo"]
            current = dict(environment, DOTNET_JitDisasm=JIT_METHODS, DOTNET_JitStdOutFile=str(listing))
            code = execute_sample(command, output / (arm + ".log"), output / (arm + ".host.json"), arm, 0, current, 90)
            if code:
                raise ValueError(f"Process exit {code}")
            data = json.loads(proof.read_text())
            if (data["kind"], data["profile"], data["operations"]) != ("non-timed-preflight", "default-pgo", PGO_PREFLIGHT_OPERATIONS):
                raise ValueError("Fixed non-timed workload changed")
            request = validate_process_identity(data, arm, prepared, manifest, runtime, "default-pgo",
                                               product_revisions=manifest["productRevisions"][arm])
            native = validate_native(listing, request, "default-pgo")
            gate["arms"][arm] = {"status": "Passed", "requestType": request, "native": native}
        except Exception as error:
            gate["arms"][arm] = {"status": "Failed", "error": repr(error)}
        write(output / "gate.json", gate)
    gate["status"] = "Passed" if all(r["status"] == "Passed" for r in gate["arms"].values()) else "Failed"
    write(output / "gate.json", gate)
    if gate["status"] != "Passed":
        raise ValueError("R1 native gate failed; retained both original arm attempts")
    return gate


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("reference-evidence", "r1-source", "build-dotnet", "runtime-dotnet", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    r1 = args.r1_source.resolve(strict=True)
    reference = args.reference_evidence.resolve(strict=True)
    build = args.build_dotnet.resolve(strict=True)
    runtime = args.runtime_dotnet.resolve(strict=True)
    tool = capture(["git", "rev-parse", "HEAD"], root)
    verify_source(root, tool)
    verify_source(r1, R1)
    if capture([str(build), "--version"], root) != SDK:
        raise ValueError("Unexpected build SDK")
    runtimes = validate_runtime(runtime)
    cpus = sorted(os.sched_getaffinity(0))
    affinity = ",".join(map(str, cpus[:2]))
    topology = topology_for_host(affinity)
    write(output / "plan.json", {"toolRevision": tool, "referenceRun": REFERENCE_RUN,
        "referenceEvidence": str(reference), "referenceTool": REFERENCE_TOOL,
        "baselineClient": CANDIDATE, "candidateClient": R1, "commonProduct": CANDIDATE,
        "sdk": SDK, "runtimes": runtimes, "topology": topology,
        "operationsPerProcess": PGO_PREFLIGHT_OPERATIONS, "processes": 2, "timedProcesses": 0,
        "fixedPausesAfterOperations": [1024, 2048, 3072, 4096], "pauseMilliseconds": 250,
        "order": ["baseline", "candidate"], "runtimeMode": "default-pgo",
        "scope": "Native mechanism only. No performance or release acceptance; preflight Tier1 is process-local.",
        "failurePolicy": "One attempt per arm, no replacement, no extra warmup; retain every failure."})
    for name, command in (("sdk", [build, "--info"]), ("runtime", [runtime, "--info"]), ("cpu", ["cat", "/proc/cpuinfo"]), ("os", ["uname", "-a"])):
        run(command, output / (name + ".txt"), root)
    command = [build, "build", "src/SharpLink.Client/SharpLink.Client.csproj", "-c", "Release",
               "-m:1", "-nr:false", "-p:UseSharedCompilation=false", "--nologo"]
    write(output / "build-command.json", list(map(str, command)))
    run(command, output / "r1-build.log", r1)
    verify_source(r1, R1)
    prepared = output / "prepared"
    manifest = compose(reference, r1 / "src/SharpLink.Client/bin/Release/net10.0/SharpLink.Client.dll", prepared)
    preflight(prepared, manifest, runtime, affinity, output / "preflight")
    write(output / "complete.json", {"status": "Mechanism evidence complete, not performance acceptance",
                                    "toolRevision": tool, "nonTimedProcesses": 2, "timedProcesses": 0})


if __name__ == "__main__":
    main()
