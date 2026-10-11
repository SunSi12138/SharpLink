#!/usr/bin/env python3
"""Stamp the standalone evidence binary only after checking its actual source inputs.

The disposable checkout must already be staged and `git write-tree` captured by
the collector. The supplied tree must describe the working production sources.
ModeIdentity is deliberately excluded from the measurement-content digest: it
contains that digest and the varying source identities, not executable logic.
"""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess


HARNESS = "test/SharpLink.FlowArchitectureEvidence"
CONTROLLER = "src/SharpLink.Runtime/StreamFlowController.cs"
INITIAL_DEV = "f8ba89121c87a6410a0139c44d7db30eab877f41"
BASELINE = "c7ba1768f31ce7219dc438dc6009347de6806ed5"
BASELINE_CONTROLLER_SHA256 = "7694add9ffc1d858c80f0aa8f1f8815717068b8ff7181b86998672e68bfe60e2"
# The B2 comparison shares the independently reproduced lease, snapshot and terminal fixes.
# Initial-dev performance is not silently combined with this correctness control.
FIXED_CONTROLLER_SHA256 = "0c1b1f2565697fb6dfa6097e16fdceda28d3ba4aba032ba28b3d8933ab223af4"
LINKED = [
    "test/SharpLink.Benchmarks/BenchmarkEnvironment.cs",
    "test/SharpLink.Benchmarks/ServerLifecycleTestExtensions.cs",
    "test/SharpLink.Benchmarks/AllocationReadProbe.cs",
    "test/SharpLink.Benchmarks/BenchmarkContracts.cs",
    "test/SharpLink.Benchmarks/BenchmarkService.cs",
]
BUILD_INPUTS = [
    "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
    "global.json", "NuGet.Config", "NuGet.config", "nuget.config",
]


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args])


def blob_sha(data):
    return hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def require_object(root, value, kind):
    if re.fullmatch(r"[0-9a-fA-F]{40}", value) is None:
        raise ValueError(f"Expected an explicit 40-digit Git {kind} SHA, got {value!r}")
    if git(root, "cat-file", "-t", value).decode().strip() != kind:
        raise ValueError(f"{value} is not a Git {kind}")


def tree_entries(root, ref, prefix):
    entries = {}
    for row in git(root, "ls-tree", "-r", "-z", ref, "--", prefix).split(b"\0"):
        if not row:
            continue
        metadata, path = row.split(b"\t", 1)
        mode, kind, digest = metadata.decode().split()
        if kind != "blob" or mode not in {"100644", "100755"}:
            raise ValueError(f"Unexpected non-file source input: {row!r}")
        entries[path.decode()] = digest
    return entries


def manifest_digest(root, paths):
    records = []
    digest = hashlib.sha256()
    for name in sorted(paths):
        data = (root / name).read_bytes()
        # Length-prefix both the path and contents so concatenation is unambiguous.
        encoded_name = name.encode()
        digest.update(len(encoded_name).to_bytes(8, "big"))
        digest.update(encoded_name)
        digest.update(len(data).to_bytes(8, "big"))
        digest.update(data)
        records.append({"path": name, "bytes": len(data), "sha256": sha256(data)})
    return digest.hexdigest(), records


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--baseline-sha", required=True)
    parser.add_argument("--candidate-sha", required=True)
    parser.add_argument("--runtime-source-tree-sha", required=True)
    parser.add_argument("--mode", required=True,
                        choices=["correctness-fixed-baseline", "B2-server-grant-4096"])
    parser.add_argument("--record", required=True, type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    baseline = args.baseline_sha.lower()
    candidate = args.candidate_sha.lower()
    runtime_tree = args.runtime_source_tree_sha.lower()
    require_object(root, baseline, "commit")
    require_object(root, candidate, "commit")
    require_object(root, runtime_tree, "tree")

    if baseline != BASELINE:
        raise ValueError("This pilot requires the fixed shared correctness control")
    require_object(root, INITIAL_DEV, "commit")
    old_source = git(root, "show", f"{INITIAL_DEV}:{CONTROLLER}")
    fixed_source = git(root, "show", f"{baseline}:{CONTROLLER}")
    if sha256(old_source) != BASELINE_CONTROLLER_SHA256 or sha256(fixed_source) != FIXED_CONTROLLER_SHA256:
        raise ValueError("Initial-dev or common lease-fix source mismatch")
    initial_entries = tree_entries(root, INITIAL_DEV, "src")
    baseline_entries = tree_entries(root, baseline, "src")
    expected_control = dict(initial_entries)
    expected_control[CONTROLLER] = blob_sha(fixed_source)
    bridge_path = "src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs"
    fixed_bridge = git(root, "show", f"{baseline}:{bridge_path}")
    if sha256(fixed_bridge) != "531753a3f2fb54f1a596b854d3af30cd4f3625eef3c2bcfc955794eb1dd64075":
        raise ValueError("Common snapshot-fix source mismatch")
    expected_control[bridge_path] = blob_sha(fixed_bridge)
    if baseline_entries != expected_control:
        raise ValueError("Correctness control contains another production change")

    approved_path = root / HARNESS / "B2ProductionManifest.json"
    approved = json.loads(approved_path.read_text())
    if approved.get("initialDevSha") != INITIAL_DEV or approved.get("correctnessControlSha") != BASELINE:
        raise ValueError("Reviewed candidate manifest uses different controls")
    changes = approved["changes"]
    required_changes = {CONTROLLER, "src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs",
                        "src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs",
                        "src/SharpLink.Runtime/StreamFlowController.SendGrants.cs"}
    if not required_changes.issubset(changes):
        raise ValueError("The reviewed manifest does not describe an integrated B2 producer path")
    if any(name not in required_changes and not (
            name.startswith("src/SharpLink.Runtime/StreamFlowController.SendGrants.") and name.endswith(".cs"))
           for name in changes):
        raise ValueError("Candidate changed a path outside the explicitly scoped B2 pilot")
    expected_entries = dict(baseline_entries)
    if args.mode == "correctness-fixed-baseline":
        if candidate != BASELINE:
            raise ValueError("The baseline label requires the exact common fix commit")
    else:
        for name, digest in changes.items():
            data = (root / name).read_bytes()
            if sha256(data) != digest:
                raise ValueError("Actual B2 source differs from reviewed bytes: " + name)
            expected_entries[name] = blob_sha(data)
    if tree_entries(root, candidate, "src") != expected_entries:
        raise ValueError("Compiled mode disagrees with the candidate commit's production tree")
    recorded_entries = tree_entries(root, runtime_tree, "src")
    if recorded_entries != expected_entries:
        raise ValueError("Runtime source tree differs from the recorded production inputs")
    for name, expected in expected_entries.items():
        if blob_sha((root / name).read_bytes()) != expected:
            raise ValueError("Working source disagrees with recorded input: " + name)
    existing_sources = {
        entry.decode() for entry in
        git(root, "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "src").split(b"\0")
        if entry
    }
    if existing_sources != set(expected_entries):
        raise ValueError("Unrecorded production sources are present")
    actual_controller = (root / CONTROLLER).read_bytes()

    # Existing service/codec fixture inputs and central package/build policy must also
    # be the baseline versions. The new harness itself is intentionally the same on A/B.
    build_inputs = [name for name in BUILD_INPUTS if (root / name).is_file()]
    for name in LINKED + build_inputs:
        original = git(root, "show", f"{baseline}:{name}")
        if (root / name).read_bytes() != original:
            raise ValueError(f"A shared baseline fixture/build input was modified: {name}")

    measurement_paths = set(LINKED + build_inputs + [HARNESS + "/B2ProductionManifest.json"])
    for path in (root / HARNESS).iterdir():
        if path.suffix in {".cs", ".csproj", ".py"} and path.name != "ModeIdentity.cs":
            measurement_paths.add(path.relative_to(root).as_posix())
    measurement_sha, measurement_manifest = manifest_digest(root, measurement_paths)
    pump_paths = [name for name in expected_entries
                  if Path(name).name.startswith("RpcSession.SendPump") and name.endswith(".cs")]
    if not pump_paths:
        raise ValueError("No production SendPump sources found")
    pump_sha, pump_manifest = manifest_digest(root, pump_paths)

    values = {
        "InitialDevBaselineSha": INITIAL_DEV,
        "CorrectnessControlSha": BASELINE,
        "Mode": args.mode,
        "BaselineSha": baseline,
        "CandidateSha": candidate,
        "RuntimeSourceTreeSha": runtime_tree,
        "MeasurementSourceSha": measurement_sha,
        "ControllerSourceSha256": sha256(actual_controller),
        "SendPumpSourceSha256": pump_sha,
    }
    lines = [
        "namespace SharpLink.Benchmarks;", "",
        "// Generated by stamp_identity.py after exact source/tree validation.",
        "// This file is compilation metadata; runtime arguments only verify these values.",
        "internal static class ModeIdentity", "{",
    ]
    lines.extend(f'    internal const string {key} = "{value}";' for key, value in values.items())
    lines.extend(["}", ""])
    identity_path = root / HARNESS / "ModeIdentity.cs"
    identity_path.write_text("\n".join(lines))
    record = {
        "schema": "sharplink-b2-server-grant-build-identity-v1",
        **values,
        "productionScheduler": "dev-SendPump",
        "b3ReadyWriterEnabled": False,
        "productionSourceFilesChecked": len(expected_entries),
        "onlyAllowedProductionDifference": [] if args.mode == "correctness-fixed-baseline" else sorted(changes),
        "modeVerification": "fixed-correctness-control-or-reviewed-integrated-B2-source-manifest",
        "b2ProducerGrantEnabled": args.mode == "B2-server-grant-4096",
        "approvedProductionManifestSha256": sha256(approved_path.read_bytes()),
        "runtimeSourceTreeVerification": "all-production-source-paths-and-working-blobs-match-recorded-tree",
        "measurementHashExcludes": [HARNESS + "/ModeIdentity.cs"],
        "measurementSourceManifest": measurement_manifest,
        "sendPumpSourceManifest": pump_manifest,
        "compiledIdentityFileSha256": sha256(identity_path.read_bytes()),
    }
    args.record.parent.mkdir(parents=True, exist_ok=True)
    args.record.write_text(json.dumps(record, indent=2) + "\n")
    print(json.dumps({key: record[key] for key in
                      ["Mode", "BaselineSha", "CandidateSha", "RuntimeSourceTreeSha",
                       "MeasurementSourceSha", "ControllerSourceSha256", "SendPumpSourceSha256"]}))


if __name__ == "__main__":
    main()
