#!/usr/bin/env python3
"""Matched, balanced-wire transport control. Does not grant production acceptance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
MODES = ("A", "B1", "B2", "B2-adaptive")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--pgo", type=int, choices=(0, 1), required=True)
    parser.add_argument("--launch", type=int, choices=(1, 2), required=True)
    parser.add_argument("--group", choices=("high", "small", "large", "pressure"), default="high")
    parser.add_argument("--transport", choices=("tcp", "sharedmemory"), required=True)
    args = parser.parse_args()
    # Do not identify modified sources with the parent commit's identity.
    if subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=normal"], cwd=ROOT).strip():
        raise SystemExit("Commit the reviewed source first; a dirty worktree is not exact-head evidence.")
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], cwd=ROOT, text=True).strip()
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise SystemExit("dotnet is required")
    host = ROOT / "test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll"
    if not host.is_file():
        raise SystemExit("Build SharpLink.Benchmarks in Release first")
    env = os.environ.copy()
    env.update(DOTNET_TieredPGO=str(args.pgo), DOTNET_ReadyToRun="0", DOTNET_TieredCompilation="1",
               DOTNET_PROCESSOR_COUNT="4", SHARPLINK_SOURCE_TREE=tree)
    cases = {"high": [(c, 1024, 16, c*4096) for c in (32, 128)],
             "small": [(1, 1024, 16, 8192), (8, 1024, 16, 32768)],
             "large": [(c, 64, 4096, c*4096) for c in (32, 128)],
             "pressure": [(128, 256, 16, 8192)]}[args.group]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    cpus = sorted(os.sched_getaffinity(0))[:4] if hasattr(os, "sched_getaffinity") else []
    prefix = ["taskset", "-c", ",".join(map(str, cpus))] if cpus else []
    for streams, items, size, window in cases:
        name = f"{args.transport}-c{streams}-b{size}-pgo{args.pgo}-launch{args.launch}-{args.group}"
        target = output / (name + ".json")
        if target.exists():
            raise SystemExit(f"Refusing to overwrite prior evidence: {target}")
        cmd = prefix + [dotnet, str(host), "--phase-b-transport-evidence", args.transport,
                        str(streams), str(items), str(size), "8", str(window), str(target)]
        started = time.monotonic()
        with (output / (name + ".log")).open("w") as log:
            try:
                result = subprocess.run(cmd, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=90)
                code = result.returncode
            except subprocess.TimeoutExpired:
                code = 124
        (output / (name + ".exit.json")).write_text(json.dumps({
            "command": cmd, "exit_code": code, "seconds": time.monotonic()-started,
            "source_tree": tree, "affinity": cpus, "host_sha256": hashlib.sha256(host.read_bytes()).hexdigest()}))
        if code:
            raise SystemExit(f"{name} failed with {code}; log/report retained")


if __name__ == "__main__":
    main()
