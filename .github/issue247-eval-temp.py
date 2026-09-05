import json
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])


def controller(side: str):
    rows = {}
    for path in sorted((root / "pair").glob(f"{side}-*.json")):
        for measurement in json.loads(path.read_text())["Measurements"]:
            rows.setdefault(measurement["Name"], []).append(measurement)
    if not rows:
        raise SystemExit(f"no controller rows for {side}")
    return {
        name: {
            "ns": statistics.median(x["NanosecondsPerOperation"] for x in values),
            "b": statistics.median(x["BytesPerOperation"] for x in values),
            "ns_raw": [x["NanosecondsPerOperation"] for x in values],
            "b_raw": [x["BytesPerOperation"] for x in values],
        }
        for name, values in rows.items()
    }


def rpc(side: str, token: str, field: str):
    values = [
        json.loads(path.read_text())[field]
        for path in sorted((root / "rpc").glob(f"{side}-{token}-*.json"))
    ]
    if len(values) != 5:
        raise SystemExit(f"expected five {side}/{token}/{field} samples, got {len(values)}")
    return values, statistics.median(values)


base = controller("base")
head = controller("head")

_, base_static_alloc = rpc("base", "static", "AllocatedBytesPerOperation")
_, head_static_alloc = rpc("head", "static", "AllocatedBytesPerOperation")
_, base_admission_alloc = rpc("base", "admission", "AllocatedBytesPerOperation")
_, head_admission_alloc = rpc("head", "admission", "AllocatedBytesPerOperation")
_, base_qps = rpc("base", "admission", "ThroughputPerSecond")
_, head_qps = rpc("head", "admission", "ThroughputPerSecond")
_, base_cpu = rpc("base", "admission", "CpuUsPerOperation")
_, head_cpu = rpc("head", "admission", "CpuUsPerOperation")
base_p99_raw, base_p99 = rpc("base", "admission", "P99Us")
head_p99_raw, head_p99 = rpc("head", "admission", "P99Us")

base_additional_alloc = base_admission_alloc - base_static_alloc
head_additional_alloc = head_admission_alloc - head_static_alloc
additional_alloc_reduction = 1 - head_additional_alloc / base_additional_alloc

gc_alloc_reduction = 1 - head["GlobalConcurrencyImmediate"]["b"] / base["GlobalConcurrencyImmediate"]["b"]
rate_alloc_reduction = 1 - head["GlobalRateImmediate"]["b"] / base["GlobalRateImmediate"]["b"]
gc_cpu_reduction = 1 - head["GlobalConcurrencyImmediate"]["ns"] / base["GlobalConcurrencyImmediate"]["ns"]
rate_cpu_reduction = 1 - head["GlobalRateImmediate"]["ns"] / base["GlobalRateImmediate"]["ns"]
partition_cpu_change = head["PartitionImmediate"]["ns"] / base["PartitionImmediate"]["ns"] - 1
qps_change = head_qps / base_qps - 1
rpc_cpu_reduction = 1 - head_cpu / base_cpu
p99_change = head_p99 / base_p99 - 1

go = (
    additional_alloc_reduction >= 0.25
    or gc_alloc_reduction >= 0.25
    or rate_alloc_reduction >= 0.25
    or gc_cpu_reduction >= 0.10
    or rate_cpu_reduction >= 0.10
    or qps_change >= 0.03
    or rpc_cpu_reduction >= 0.03
)

hard = {
    "go_threshold_met": go,
    "disabled_bop_no_regression": head["Disabled"]["b"] <= base["Disabled"]["b"],
    "partition_cpu_no_stable_gt_3pct_regression": partition_cpu_change <= 0.03,
    "full_rpc_p99_no_stable_gt_3pct_regression": p99_change <= 0.03,
    "static_rpc_alloc_no_regression": head_static_alloc <= base_static_alloc + 1.0,
}

lines = [
    "# Issue 247 final gate summary",
    "",
    "| Scenario | base ns/op | head ns/op | CPU delta | base B/op | head B/op | alloc delta |",
    "|---|---:|---:|---:|---:|---:|---:|",
]
for name in base:
    b = base[name]
    h = head[name]
    cpu_delta = (h["ns"] / b["ns"] - 1) * 100 if b["ns"] else 0
    alloc_delta = (h["b"] / b["b"] - 1) * 100 if b["b"] else 0
    lines.append(
        f"| {name} | {b['ns']:.2f} | {h['ns']:.2f} | {cpu_delta:+.2f}% | "
        f"{b['b']:.1f} | {h['b']:.1f} | {alloc_delta:+.2f}% |"
    )

lines += [
    "",
    "## Full tiny RPC medians (five alternating rounds)",
    "",
    f"- Static allocation: {base_static_alloc:.2f} -> {head_static_alloc:.2f} B/op",
    f"- Admission allocation: {base_admission_alloc:.2f} -> {head_admission_alloc:.2f} B/op",
    f"- Admission additional allocation: {base_additional_alloc:.2f} -> {head_additional_alloc:.2f} B/op "
    f"({additional_alloc_reduction * 100:+.2f}% reduction)",
    f"- Admission QPS: {base_qps:.2f} -> {head_qps:.2f} ({qps_change * 100:+.2f}%)",
    f"- Admission CPU: {base_cpu:.2f} -> {head_cpu:.2f} us/op ({rpc_cpu_reduction * 100:+.2f}% reduction)",
    f"- Admission P99: {base_p99:.2f} -> {head_p99:.2f} us ({p99_change * 100:+.2f}%)",
    "",
    "## Gate metrics",
    "",
    f"- Global concurrency controller allocation reduction: {gc_alloc_reduction * 100:.2f}%",
    f"- Global rate controller allocation reduction: {rate_alloc_reduction * 100:.2f}%",
    f"- Global concurrency controller CPU reduction: {gc_cpu_reduction * 100:.2f}%",
    f"- Global rate controller CPU reduction: {rate_cpu_reduction * 100:.2f}%",
    f"- Partition controller CPU change: {partition_cpu_change * 100:+.2f}%",
    "",
    "## Hard gates",
    "",
]
lines += [f"- {'PASS' if passed else 'FAIL'}: {name}" for name, passed in hard.items()]
lines += [
    "",
    "Raw admission P99 base: " + ", ".join(f"{x:.3f}" for x in base_p99_raw),
    "Raw admission P99 head: " + ", ".join(f"{x:.3f}" for x in head_p99_raw),
]

summary = "\n".join(lines) + "\n"
(root / "gate-summary.md").write_text(summary)
print(summary)

if not all(hard.values()):
    raise SystemExit("issue 247 candidate failed a hard gate")
