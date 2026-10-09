#!/usr/bin/env python3
"""Native transport evidence is separate from JIT and cannot pass by relabeling it."""
import argparse
import collections
import importlib.util
import json
from pathlib import Path
import statistics

SPEC = importlib.util.spec_from_file_location("budget", Path(__file__).with_name("verify-ready-writer-budget.py"))
BUDGET = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUDGET)


def expected_plan():
    result = []
    for transport in ("sharedmemory", "tcp"):
        for size, items, window in ((16, 2048, 8192), (4096, 128, 524288)):
            for launch in (0, 1):
                result.append([transport, "nativeaot", 128, items, size, window, 16384, launch, 8192])
    return result


def validate_document(document, source, case):
    metadata = document["metadata"]
    if metadata.get("DynamicCodeSupported") is not False or metadata.get("Pgo") != "nativeaot":
        raise ValueError("Native execution marker missing; a JIT report is not native evidence")
    return BUDGET.validate_document(document, source, case)


def validate_provenance(provenance):
    for key, size in (("source_tree", 40), ("host_sha256", 64)):
        value = provenance.get(key, "")
        if len(value) != size or any(c not in "0123456789abcdef" for c in value):
            raise ValueError("Invalid source/binary hash")
    if provenance.get("plan") != expected_plan() or provenance.get("runtime") != "nativeaot":
        raise ValueError("Altered native population")
    if (provenance.get("rounds"), provenance.get("slots"), provenance.get("quanta"),
            provenance.get("allocation_diagnostic")) != (4, 16, [1, 16], False):
        raise ValueError("Altered native comparison policy")
    affinity = provenance.get("cpu_affinity", [])
    if len(affinity) != 4 or len(set(affinity)) != 4 or any(type(x) is not int or x < 0 for x in affinity):
        raise ValueError("Wrong CPU affinity")


def report_name(index, case):
    return f"{index:02}-{case[0]}-b{case[4]}-w{case[5]}-r{case[7]}.json"


def read_exit(path):
    code = json.loads(path.with_suffix(".exit").read_text())["code"]
    if type(code) is not int:
        raise ValueError("Native exit code must be an integer, not a boolean or coercible value")
    return code


def coverage(root):
    """Inventory all planned processes, without deriving timings from partial data."""
    root = Path(root)
    provenance = json.loads((root / "provenance.json").read_text())
    validate_provenance(provenance)
    records = []
    names = set()
    for index, case in enumerate(expected_plan()):
        name = report_name(index, case)
        names.add(name)
        path = root / name
        record = dict(index=index, report=name, status="missing", validated_rows=0)
        try:
            if path.with_suffix(".exit").exists():
                code = read_exit(path)
                record["exit_code"] = code
                if code != 0:
                    record.update(status="failed", detail="Nonzero exit; any partial rows are excluded")
                elif not path.exists():
                    record.update(status="missing", detail="Successful exit without report")
                else:
                    rows = validate_document(json.loads(path.read_text()), provenance["source_tree"], case)
                    record.update(status="complete", validated_rows=len(rows))
            else:
                record["detail"] = "Missing process exit record"
        except (ValueError, KeyError, TypeError, OSError) as error:
            record.update(status="invalid", detail=str(error))
        records.append(record)
    extras = sorted(p.name for p in root.glob("[0-9]*.json") if p.name not in names)
    complete = sum(record["status"] == "complete" for record in records)
    return dict(source_tree=provenance["source_tree"], planned=8, complete=complete,
                accepted=complete == 8 and not extras, unexpected_reports=extras, cases=records)


def summarize(root):
    root = Path(root)
    provenance = json.loads((root / "provenance.json").read_text())
    validate_provenance(provenance)
    names = set()
    rows = []
    for index, case in enumerate(expected_plan()):
        name = report_name(index, case)
        names.add(name)
        path = root / name
        if read_exit(path) != 0:
            raise ValueError("Nonzero native process exit")
        rows.extend(dict(row, Launch=case[7]) for row in validate_document(
            json.loads(path.read_text()), provenance["source_tree"], case))
    if {p.name for p in root.glob("[0-9]*.json")} != names or len(rows) != 128:
        raise ValueError("Missing or additional native reports")
    groups = collections.defaultdict(list)
    for row in rows:
        groups[(row["Transport"], row["ItemBytes"])].append(row)
    lines = ["# Native ready-writer transport controls", "", f"Exact tree `{provenance['source_tree']}`.",
             "8 processes / 128 samples; two AB/BA launches, four rounds per mode. Same 8 KiB prepared cap on both sides.", "",
             "| Transport | Item B | Quantum | A item/s | B3 item/s | Throughput delta | CPU delta | Allocation delta B/item | Launch deltas |",
             "|---|---:|---:|---:|---:|---:|---:|---:|---|"]
    median = lambda samples, key: statistics.median(s[key] for s in samples)
    for (transport, size), group in sorted(groups.items()):
        for quantum in (1, 16):
            a = [s for s in group if s["Mode"] == f"A-ready/q{quantum}"]
            b = [s for s in group if s["Mode"] == f"B3-ready/q{quantum}"]
            speed = 100 * (median(b, "ItemsPerSecond") / median(a, "ItemsPerSecond") - 1)
            cpu = 100 * (median(b, "CpuMs") / median(a, "CpuMs") - 1)
            allocation = median(b, "AllocatedBytesPerItem") - median(a, "AllocatedBytesPerItem")
            launches = [100 * (median([s for s in b if s["Launch"] == launch], "ItemsPerSecond") /
                               median([s for s in a if s["Launch"] == launch], "ItemsPerSecond") - 1) for launch in (0, 1)]
            lines.append(f"| {transport} | {size} | {quantum} | {median(a, 'ItemsPerSecond'):.0f} | {median(b, 'ItemsPerSecond'):.0f} | {speed:+.2f}% | {cpu:+.2f}% | {allocation:+.3f} | {launches[0]:+.2f}%, {launches[1]:+.2f}% |")
    lines += ["", "Positive throughput is faster; positive CPU/allocation is a regression. All negative controls remain. This is balanced-wire fixed-lifecycle research, not full RPC acceptance. Native and JIT absolute timings are not pooled."]
    return "\n".join(lines) + "\n", rows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("--coverage-only", action="store_true")
    args = parser.parse_args()
    if args.coverage_only:
        result = coverage(args.root)
        (args.root / "coverage.json").write_text(json.dumps(result, indent=2))
        lines = ["# Native population coverage", "", "Inventory only; not a partial performance summary.", "",
                 "| Process | Status | Validated rows | Exit |", "|---|---|---:|---|"]
        for case in result["cases"]:
            lines.append(f"| {case['report']} | {case['status']} | {case['validated_rows']} | {case.get('exit_code', 'missing/invalid')} |")
        if result["unexpected_reports"]:
            lines += ["", "Unexpected reports: " + ", ".join(result["unexpected_reports"])]
        (args.root / "coverage.md").write_text("\n".join(lines) + "\n")
        print(f"Native population: {result['complete']}/8 complete; accepted={result['accepted']}")
        if not result["accepted"]:
            raise SystemExit(1)
        return
    text, rows = summarize(args.root)
    (args.root / "summary.md").write_text(text)
    print(f"PASS 8 native processes / {len(rows)} rows with exact source, credits and queue budgets")


if __name__ == "__main__":
    main()
