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
BASELINE = "f8ba89121c87a6410a0139c44d7db30eab877f41"
BASELINE_CONTROLLER_SHA256 = "7694add9ffc1d858c80f0aa8f1f8815717068b8ff7181b86998672e68bfe60e2"
# Result of eng/issue735/apply_split_gate.py on the pinned baseline. A different
# candidate must update this reviewed digest; an unknown source can never be relabelled.
SPLIT_CONTROLLER_SHA256 = "a9cad11d9f80d8aedce4d412ada02ade32cd8048e0cfe133ad469d029eaf658d"
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
                        choices=["baseline", "split-gate", "D-split-direction-gates"])
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
        raise ValueError("The supplied baseline does not match the audited current-dev baseline")
    baseline_controller = git(root, "show", f"{baseline}:{CONTROLLER}")
    if sha256(baseline_controller) != BASELINE_CONTROLLER_SHA256:
        raise ValueError("The pinned baseline controller source digest does not match")
    actual_controller = (root / CONTROLLER).read_bytes()
    expected_controller_sha = (BASELINE_CONTROLLER_SHA256 if args.mode == "baseline"
                               else SPLIT_CONTROLLER_SHA256)
    if sha256(actual_controller) != expected_controller_sha:
        raise ValueError("Compiled mode disagrees with actual controller bytes; refusing silent fallback")

    baseline_entries = tree_entries(root, baseline, "src")
    recorded_entries = tree_entries(root, runtime_tree, "src")
    if not baseline_entries or baseline_entries.keys() != recorded_entries.keys():
        raise ValueError("Production source paths differ from the audited baseline")
    expected_entries = dict(baseline_entries)
    expected_entries[CONTROLLER] = blob_sha(actual_controller)
    if args.mode != "baseline" and tree_entries(root, candidate, "src") != expected_entries:
        raise ValueError("The candidate commit does not contain the actual D production sources")
    for name, expected in expected_entries.items():
        if recorded_entries[name] != expected:
            raise ValueError(f"Runtime source tree includes an unexpected production change: {name}")
        if blob_sha((root / name).read_bytes()) != expected:
            raise ValueError(f"Working source disagrees with the recorded tree: {name}")
    existing_sources = {
        entry.decode() for entry in
        git(root, "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "src").split(b"\0")
        if entry
    }
    if existing_sources != set(expected_entries):
        raise ValueError("Unrecorded added/deleted production source files are present")

    # Existing service/codec fixture inputs and central package/build policy must also
    # be the baseline versions. The new harness itself is intentionally the same on A/B.
    build_inputs = [name for name in BUILD_INPUTS if (root / name).is_file()]
    for name in LINKED + build_inputs:
        original = git(root, "show", f"{baseline}:{name}")
        if (root / name).read_bytes() != original:
            raise ValueError(f"A shared baseline fixture/build input was modified: {name}")

    measurement_paths = set(LINKED + build_inputs)
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
        "schema": "sharplink-flow-architecture-build-identity-v1",
        **values,
        "productionScheduler": "dev-SendPump",
        "b3ReadyWriterEnabled": False,
        "productionSourceFilesChecked": len(expected_entries),
        "onlyAllowedProductionDifference": "none" if args.mode == "baseline" else CONTROLLER,
        "modeVerification": "pinned-SHA256-baseline-controller-or-audited-D-overlay-bytes",
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
