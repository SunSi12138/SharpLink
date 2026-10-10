#!/usr/bin/env python3
"""Summarize provenance-checked, equal-source #741 full-RPC A/B and A/A controls."""
import json
import statistics
import sys
from collections import defaultdict
from pathlib import Path

root = Path(sys.argv[1])
mode, base_sha, head_sha = sys.argv[2:5]
docs = defaultdict(dict)
aa = defaultdict(dict)
for file in sorted((root / "raw").glob("*.json")):
    parts = file.stem.split("__")
    if len(parts) != 5:
        raise SystemExit(f"Unexpected filename {file.name}")
    transport, scenario, concurrency, revision, repeat = parts
    if not concurrency.startswith("c") or not repeat.startswith("r"):
        raise SystemExit(f"Unexpected dimensions for {file.name}")
    data = json.loads(file.read_text())
    expected_sha = base_sha if revision == "base" else head_sha
    if (data.get("commit") != expected_sha or data.get("runtimeMode") != mode
            or data.get("transport") != transport or data.get("scenario") != scenario
            or data.get("concurrency") != int(concurrency[1:])):
        raise SystemExit(f"Invalid provenance for {file.name}")
    if (data.get("validationFailures") != 0 or data.get("operations", 0) <= 0
            or data.get("physicalAttempts") !=
            data.get("operations") * data.get("expectedAttemptsPerCall")):
        raise SystemExit(f"Invalid full-RPC execution for {file.name}")
    k = (transport, scenario, int(concurrency[1:]))
    dest = aa if revision.startswith("aa") else docs
    key = (revision, repeat)
    if key in dest[k]:
        raise SystemExit(f"Duplicate sample {file}")
    dest[k][key] = data

expected_reps = 6 if mode == "jit" else 4
if not docs:
    raise SystemExit("No A/B result documents found")
metrics = [
    ("throughputOperationsPerSecond", "QPS"),
    ("cpuUsPerOperation", "CPU/op"),
    ("allocatedBytesPerOperation", "B/op"),
    ("p99Us", "P99"),
    ("p999Us", "P999"),
]
summary = [
    f"# Issue #741 {mode.upper()} full-RPC unary/retry A/B",
    "",
    f"- Base: `{base_sha}`",
    f"- Candidate: `{head_sha}`",
    f"- Paired samples per configuration: {expected_reps}; balanced AB/BA order.",
    "- Both revisions were built from identical benchmark source and run on the same hosted runner.",
    "- CPU and allocated bytes include in-process server plus client; no telemetry listener was enabled.",
    "- QPS/CPU/tail-latency are hosted-runner observations, not formal statistical proof.",
    "",
    "| Transport | Scenario | c | Base B/op | Head B/op | Δ B/op | QPS Δ | CPU/op Δ | P99 Δ | P999 Δ |",
    "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|",
]
pairs = []
def pct(head, base):
    return (head / base - 1) * 100 if base else 0.0

for (transport, scenario, concurrency), samples in sorted(docs.items()):
    deltas = {k: [] for k, _ in metrics}
    base_values = {k: [] for k, _ in metrics}
    head_values = {k: [] for k, _ in metrics}
    for index in range(expected_reps):
        repeat = f"r{index}"
        baseline = samples.get(("base", repeat))
        candidate = samples.get(("candidate", repeat))
        if baseline is None or candidate is None:
            raise SystemExit(f"Missing {transport}/{scenario}/c{concurrency}/{repeat} A/B pair")
        pair = {
            "mode": mode, "transport": transport, "scenario": scenario,
            "concurrency": concurrency, "repeat": repeat
        }
        for key, _ in metrics:
            b = baseline[key]
            h = candidate[key]
            base_values[key].append(b)
            head_values[key].append(h)
            deltas[key].append(h - b if key == "allocatedBytesPerOperation" else pct(h, b))
            pair[key + "Base"] = b
            pair[key + "Candidate"] = h
        pairs.append(pair)
    med = {key: statistics.median(values) for key, values in deltas.items()}
    summary.append(
        f"| {transport} | {scenario} | {concurrency} | "
        f"{statistics.median(base_values['allocatedBytesPerOperation']):.1f} | "
        f"{statistics.median(head_values['allocatedBytesPerOperation']):.1f} | "
        f"{med['allocatedBytesPerOperation']:+.1f} | "
        f"{med['throughputOperationsPerSecond']:+.1f}% | "
        f"{med['cpuUsPerOperation']:+.1f}% | "
        f"{med['p99Us']:+.1f}% | {med['p999Us']:+.1f}% |"
    )
summary.extend([
    "",
    "## A/A measurement noise control",
    "",
    "| Transport | Scenario | c | A/A QPS Δ | A/A allocation Δ |",
    "|---|---|---:|---:|---:|"
])
for (transport, scenario, concurrency), samples in sorted(aa.items()):
    qps, allocations = [], []
    for index in range(3):
        first = samples.get(("aa0", f"r{index}"))
        second = samples.get(("aa1", f"r{index}"))
        if first is None or second is None:
            raise SystemExit(f"Missing candidate-vs-itself control {transport}/{scenario}/{index}")
        qps.append(pct(second["throughputOperationsPerSecond"], first["throughputOperationsPerSecond"]))
        allocations.append(second["allocatedBytesPerOperation"] - first["allocatedBytesPerOperation"])
    summary.append(
        f"| {transport} | {scenario} | {concurrency} | "
        f"{statistics.median(qps):+.1f}% | {statistics.median(allocations):+.1f} B |")
if not aa:
    raise SystemExit("No negative A/A control samples found")
summary.extend([
    "",
    "## Limitations / acceptance",
    "",
    "- Expected physical attempts are validated for first-success (1), forced one retry (2), and exhausted retry (3).",
    "- This exercise covers real generated Idempotent Unary RPCs, TCP/SharedMemory and JIT/NativeAOT.",
    "- Failures/cancellations and graceful drain also require the focused lifecycle tests already in PR Fast.",
    "- Retry-path non-regression should be evaluated against the paired A/A noise control rather than an arbitrary single run.",
    "- Historical 136 B wrapper attribution is not automatically a current-head finding.",
])
(root / "summary.md").write_text("\n".join(summary) + "\n")
(root / "paired.json").write_text(json.dumps(pairs, indent=2) + "\n")
print("\n".join(summary))
