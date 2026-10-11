#!/usr/bin/env python3
"""Recalculate historical #803 RPC and #742 matched-transport evidence from raw JSON.

No result is treated as a current-dev measurement or production acceptance.
Only Python's standard library is used. Pass the extracted historical evidence
root explicitly; reports are written to a separate output directory.
"""
from pathlib import Path
import json
import statistics
import re
import argparse
import hashlib

ROOT: Path
OUTPUT: Path
med = statistics.median


def ratio(candidate, baseline):
    return 100 * (candidate / baseline - 1)


def production():
    root = ROOT / "performance-39f27f08" / "measure"
    status = json.loads((root / "status.json").read_text())
    assert status["expectedRecords"] == 416 and len(status["records"]) == 416
    assert status["status"] == "completed" and not status["failures"]
    assert status["devSha"] == "0fe26024b114bb6e78411a9b86276086c045d03d"
    assert status["prSha"] == "39f27f08d9704e19b5ec7523f4a9f7494fe828cc"
    groups = {}
    filenames = set()
    for record in status["records"]:
        assert record["error"] is None and record["file"] not in filenames
        filenames.add(record["file"])
        data = json.loads((root / record["file"]).read_text())
        assert data["commit"] == record["sourceSha"]
        assert data["writerMode"] == record["mode"]
        assert data["validationFailures"] == 0 and data["tieredPgo"] == "1"
        key = record["scenario"], record["transport"], record["concurrency"]
        groups.setdefault(key, {}).setdefault(record["round"], {})[record["arm"]] = data
    cells = []
    for key, rounds in groups.items():
        samples = []
        assert len(rounds) == 4
        for index, arms in sorted(rounds.items()):
            assert set(arms) == {"dev-a", "dev-b", "pr-legacy", "pr-b3"}
            a, aa, old, b = [arms[name] for name in ("dev-a", "dev-b", "pr-legacy", "pr-b3")]
            devq = med([a["throughputOperationsPerSecond"], aa["throughputOperationsPerSecond"]])
            devcpu = med([a["cpuUsPerOperation"], aa["cpuUsPerOperation"]])
            devbytes = med([a["allocatedBytesPerOperation"], aa["allocatedBytesPerOperation"]])
            sample = {"round": index,
                "throughputPctVsDev": ratio(b["throughputOperationsPerSecond"], devq),
                "throughputPctVsCandidateLegacy": ratio(b["throughputOperationsPerSecond"], old["throughputOperationsPerSecond"]),
                "devAAPct": ratio(aa["throughputOperationsPerSecond"], a["throughputOperationsPerSecond"]),
                "cpuPctVsDev": ratio(b["cpuUsPerOperation"], devcpu),
                "allocatedBytesPerRpcDelta": b["allocatedBytesPerOperation"] - devbytes,
                "arms": {name: {metric: data[metric] for metric in (
                    "throughputOperationsPerSecond", "cpuUsPerOperation", "allocatedBytesPerOperation",
                    "p50Us", "p99Us", "p999Us", "operations", "gen0Collections", "gen1Collections", "gen2Collections")}
                    for name, data in arms.items()}}
            samples.append(sample)
        metrics = ("throughputPctVsDev", "throughputPctVsCandidateLegacy", "devAAPct", "cpuPctVsDev", "allocatedBytesPerRpcDelta")
        cells.append({"scenario": key[0], "transport": key[1], "concurrency": key[2],
            "medianPaired": {metric: med(sample[metric] for sample in samples) for metric in metrics},
            "rounds": samples})
    (OUTPUT / "production-recomputed.json").write_text(json.dumps({"subjectSha": status["prSha"],
        "oldDevSha": status["devSha"], "recordsValidated": len(filenames),
        "method": "Per round: reference is arithmetic mean of dev-a/dev-b (median of two); report median of four paired deltas.",
        "cells": cells}, indent=2) + "\n")
    report = ["# Independently recomputed #803 historical generated-RPC evidence", "",
        "416/416 original records validated. PGO ON only. Baseline is old dev `0fe26024`, not current `f8ba8912`.",
        "Paired deltas use each round's median of the two dev controls; the table is the median of four paired deltas.", "",
        "| Scenario | Transport | c | RPC/s vs old dev | RPC/s vs candidate legacy | dev A/A | CPU/RPC | Added B/RPC | Paired RPC/s deltas |",
        "|---|---|---:|---:|---:|---:|---:|---:|---|"]
    for cell in cells:
        m = cell["medianPaired"]
        report.append(f"| {cell['scenario']} | {cell['transport']} | {cell['concurrency']} | " +
            f"{m['throughputPctVsDev']:+.2f}% | {m['throughputPctVsCandidateLegacy']:+.2f}% | {m['devAAPct']:+.2f}% | " +
            f"{m['cpuPctVsDev']:+.2f}% | {m['allocatedBytesPerRpcDelta']:+.2f} | " +
            ", ".join(f"{sample['throughputPctVsDev']:+.2f}%" for sample in cell['rounds']) + " |")
    report += ["", f"Negative median throughput cells: {sum(c['medianPaired']['throughputPctVsDev'] < 0 for c in cells)}/{len(cells)}.",
        "", "Concurrency >1 records batch latency, not individual RPC latency. Duplex item/s divides by one direction's 100 items; it is not total bidirectional wire DATA/s.",
        "No per-operation profiles, P95, operation-level counter deltas, or full generated-RPC NativeAOT results are present in this artifact."]
    (OUTPUT / "production-recomputed.md").write_text("\n".join(report) + "\n")
    return cells


def prototype():
    groups = {}
    totals = {}
    for runtime in ("b3_jit", "b3_native"):
        total = 0
        processes = 0
        for file in (ROOT / runtime).glob("*.json"):
            doc = json.loads(file.read_text())
            if "samples" not in doc:
                continue
            assert doc["status"] == "completed" and doc["error"] is None
            metadata = doc["metadata"]
            assert metadata["Source"] == "1c90f0e0eea224204feb0680c0bb6742b1380f1b"
            assert metadata["DynamicCodeSupported"] == (runtime == "b3_jit")
            assert json.loads((ROOT / runtime / file.with_suffix(".exit").name).read_text())["code"] == 0
            processes += 1
            total += len(doc["samples"])
            for sample in doc["samples"]:
                metrics = sample["ReadyWriterMetrics"]
                assert metrics["RemainingQueuedBytes"] == 0
                assert metrics["NormalQueueRejections"] == 0
                assert sample["ItemsReceived"] == metadata["streams"] * metadata["items"]
                assert sample["BytesReturned"] == sample["ItemsReceived"] * metadata["bytes"]
                if metadata["preparedByteBudget"] != 8192:
                    continue
                key = runtime, metadata["transport"], metadata["Pgo"], metadata["bytes"]
                groups.setdefault(key, {}).setdefault(sample["Mode"], []).append(sample)
        assert (processes, total) == ((48, 768) if runtime == "b3_jit" else (8, 128))
        totals[runtime] = total
    cells = []
    for key, modes in sorted(groups.items()):
        result = {"runtime": key[0], "transport": key[1], "pgo": key[2], "itemBytes": key[3], "preparedPerStreamCap": 8192, "modes": {}}
        for mode, samples in sorted(modes.items()):
            result["modes"][mode] = {"samples": len(samples),
                "itemPerSecond": med(s["ItemsPerSecond"] for s in samples),
                "cpuNanosecondsPerItem": med(s["CpuMs"] * 1e6 / s["ItemsReceived"] for s in samples),
                "allocatedBytesPerItem": med(s["AllocatedBytesPerItem"] for s in samples),
                "countsPerItem": {name: med(s["ReadyWriterMetrics"][name] / s["ItemsReceived"] for s in samples)
                    for name in ("WriterTurns", "EventChannelReadCalls", "ReadyNotifications", "WireUpdateNotifications", "RingCapacityWaits", "ExistingPumpBudgetRmwLowerBound")}}
        a1, a16, b1, b16 = [result["modes"][m]["itemPerSecond"] for m in ("A-ready/q1", "A-ready/q16", "B3-ready/q1", "B3-ready/q16")]
        result["q16VersusQ1Pct"] = {"A-ready": ratio(a16, a1), "B3-ready": ratio(b16, b1)}
        result["B3VersusAPct"] = {"q1": ratio(b1, a1), "q16": ratio(b16, a16)}
        result["quantumCreditInteractionFactor"] = (b16 / a16) / (b1 / a1)
        cells.append(result)
    (OUTPUT / "prototype-recomputed.json").write_text(json.dumps({"sampleTotals": totals, "cells": cells}, indent=2) + "\n")
    lines = ["# Recomputed #742 quantum sensitivity (matched transport only)", "",
        "All original complete records pass identity/credit/queue checks. Native was compiled and executed; neither runtime population is generated RPC.", "",
        "| Runtime | Transport | PGO | Item B | A q16/q1 | B3 q16/q1 | B3/A q1 | B3/A q16 | Interaction |",
        "|---|---|---|---:|---:|---:|---:|---:|---:|"]
    for c in cells:
        lines.append(f"| {c['runtime']} | {c['transport']} | {c['pgo']} | {c['itemBytes']} | " +
            f"{c['q16VersusQ1Pct']['A-ready']:+.2f}% | {c['q16VersusQ1Pct']['B3-ready']:+.2f}% | " +
            f"{c['B3VersusAPct']['q1']:+.2f}% | {c['B3VersusAPct']['q16']:+.2f}% | {c['quantumCreditInteractionFactor']:.4f} |")
    lines += ["", "These are ratios of independently matched mode medians. q16/q1 changes scheduling and induced WINDOW_UPDATE/capacity-wait behavior together; it is not nanosecond attribution to linked-list operations.",
        "The 16 B controls use c128, 2048 items/stream, 8 KiB stream and connection windows, 16 slots/stream, 8 KiB prepared cap/stream, 16 KiB flush threshold and four processor affinity.",
        "The 4096 B controls use 128 items/stream and a 512 KiB connection window. Lifecycle setup/teardown is outside measurement; preparation/serialization is inside."]
    (OUTPUT / "prototype-recomputed.md").write_text("\n".join(lines) + "\n")
    return totals


def stress():
    root = ROOT / "stress-8ac4c849"
    codes = [tuple(map(int, line.split(","))) for line in (root / "exit-codes.csv").read_text().splitlines()]
    assert codes == [(1, 2), (2, 0), (3, 2), (4, 0), (5, 2), (6, 0), (7, 0), (8, 0)]
    rows = []
    for index, code in codes:
        text = re.sub(r"\x1b\[[0-9;]*m", "", (root / f"round-{index}.log").read_text())
        assert "total: 2061" in text and "skipped: 0" in text
        failures = [line.strip() for line in text.splitlines() if "[Test Failure] Exception:" in line]
        rows.append({"round": index, "exitCode": code, "failures": failures})
    (OUTPUT / "stress-recomputed.json").write_text(json.dumps(rows, indent=2) + "\n")


if __name__ == "__main__":
    if not __debug__:
        raise RuntimeError("Run without -O; assertions validate the original evidence")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-root", type=Path, required=True,
        help="Extracted archive's evidence/historical directory")
    parser.add_argument("--output-dir", type=Path, required=True,
        help="Separate writable directory for recomputed reports")
    args = parser.parse_args()
    ROOT, OUTPUT = args.evidence_root.resolve(), args.output_dir.resolve()
    if ROOT == OUTPUT:
        raise ValueError("Keep raw evidence and new reports in separate directories")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    cells = production()
    totals = prototype()
    stress()
    print(json.dumps({"productionCells": len(cells), "productionRawRecords": 416,
        "prototypeSamples": totals, "stressFailedRounds": [1, 3, 5]}))

    (OUTPUT / "historical-analysis-provenance.json").write_text(json.dumps({
        "scriptSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "evidenceRoot": str(ROOT), "outputDirectory": str(OUTPUT),
        "inputFamilies": ["performance-39f27f08", "stress-8ac4c849", "b3_jit", "b3_native"],
        "scope": "recomputation of frozen historical artifacts; not a new RPC measurement"
    }, indent=2) + "\n")
