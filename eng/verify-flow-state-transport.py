#!/usr/bin/env python3
"""Validate actual data/credit balance and show ALL controls, including regressions."""
import argparse
import json
import math
from pathlib import Path
import re
import statistics
from collections import defaultdict

MODES = {"A", "B1", "B2", "B2-adaptive"}


def validate(path):
    report = json.loads(path.read_text())
    if report.get("status") != "completed" or report.get("error") is not None:
        raise ValueError(f"Incomplete/failed report: {path}")
    metadata = report["metadata"]
    source = metadata["Source"]
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise ValueError("Missing exact measured tree")
    streams, items, size, rounds = (metadata[k] for k in ("streams", "items", "bytes", "rounds"))
    if not all(type(v) is int and v > 0 for v in (streams, items, size, rounds)):
        raise ValueError("Invalid workload sizes")
    if metadata["transport"] not in ("tcp", "sharedmemory", "pipe") or metadata["Pgo"] not in ("0", "1"):
        raise ValueError("Unspecified transport or JIT mode")
    rows = report["samples"]
    expected = {(mode, r) for mode in MODES for r in range(rounds)}
    if len(rows) != len(expected) or {(x["Mode"], x["Round"]) for x in rows} != expected:
        raise ValueError("Missing or duplicate mode/round; negative controls are mandatory")
    for row in rows:
        if (row["Source"] != source or row["Transport"] != metadata["transport"] or
            (row["Streams"], row["ItemsPerStream"], row["ItemBytes"]) != (streams, items, size) or
            row["ConnectionWindow"] != metadata["connectionWindow"] or row["StreamWindow"] != 8192):
            raise ValueError("Source/configuration mismatch")
        total = streams*items
        for field in ("ItemsReceived", "BytesReturned", "UpdateFrames", "OwnerCommands", "RefillCommands",
                      "PressureRevocations", "QueuedWaiterAdmissions"):
            if type(row[field]) is not int or row[field] < 0:
                raise ValueError("Invalid operation count")
        if "RevocationSweeps" in row and (type(row["RevocationSweeps"]) is not int or row["RevocationSweeps"] < 0):
            raise ValueError("Invalid sweep count")
        if row["ItemsReceived"] != total or row["BytesReturned"] != total*size or row["UpdateFrames"] < 1:
            raise ValueError("Lost data or mismatched actual peer credit")
        for field in ("ElapsedMs", "CpuMs", "ItemsPerSecond", "AllocatedBytesPerItem", "OwnerCommandsPerItem", "ProducerSpreadMs"):
            if not isinstance(row[field], (int, float)) or not math.isfinite(row[field]) or row[field] < 0:
                raise ValueError("Invalid measurement")
        if row["ElapsedMs"] <= 0 or row["CpuMs"] <= 0 or not math.isclose(row["ItemsPerSecond"], total*1000/row["ElapsedMs"], rel_tol=1e-9):
            raise ValueError("Invalid throughput denominator")
        if not math.isclose(row["OwnerCommandsPerItem"], row["OwnerCommands"]/total, rel_tol=1e-12):
            raise ValueError("Invalid owner command denominator")
        if row["Mode"] == "A":
            if row["OwnerCommands"] != 0:
                raise ValueError("A does not have an owner queue; zero does NOT mean zero locks")
        elif row["OwnerCommands"] < row["UpdateFrames"] + row["RefillCommands"]:
            raise ValueError("Dropped update/refill coordination from attribution")
        if row["Mode"] == "B1" and row["RefillCommands"] != total:
            raise ValueError("B1 must submit every data item")
        times = row["ProducerDurationMs"]
        if len(times) != streams or any(not math.isfinite(x) or x <= 0 for x in times):
            raise ValueError("Invalid per-producer completion timings")
        if not math.isclose(max(times)-min(times), row["ProducerSpreadMs"], abs_tol=1e-7):
            raise ValueError("Invalid completion spread")
    return metadata, rows


def summarize(root):
    reports = [p for p in sorted(root.glob("*.json")) if not p.name.endswith(".exit.json")]
    if not reports:
        raise ValueError("No report files")
    all_rows = []
    identities = set()
    measured_sources = set()
    for path in reports:
        meta, rows = validate(path)
        measured_sources.add(meta["Source"])
        if len(measured_sources) != 1:
            raise ValueError("Do not merge measurements from different source trees")
        exit_path = path.with_name(path.stem + ".exit.json")
        if not exit_path.is_file() or json.loads(exit_path.read_text())["exit_code"] != 0:
            raise ValueError("Report lacks a successful process exit record")
        launch_match = re.search(r"launch([12])", path.name)
        if not launch_match:
            raise ValueError("Independent launch id missing")
        launch = int(launch_match[1])
        for row in rows:
            row = dict(row, Pgo=meta["Pgo"], Launch=launch)
            identity = (row["Transport"], row["Streams"], row["ItemBytes"], row["ItemsPerStream"], row["ConnectionWindow"], row["Pgo"], launch, row["Mode"], row["Round"])
            if identity in identities:
                raise ValueError("Repeated workload/launch identity")
            identities.add(identity)
            all_rows.append(row)
    groups = defaultdict(list)
    for row in all_rows:
        groups[(row["Pgo"], row["Transport"], row["Streams"], row["ItemBytes"], row["ItemsPerStream"], row["ConnectionWindow"])].append(row)
    lines = ["# Phase B — integrated transport control", "",
             f"{len(reports)} reports / {len(all_rows)} measured rows, complete modes and process exits validated.", "",
             "Primary throughput statistic: ratio of the per-mode median items/s (not median of percentages).",
             "Geometric means, per-launch values and raw round data remain available; these short local measurements are exploratory.", "",
             "All modes have ONE unsettled emission per stream, real transport/SendPump/parser/production receive accounting and balanced key-only credits. Setup and handshake exchange, generated RPC dispatch, full cancellation/multi-handle contract, unbalanced duplicate credits and cold allocation are NOT measured.", "",
             "A uses the frozen resolved Phase A controller. B1 enqueues every acquire; B2 has fixed 4 KiB grants; B2-adaptive caps new grants by half-window/registered-stream fair share. The 8 KiB stream window and connection window are explicit matching experiment inputs, not new defaults.", "",
             "A's zero owner commands means no owner QUEUE; its per-item connection lock remains. Internal Channel/Lock atomics are not measured. Producer completion spread is not waiter fairness/latency proof.", "",
             "| PGO | Transport | c | bytes/item | items/stream | conn window | mode | median item/s | vs A | CPU ns/item | B/item | owner/item | revocations |", "|---|---|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|"]
    summary = []
    sweep_rows = []
    for key, rows in sorted(groups.items()):
        base = statistics.median(x["ItemsPerSecond"] for x in rows if x["Mode"] == "A")
        for mode in ("A", "B1", "B2", "B2-adaptive"):
            samples = [x for x in rows if x["Mode"] == mode]
            rate = statistics.median(x["ItemsPerSecond"] for x in samples)
            cpu = statistics.median(x["CpuMs"]*1e6/x["ItemsReceived"] for x in samples)
            allocation = statistics.median(x["AllocatedBytesPerItem"] for x in samples)
            commands = statistics.median(x["OwnerCommandsPerItem"] for x in samples)
            revocations = statistics.median(x["PressureRevocations"] for x in samples)
            sweeps = statistics.median(x["RevocationSweeps"] for x in samples) if all("RevocationSweeps" in x for x in samples) else None
            if sweeps is not None:
                sweep_rows.append((key, mode, sweeps))
            change = (rate/base-1)*100
            summary.append(dict(zip(("pgo", "transport", "streams", "bytes", "items", "connection_window"), key),
                                mode=mode, rate=rate, change_percent=change, cpu_ns=cpu, allocated_bytes=allocation,
                                owner_commands=commands, revocations=revocations, revocation_sweeps=sweeps, samples=len(samples)))
            lines.append("| " + " | ".join(map(str, key)) + f" | {mode} | {rate:.0f} | {change:+.2f}% | {cpu:.1f} | {allocation:.3f} | {commands:.6f} | {revocations:g} |")
    lines += ["", "## Independent launch check (adaptive vs A; no favorable sample filtering)", "",
              "| PGO | Transport | c | item bytes | window | launch | median rate ratio | geometric mean rate ratio |", "|---|---|---:|---:|---:|---:|---:|---:|"]
    for key, rows in sorted(groups.items()):
        for launch in sorted({x["Launch"] for x in rows}):
            a = [x["ItemsPerSecond"] for x in rows if x["Mode"] == "A" and x["Launch"] == launch]
            b = [x["ItemsPerSecond"] for x in rows if x["Mode"] == "B2-adaptive" and x["Launch"] == launch]
            ratio = statistics.median(b)/statistics.median(a)-1
            geometric = statistics.geometric_mean(b)/statistics.geometric_mean(a)-1
            lines.append(f"| {key[0]} | {key[1]} | {key[2]} | {key[3]} | {key[5]} | {launch} | {ratio*100:+.2f}% | {geometric*100:+.2f}% |")
    if sweep_rows:
        lines += ["", "## Actual pressure reclamation sweeps", "",
                  "Sweeps count owner-wide traversals; revocations count reclaimed nonzero grants. They are not the same metric.", "",
                  "| PGO | Transport | c | item bytes | items/stream | window | mode | median total sweeps |", "|---|---|---:|---:|---:|---:|---|---:|"]
        for key, mode, sweeps in sweep_rows:
            lines.append("| " + " | ".join(map(str, key)) + f" | {mode} | {sweeps:g} |")
    return "\n".join(lines)+"\n", summary, all_rows


if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("root", type=Path)
    args = parser.parse_args(); text, rows, raw = summarize(args.root)
    (args.root/"transport-summary.md").write_text(text)
    # JSON summaries live in a separate subdirectory so they cannot look like input reports.
    (args.root/"derived").mkdir(exist_ok=True)
    (args.root/"derived/summary.json").write_text(json.dumps(rows, indent=2))
    print(text)
