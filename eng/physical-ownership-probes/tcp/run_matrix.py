#!/usr/bin/env python3
"""Run a fixed, alternating-pair, fresh-process TCP stream matrix on prepared binaries."""
import argparse
import datetime
import json
import os
from pathlib import Path
import subprocess
import time


def observation():
    stat = Path("/proc/stat")
    load = Path("/proc/loadavg")
    return {
        "timestampUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "cpu": {line.split()[0]: list(map(int, line.split()[1:]))
                for line in stat.read_text().splitlines() if line.startswith("cpu")}
               if stat.exists() else None,
        "loadavg": load.read_text().strip() if load.exists() else None,
    }


def execute_sample(command, log_path, host_path, variant, round_number, environment, timeout):
    """Retain process outcome even when it times out or cannot start."""
    host = {"command": command, "variant": variant, "round": round_number,
            "before": observation(), "exitCode": None, "timeoutSeconds": timeout}
    started = time.monotonic()
    try:
        with log_path.open("w", encoding="utf-8") as log:
            result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT,
                                    env=environment, timeout=timeout)
        host.update(exitCode=result.returncode,
                    status="success" if result.returncode == 0 else "failed")
        if result.returncode:
            host["reason"] = f"Process exited with code {result.returncode}"
        return result.returncode
    except subprocess.TimeoutExpired as error:
        host.update(status="timed-out", reason=str(error))
        raise
    except OSError as error:
        host.update(status="start-failed", reason=str(error))
        raise
    finally:
        host.update(after=observation(), wallSeconds=time.monotonic() - started)
        host_path.write_text(json.dumps(host, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepared", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="New, non-existing directory")
    parser.add_argument("--baseline-revision", required=True)
    parser.add_argument("--candidate-revision", required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--runtime-mode", choices=("default", "fullopts"), default="default")
    parser.add_argument("--export-ticks", action="store_true", help="Requires the ablation probe; export ordered durations after measurement")
    parser.add_argument("--keep-going", action="store_true", help="Attempt the declared schedule once despite failures; never retry or replace")
    parser.add_argument("--rounds", type=int, default=6)
    parser.add_argument("--warmup", type=float, default=3)
    parser.add_argument("--seconds", type=float, default=6)
    parser.add_argument("--affinity", default="2,3", help="Linux taskset CPU list; use 'none' to disable")
    parser.add_argument("--workers", type=int, default=1)
    parser.add_argument("--items", default="1,100")
    parser.add_argument("--modes", default="static-intercepted,dynamic-intercepted")
    args = parser.parse_args()
    if min(args.rounds, args.workers, args.warmup, args.seconds) <= 0:
        parser.error("Rounds, workers, warmup and seconds must be positive")
    items = [int(n) for n in args.items.split(",")]
    modes = args.modes.split(",")
    if not items or min(items) <= 0 or not modes or any(
            mode not in ("static-intercepted", "dynamic-intercepted") for mode in modes):
        parser.error("Use positive item counts and supported static/dynamic intercepted modes")
    prepared = args.prepared.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    runs = output / "runs"
    runs.mkdir()
    env = dict(os.environ)
    for key in list(env):
        if key.startswith(("DOTNET_Tiered", "COMPlus_Tiered", "DOTNET_Jit", "COMPlus_Jit",
                           "DOTNET_TC_", "COMPlus_TC_", "DOTNET_ReadyToRun", "COMPlus_ReadyToRun",
                           "DOTNET_ZapDisable", "COMPlus_ZapDisable")):
            del env[key]
    if args.runtime_mode == "fullopts":
        env["DOTNET_TieredCompilation"] = "0"
    if args.export_ticks:
        (output / "ticks").mkdir()
    manifest = {
        "arguments": {key: str(value) if isinstance(value, Path) else value
                      for key, value in vars(args).items()},
        "environment": {key: value for key, value in env.items()
                        if key.startswith(("DOTNET_", "COMPlus_"))},
        "runtimeconfig": json.loads((prepared / "baseline" / "PhysicalTcpProbe.runtimeconfig.json").read_text()),
        "preparedManifest": json.loads((prepared / "prepared-manifest.json").read_text())
                            if (prepared / "prepared-manifest.json").exists() else None,
        "cpuinfo": Path("/proc/cpuinfo").read_text() if Path("/proc/cpuinfo").exists() else None,
    }
    (output / "matrix-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    failures = []
    scenarios = [(mode, count) for count in items for mode in modes]
    for round_number in range(1, args.rounds + 1):
        rotation = (round_number - 1) % len(scenarios)
        schedule = scenarios[rotation:] + scenarios[:rotation]
        for mode, count in schedule:
            order = ("baseline", "candidate") if round_number % 2 else ("candidate", "baseline")
            for variant in order:
                key = f"{mode}-n{count}-c{args.workers}-r{round_number}-{variant}"
                destination = runs / (key + ".json")
                command = [] if args.affinity == "none" else ["taskset", "-c", args.affinity]
                command += [args.dotnet, str(prepared / variant / "PhysicalTcpProbe.dll"),
                            mode, str(count), str(args.workers), str(args.warmup), str(args.seconds),
                            str(destination)]
                if args.export_ticks:
                    command += ["measure", str(output / "ticks" / (key + ".json"))]
                run_env = dict(env, SHARPLINK_BENCHMARK_SHA=(args.baseline_revision
                               if variant == "baseline" else args.candidate_revision))
                try:
                    exit_code = execute_sample(
                        command, runs / (key + ".log"), runs / (key + ".host.json"),
                        variant, round_number, run_env, args.warmup + args.seconds + 90)
                    if exit_code:
                        raise RuntimeError(f"Failed {key}: exit {exit_code}; inspect its log")
                    data = json.loads(destination.read_text())
                    print(f"{key} ops/s={data['operationsPerSecond']:.0f} "
                          f"p50={data['p50Us']:.2f}us p99={data['p99Us']:.2f}us "
                          f"alloc={data['allocatedBytesPerOperation']:.1f}B/op", flush=True)
                except (subprocess.TimeoutExpired, OSError, ValueError, KeyError, RuntimeError) as error:
                    failures.append({"sample": key, "error": repr(error)})
                    (output / "matrix-failures.json").write_text(json.dumps(failures, indent=2) + "\n")
                    if not args.keep_going:
                        raise
    if failures:
        raise RuntimeError(f"Declared matrix finished with {len(failures)} failures; no samples were replaced")
    print("MATRIX_COMPLETE", flush=True)


if __name__ == "__main__":
    main()
