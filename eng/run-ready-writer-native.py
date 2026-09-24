#!/usr/bin/env python3
"""Run a declared native transport matrix; preserve failed reports and exit codes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]


def plan():
    return [[transport, "nativeaot", 128, items, size, window, 16384, launch, 8192]
            for transport in ("sharedmemory", "tcp")
            for size, items, window in ((16, 2048, 8192), (4096, 128, 524288))
            for launch in (0, 1)]


def name(index, case):
    return f"{index:02}-{case[0]}-b{case[4]}-w{case[5]}-r{case[7]}.json"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--source", required=True)
    args = parser.parse_args()
    actual = subprocess.check_output(["git", "write-tree"], cwd=ROOT, text=True).strip()
    subprocess.run(["git", "diff", "--exit-code"], cwd=ROOT, check=True)
    if actual != args.source:
        raise ValueError("Index does not match measured tree")
    binary = args.binary.resolve(strict=True)
    affinity = sorted(os.sched_getaffinity(0))[:4]
    if len(affinity) != 4:
        raise ValueError("Four available CPUs are required")
    args.output.mkdir(parents=True, exist_ok=True)
    provenance = dict(source_tree=actual, host_sha256=hashlib.sha256(binary.read_bytes()).hexdigest(),
                      cpu_affinity=affinity, runtime="nativeaot", plan=plan(),
                      rounds=4, slots=16, quanta=[1, 16], allocation_diagnostic=False)
    path = args.output / "provenance.json"
    if path.exists():
        raise FileExistsError("Do not combine independent evidence runs")
    path.write_text(json.dumps(provenance, indent=2))
    for index, case in enumerate(plan()):
        transport, _, streams, items, size, window, flush, launch, budget = case
        target = args.output / name(index, case)
        if target.exists():
            raise FileExistsError(target)
        env = dict(os.environ, SHARPLINK_SOURCE_TREE=actual, DOTNET_PROCESSOR_COUNT="4",
                   SHARPLINK_READY_ORDER=str(launch), SHARPLINK_READY_PREPARED_BYTES=str(budget),
                   SHARPLINK_READY_ALLOCATION_DIAGNOSTIC="0")
        env.pop("DOTNET_TieredPGO", None)
        command = ["taskset", "-c", ",".join(map(str, affinity)), str(binary),
                   "--ready-writer-evidence", transport, str(streams), str(items), str(size),
                   "4", str(window), "16", str(flush), str(target.resolve())]
        started = time.monotonic()
        print("RUN", index, target.name, flush=True)
        with target.with_suffix(".log").open("w") as log:
            try:
                code = subprocess.run(command, cwd=ROOT, env=env, stdout=log,
                                      stderr=subprocess.STDOUT, timeout=180).returncode
            except subprocess.TimeoutExpired:
                code = 124
        target.with_suffix(".exit").write_text(json.dumps(dict(code=code, seconds=time.monotonic() - started)))
        if code:
            raise SystemExit(f"Native process failed ({code}); no retry or dropped sample: {target}")
        print("PASS", index, flush=True)


if __name__ == "__main__":
    main()
