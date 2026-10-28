#!/usr/bin/env python3
"""Apply fixed local or controlled campaign formulas without filtering/replacement."""
import csv
import json
import math
from pathlib import Path
import sys
from spec import CALLS, CELLS, LOCAL, SAMPLES, validate_schedule


def aggregate_samples(samples):
    if [r["sample"] for r in samples] != list(range(SAMPLES)):
        raise ValueError("Missing, duplicated or reordered sample")
    if not all(r["completed"] == CALLS and r["failure"] is None and len(r["ticks"]) == CALLS and r["frequency"] > 0 and r["elapsedTicks"] > 0 for r in samples):
        raise ValueError("Incomplete/failed sample cannot enter a full campaign comparison")
    n = sum(r["completed"] for r in samples)
    ticks = sorted(t * 1e9 / r["frequency"] for r in samples for t in r["ticks"])
    if n != 10000 or not all(t > 0 for t in ticks):
        raise ValueError("Expected all 10000 completed call durations")
    return {"meanNs": 1e9 * sum(r["elapsedTicks"] / r["frequency"] for r in samples) / n,
            "p99Ns": ticks[math.ceil(.99 * n) - 1],
            "bytesPerCall": sum(r["bytes"] for r in samples) / n,
            "cpuNsPerCall": 1e6 * sum(r["cpuMilliseconds"] for r in samples) / n,
            "gcCollections": [sum(r["gcCollections"][g] for r in samples) for g in range(3)],
            "completedCalls": n}


def pair_result(index, label, left, right):
    logs = {c: math.log(right["cells"][c]["meanNs"] / left["cells"][c]["meanNs"]) for c in CELLS}
    return {"pairIndex": index, "label": label, "meanLogRatios": logs,
            "interactions": {t: logs[f"I/{t}"] - logs[f"P/{t}"] for t in ["T", "U"]},
            "meanRatios": {c: math.exp(logs[c]) for c in CELLS},
            "p99Ratios": {c: right["cells"][c]["p99Ns"] / left["cells"][c]["p99Ns"] for c in CELLS}}


def compare(processes, pairs, baseline, candidate):
    cross, same = [], []
    for index, label in enumerate(pairs):
        first, second = processes[2*index:2*index+2]
        if set(label) == {baseline, candidate}:
            left, right = (first, second) if label[0] == baseline else (second, first)
            cross.append(pair_result(index, label, left, right))
        elif label in [baseline*2, candidate*2]:
            same.append(pair_result(index, label, first, second))
    if len(cross) != 4 or len(same) != 4:
        raise ValueError("Each contrast requires four cross pairs and two same-arm pairs per arm")
    noise = {t: max(abs(q["interactions"][t]) for q in same) for t in ["T", "U"]}
    decisions = {}
    for t in ["T", "U"]:
        favorable = all(p["meanLogRatios"][f"I/{t}"] < 0 and p["interactions"][t] < -noise[t] for p in cross)
        adverse = all(p["meanLogRatios"][f"I/{t}"] > 0 and p["interactions"][t] > noise[t] for p in cross)
        decisions[t] = "favorable exploratory signal" if favorable else "adverse exploratory signal" if adverse else "inconclusive"
    def gm(values):
        return math.exp(sum(math.log(v) for v in values) / len(values))
    return {"baselineArm": baseline, "candidateArm": candidate, "crossPairs": cross, "samePairs": same,
            "noiseEnvelopes": noise, "decisions": decisions,
            "cellSummary": {c: {"meanGeometricRatio": gm([p["meanRatios"][c] for p in cross]),
                                "p99GeometricRatio": gm([p["p99Ratios"][c] for p in cross])} for c in CELLS},
            "interactionGeometricRatios": {t: math.exp(sum(p["interactions"][t] for p in cross)/4) for t in ["T", "U"]}}


def comparison_markdown(name, result):
    lines = [f"## {name}: T **{result['decisions']['T']}**, U **{result['decisions']['U']}**", "",
             "| Cell | Four mean ratios | Mean GM | Four P99 ratios | P99 GM |", "|---|---|---:|---|---:|"]
    for c in CELLS:
        summary = result["cellSummary"][c]
        lines.append(f"| {c} | " + ", ".join(f"{p['meanRatios'][c]:.6f}" for p in result["crossPairs"]) + f" | {summary['meanGeometricRatio']:.6f} | " + ", ".join(f"{p['p99Ratios'][c]:.6f}" for p in result["crossPairs"]) + f" | {summary['p99GeometricRatio']:.6f} |")
    for title, key in [("Cross-arm L and D", "crossPairs"), ("Same-arm S and H (second/first)", "samePairs")]:
        lines += ["", f"### {title}", "", "| Pair (zero-based) | Order | I/T log | P/T log | I/U log | P/U log | T interaction | U interaction |", "|---:|---|---:|---:|---:|---:|---:|---:|"]
        for p in result[key]:
            lines.append(f"| {p['pairIndex']} | {p['label']} | " + " | ".join(f"{p['meanLogRatios'][c]:.9f}" for c in CELLS) + f" | {p['interactions']['T']:.9f} | {p['interactions']['U']:.9f} |")
    lines += ["", f"noise[T]={result['noiseEnvelopes']['T']:.9f}; noise[U]={result['noiseEnvelopes']['U']:.9f}.", f"Interaction geometric ratios: T={result['interactionGeometricRatios']['T']:.6f}, U={result['interactionGeometricRatios']['U']:.6f}.", "", "| Same pair | Order | Cell | Mean second/first | P99 second/first |", "|---:|---|---|---:|---:|"]
    for p in result["samePairs"]:
        for c in CELLS:
            lines.append(f"| {p['pairIndex']} | {p['label']} | {c} | {p['meanRatios'][c]:.6f} | {p['p99Ratios'][c]:.6f} |")
    return lines


def analyze(root):
    manifest = json.loads((root / "manifest.json").read_text())
    name, profile = validate_schedule(manifest)
    ledger = [json.loads(line) for line in (root / "execution.jsonl").read_text().splitlines()]
    if len(ledger) != len(manifest["processes"]) or not all(row["valid"] for row in ledger):
        raise ValueError("Incomplete campaign: retain raw evidence; no full comparison")
    processes = []
    for process, execution in zip(manifest["processes"], ledger):
        if process["name"] != execution["name"]:
            raise ValueError("Execution inventory differs from frozen plan")
        rows = [json.loads(line) for line in Path(process["samples"]).read_text().splitlines()]
        if [(r["cell"], r["sample"]) for r in rows] != [(c, s) for c in CELLS for s in range(SAMPLES)]:
            raise ValueError("Unexpected process/cell/sample inventory")
        processes.append({"name": process["name"], "arm": process["arm"],
                          "cells": {c: aggregate_samples([r for r in rows if r["cell"] == c]) for c in CELLS}})
    comparisons = {key: compare(processes, manifest["pairs"], *arms) for key, arms in profile["comparisons"].items()}
    count = sum(p["cells"][c]["completedCalls"] for p in processes for c in CELLS)
    expected = len(manifest["processes"]) * len(CELLS) * SAMPLES * CALLS
    if count != expected:
        raise ValueError(f"Expected {expected} completed calls; found {count}")
    if name == LOCAL:
        result = {"processes": processes, **{k: v for k, v in comparisons["B/A"].items() if k not in ["baselineArm", "candidateArm"]}, "completedCalls": count}
    else:
        result = {"profile": name, "processes": processes, "comparisons": comparisons, "completedCalls": count}
    (root / "analysis.json").write_text(json.dumps(result, indent=2))
    with (root / "process-aggregates.csv").open("w") as f:
        writer = csv.writer(f)
        writer.writerow(["process", "arm", "cell", "meanNs", "p99Ns", "bytesPerCall", "cpuNsPerCall", "gc0", "gc1", "gc2", "completedCalls"])
        for p in processes:
            for c in CELLS:
                a = p["cells"][c]
                writer.writerow([p["name"], p["arm"], c, a["meanNs"], a["p99Ns"], a["bytesPerCall"], a["cpuNsPerCall"], *a["gcCollections"], a["completedCalls"]])
    lines = ["# " + profile["title"], "", f"All {count:,} completed calls retained. Each contrast and T/U decision is separate; no release pass threshold is applied.", ""]
    for key, comparison in comparisons.items():
        lines += comparison_markdown(key, comparison) + [""]
    lines += ["Complete process/cell mean/P99/CPU/allocation/GC values: `process-aggregates.csv`. Full precision: `analysis.json`. Descriptive outcomes do not clear M0's historical No-Go or establish release equivalence."]
    (root / "calculations.md").write_text("\n".join(lines) + "\n")
    print(json.dumps({"completedCalls": count, "contrasts": {k: {x: v[x] for x in ["decisions", "noiseEnvelopes", "cellSummary"]} for k, v in comparisons.items()}}, indent=2))
    return result


if __name__ == "__main__":
    analyze(Path(sys.argv[1]).resolve())
