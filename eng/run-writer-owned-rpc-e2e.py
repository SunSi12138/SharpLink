#!/usr/bin/env python3
"""Matched real-generated-RPC B3-vs-legacy-vs-pinned-dev evidence on one CI runner.

Each record is a fresh JIT process. Identical candidate benchmark sources are copied
into the pinned dev worktree; only the runtime/producer implementation differs.
Never interpret a successful collection as a performance acceptance result.
"""
import argparse
import datetime
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import sys
import time

SCENARIOS = (
    "Server1x16", "Server100x16", "Server100x4096",
    "Client100x16", "Client100x4096",
    "Duplex100x16", "Duplex100x4096",
)
SMALL_LONG = ("Server100x16", "Client100x16", "Duplex100x16")
ARMS = ("dev-a", "pr-legacy", "pr-b3", "dev-b")
KEYS = ("throughputOperationsPerSecond", "cpuUsPerOperation",
        "allocatedBytesPerOperation", "p99Us")


def cases(smoke):
    if smoke:
        return (("Server1x16", "tcp", 1), ("Duplex100x16", "tcp", 8))
    return tuple(
        (scenario, transport, concurrency)
        for transport in ("tcp", "sharedmemory")
        for scenario in SCENARIOS
        for concurrency in ((1, 8, 32) if scenario in SMALL_LONG else (1,))
    )


def median(values):
    return statistics.median(values) if values else float("nan")


def percent(change, reference):
    return 100.0 * (change / reference - 1.0) if reference > 0 else float("nan")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dev-dll", required=True)
    parser.add_argument("--pr-dll", required=True)
    parser.add_argument("--dev-sha", required=True)
    parser.add_argument("--pr-sha", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--seconds", type=float, default=1.5)
    parser.add_argument("--rounds", type=int, default=4)
    parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    if args.rounds < 1 or args.seconds <= 0 or args.rounds > 12:
        parser.error("rounds and seconds outside supported bounds")
    root = Path(args.output).resolve()
    raw = root / "raw"
    logs = root / "logs"
    raw.mkdir(parents=True, exist_ok=True)
    logs.mkdir(parents=True, exist_ok=True)
    dev_dll = Path(args.dev_dll).resolve()
    pr_dll = Path(args.pr_dll).resolve()
    if not dev_dll.is_file() or not pr_dll.is_file():
        raise FileNotFoundError("Both pinned JIT benchmark binaries are required.")

    population = cases(args.smoke)
    expected_count = len(population) * args.rounds * len(ARMS)
    records = []
    failures = []
    status = {"status": "running", "startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "devSha": args.dev_sha, "prSha": args.pr_sha,
              "sourceModes": {"dev-a": "dev-legacy", "dev-b": "dev-legacy",
                              "pr-legacy": "candidate-legacy", "pr-b3": "candidate-writer-owned"},
              "measurementSeconds": args.seconds, "rounds": args.rounds,
              "smoke": args.smoke, "expectedRecords": expected_count,
              "cases": [{"scenario": s, "transport": t, "concurrency": c} for s, t, c in population],
              "records": records, "failures": failures}
    status_path = root / "status.json"

    def persist():
        temp = status_path.with_suffix(".tmp")
        temp.write_text(json.dumps(status, indent=2, ensure_ascii=False) + "\n")
        temp.replace(status_path)

    persist()
    try:
        for scenario, transport, concurrency in population:
            for round_index in range(args.rounds):
                # Rotation balances each of the four modes across process-order slots
                # when rounds=4; it also supplies independent dev/dev A/A controls.
                order = ARMS[round_index % len(ARMS):] + ARMS[:round_index % len(ARMS)]
                for position, arm in enumerate(order):
                    baseline = arm.startswith("dev-")
                    dll = dev_dll if baseline else pr_dll
                    sha = args.dev_sha if baseline else args.pr_sha
                    mode = "writer-owned" if arm == "pr-b3" else "legacy"
                    prefix = f"{scenario}-{transport}-c{concurrency}-r{round_index:02d}-{arm}"
                    result_path = raw / f"{prefix}.json"
                    stdout_path = logs / f"{prefix}.stdout"
                    stderr_path = logs / f"{prefix}.stderr"
                    env = dict(os.environ)
                    env["SHARPLINK_BENCHMARK_SHA"] = sha
                    env["SHARPLINK_BENCHMARK_WRITER_OWNED"] = "1" if arm == "pr-b3" else "0"
                    env["DOTNET_PROCESSOR_COUNT"] = "4"
                    env["DOTNET_ReadyToRun"] = "0"
                    env["DOTNET_TieredCompilation"] = "1"
                    env["DOTNET_TieredPGO"] = "1"
                    env["DOTNET_TC_QuickJitForLoops"] = "1"
                    warmup = 64 if concurrency == 1 else (16 if concurrency == 8 else 8)
                    command = [
                        "dotnet", str(dll), "--generated-abi-streaming-evidence",
                        scenario, str(warmup), str(args.seconds), "80000",
                        str(result_path), transport, str(concurrency),
                    ]
                    started = time.monotonic()
                    error = None
                    with stdout_path.open("w") as stdout, stderr_path.open("w") as stderr:
                        try:
                            completed = subprocess.run(
                                command, env=env, stdout=stdout, stderr=stderr,
                                timeout=90, check=False)
                            if completed.returncode != 0:
                                error = f"process exited {completed.returncode}"
                        except subprocess.TimeoutExpired:
                            error = "90-second process timeout"
                        except Exception as exc:
                            error = str(exc)
                    doc = None
                    if error is None:
                        try:
                            doc = json.loads(result_path.read_text())
                            if (doc.get("commit") != sha or doc.get("writerMode") != mode or
                                doc.get("scenario") != scenario or doc.get("transport") != transport or
                                doc.get("concurrency") != concurrency or
                                doc.get("validationFailures") != 0 or
                                doc.get("operations", 0) < concurrency or
                                not all(math.isfinite(float(doc[k])) for k in KEYS) or
                                doc.get("tieredPgo") != "1"):
                                raise ValueError("incorrect source/mode/population/metric provenance")
                        except Exception as exc:
                            error = "invalid result: " + str(exc)
                    record = {"scenario": scenario, "transport": transport,
                              "concurrency": concurrency, "round": round_index,
                              "position": position, "arm": arm, "mode": mode,
                              "sourceSha": sha, "elapsedWallSeconds": round(time.monotonic() - started, 3),
                              "file": str(result_path.relative_to(root)), "error": error}
                    records.append(record)
                    if error:
                        failures.append(record)
                    persist()
                    print(f"{prefix}: " +
                          (f"{doc['throughputOperationsPerSecond']:.0f} RPC/s; "
                           f"{doc['allocatedBytesPerOperation']:.1f} B/RPC"
                           if error is None else "FAILED "+error), flush=True)
                # An unsuccessful cell stays in artifacts, and does not silently
                # disappear from the matched population.
            write_summary(root, population, args.rounds, records, failures)
        status["status"] = ("completed" if not failures and len(records) == expected_count
                            else "failed")
    except Exception as exc:
        failures.append({"fatal": repr(exc)})
        status["status"] = "failed"
        raise
    finally:
        status["finishedUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        persist()
        write_summary(root, population, args.rounds, records, failures)
    if status["status"] != "completed":
        raise SystemExit(f"Incomplete evidence: {len(records)}/{expected_count} records, {len(failures)} failures.")


def write_summary(root, population, rounds, records, failures):
    report = [
        "# Writer-owned real generated RPC — matched JIT evidence",
        "",
        "This is **diagnostic evidence**, not a NativeAOT result or production Go.",
        "Same generated RPC implementation/benchmark code, separate frozen dev and PR",
        "binaries. Candidate B3 is explicitly enabled on both endpoints; candidate",
        "legacy and both dev controls run with it disabled.",
        "Each cell is a distinct warmed process; DOTNET_PROCESSOR_COUNT=4,",
        "TieredPGO=1 and ReadyToRun=0. A/A dev-a vs dev-b reveals hosted-runner noise.",
        "A concurrency>1 latency is the latency of a *batch* of concurrent RPCs,",
        "not the latency of an individual RPC. CPU and allocation are per completed RPC.",
        "",
        "| Scenario | Transport | c | B3 vs dev RPC/s | B3 vs PR legacy RPC/s | dev A/A RPC/s | B3 vs dev CPU/RPC | B3 − dev B/RPC |",
        "|---|---|---:|---:|---:|---:|---:|---:|",
    ]
    coverage = 0
    for scenario, transport, concurrency in population:
        rows = [row for row in records if row["scenario"] == scenario and
                row["transport"] == transport and row["concurrency"] == concurrency and
                row["error"] is None]
        by_round = {}
        for row in rows:
            try:
                doc = json.loads((root / row["file"]).read_text())
            except (OSError, ValueError):
                continue
            by_round.setdefault(row["round"], {})[row["arm"]] = doc
        valid = [v for v in by_round.values() if set(v) == set(ARMS)]
        coverage += len(valid) * len(ARMS)
        if not valid:
            report.append(f"| {scenario} | {transport} | {concurrency} | — | — | — | — | — |")
            continue
        b3_vs_dev, b3_vs_legacy, aa, cpu, bytes_delta = [], [], [], [], []
        for v in valid:
            devq = median([v["dev-a"]["throughputOperationsPerSecond"],
                           v["dev-b"]["throughputOperationsPerSecond"]])
            b3q = v["pr-b3"]["throughputOperationsPerSecond"]
            oldq = v["pr-legacy"]["throughputOperationsPerSecond"]
            b3_vs_dev.append(percent(b3q, devq))
            b3_vs_legacy.append(percent(b3q, oldq))
            aa.append(percent(v["dev-b"]["throughputOperationsPerSecond"],
                              v["dev-a"]["throughputOperationsPerSecond"]))
            devcpu = median([v["dev-a"]["cpuUsPerOperation"],
                             v["dev-b"]["cpuUsPerOperation"]])
            cpu.append(percent(v["pr-b3"]["cpuUsPerOperation"], devcpu))
            bytes_delta.append(v["pr-b3"]["allocatedBytesPerOperation"] -
                median([v["dev-a"]["allocatedBytesPerOperation"],
                        v["dev-b"]["allocatedBytesPerOperation"]]))
        def fmt(x, unit="%"):
            return f"{median(x):+.2f}{unit}"
        report.append(f"| {scenario} | {transport} | {concurrency} | "
                      f"{fmt(b3_vs_dev)} | {fmt(b3_vs_legacy)} | "
                      f"{fmt(aa)} | {fmt(cpu)} | {fmt(bytes_delta, ' B')} |")
    report += [
        "",
        f"Matched complete sets: **{coverage}/{len(population) * rounds * len(ARMS)}** records; "
        f"process/validation failures: **{len(failures)}**.",
        "",
        "A positive throughput delta is faster; a positive CPU delta consumes more CPU;",
        "a positive B/RPC delta allocates more. These are four-repetition median",
        "observations without narrow confidence bounds. Do not infer significance",
        "when changes are comparable to the A/A control.",
        "",
        "Exact per-process JSON, stdout, stderr, source SHA, process order and status",
        "are retained in this workflow artifact. Failed cases are never dropped.",
        "",
    ]
    (root / "summary.md").write_text("\n".join(report))


if __name__ == "__main__":
    main()
