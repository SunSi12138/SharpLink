#!/usr/bin/env python3
"""Audit all three fixed-host replications; host is the aggregation unit, not an iteration."""
import argparse
from collections import defaultdict
import json
import math
from pathlib import Path
from statistics import mean, median, stdev

from bridge_confirm import BASE, CANDIDATE, TREE, EXPERIMENT, HOSTS, AA_ORDER, FOCUS, PRIMARY, case_name
from attribution_report import validate_rpc, validate_gate
from summarize import actual_bdn_measurements, paired, require
from boundary_pair import ORDER, CASES


def read(path):
    return json.loads(path.read_text())


def command(path, last, plan, arm, timed=True):
    record = read(path / "command.json")
    require(record["exitCode"] == 0, f"Failed command {path}")
    require(record["startedUnixSeconds"] >= last and record["completedUnixSeconds"] >= record["startedUnixSeconds"], "Overlap/order")
    env = record["environment"]
    require(env["SHARPLINK_EXPECTED_MAGIC"] == "183" and env["SharpLinkStaticBindingPrototype"] == "true", "Dialect")
    require(env["SHARPLINK_BENCHMARK_SHA"] == plan["commits"][arm], "Command source")
    if timed:
        require(record["argv"][:3] == ["taskset", "-c", ",".join(map(str, plan["cpuAffinity"]))], "CPU affinity")
        require(env["DOTNET_PROCESSOR_COUNT"] == "2" and env["DOTNET_TieredCompilation"] == env["DOTNET_TieredPGO"] == "1", "Runtime knobs")
    return record["completedUnixSeconds"]


def audit_host(root):
    plan = read(root / "confirmation-plan.json")
    require(plan["replicate"] in HOSTS and tuple(plan["replicates"]) == HOSTS, "Replication plan")
    require(plan["commits"] == {"old": BASE, "candidate": CANDIDATE} and plan["candidateTree"] == TREE, "Candidate identity")
    require(plan["experimentSha"] == EXPERIMENT and tuple(plan["order"]) == ORDER, "Driver/order")
    require(tuple(plan["primaryMetrics"]) == PRIMARY and plan["stage2Authorized"] is False and plan["originalDevRetested"] is False, "Endpoint/scope change")
    require(plan["runAttempt"] == 1 and plan["aaOrder"] == AA_ORDER and plan["aaCases"] == list(map(case_name, FOCUS)), "AA plan")
    require(read(root / "confirmation-failures.json") == [] and read(root / "measurements/execution-failures.json") == [], "Failures retained")
    identity = read(root / "candidate-identity.json")
    require(identity["base"] == BASE and identity["localCandidateSha"] == CANDIDATE and identity["tree"] == TREE, "Candidate substituted")
    done = read(root / "completed.json")
    require(done["replicate"] == plan["replicate"] and done["executionValidated"] is True and done["stage2Authorized"] is False, "Incomplete run")
    for k, v in {"canonicalRpcExecutions": 64, "bdnActual": 720, "allocationCases": 32, "aaRpcExecutions": 24, "jitRpcExecutions": 4}.items():
        require(done[k] == v, "Incomplete plan: " + k)
    base = root / "measurements"
    provenance = read(base / "provenance.json")
    require(provenance["commits"] == plan["commits"] and provenance["order"] == plan["order"] and provenance["cpuAffinity"] == plan["cpuAffinity"], "Provenance")
    require(provenance["warmup"] == 10000 and provenance["operations"] == 100000 and len(set(provenance["sdk"].values())) == 1, "Workload/SDK")
    values = defaultdict(lambda: {"base": [], "head": []})
    runtimes = set()
    last = 0
    for i, arm in enumerate(ORDER, 1):
        slot = base / "slots" / f"{i:02d}-{arm}"
        label = "base" if arm == "old" else "head"
        last = command(slot / "bdn-command", last, plan, arm)
        for name, samples in actual_bdn_measurements(slot / "bdn").items():
            values["BDN/" + name][label].append(median(median(samples[j:j+15]) for j in (0, 15, 30)))
        for case in CASES:
            name = case_name(case)
            dest = slot / "rpc" / name
            last = command(dest, last, plan, arm)
            value = read(dest / "result.json")
            runtimes.add(value["Runtime"])
            for k, v in validate_rpc(value, "B", name).items():
                values[name + "/" + k][label].append(v)
            for k in ("BindMilliseconds", "FirstCallMicroseconds"):
                values[name + "/" + k][label].append(value[k])
        last = command(slot / "allocation", last, plan, arm, False)
        validate_gate(read(slot / "allocation/result.json"))
    negative = read(base / "negative-control/command.json")
    require(negative["exitCode"] != 0 and negative["startedUnixSeconds"] >= last, "Negative control")
    last = negative["completedUnixSeconds"]
    validate_gate(read(base / "negative-control/result.json"), True)
    comparisons = {k: paired(v) for k, v in values.items()}
    saved = read(base / "summary.json")
    require(comparisons == saved["comparisons"] and saved["stage2Authorized"] is False, "Stored comparison differs from raw")
    require(len(runtimes) == 1 and saved["runtime"] in runtimes, "Runtime mismatch")
    aa = read(root / "aa-plan.json")
    require(aa["passed"] is True and aa["order"] == AA_ORDER and len(aa["slots"]) == 8, "Incomplete AA")
    require(aa["assemblySha256"] and aa["assemblySha256"] == provenance["assemblies"]["old"], "AA binary identity")
    same = defaultdict(lambda: {"base": [], "head": []})
    for i, (label, slot_record) in enumerate(zip(AA_ORDER, aa["slots"]), 1):
        rel = f"aa/{i:02d}-{label}"
        require(slot_record == {"path": rel, "label": label, "arm": "old", "assemblySha256": aa["assemblySha256"]}, "AA slot")
        for case in FOCUS:
            name = case_name(case)
            dest = root / rel / name
            last = command(dest, last, plan, "old")
            require(read(dest / "command.json")["cwd"] == aa["sameCheckout"], "AA checkout substitution")
            value = read(dest / "result.json")
            require(value["Runtime"] in runtimes, "AA runtime")
            for k, v in validate_rpc(value, "B", name).items():
                same[name + "/" + k]["base" if label == "X" else "head"].append(v)
    return {"replicate": plan["replicate"], "host": plan["host"], "bootId": plan["bootId"],
            "runId": plan["runId"], "driverSha": plan["driverSha"], "runtime": saved["runtime"],
            "sdk": provenance["sdk"], "assemblies": provenance["assemblies"],
            "comparisons": comparisons, "aa": {k: paired(v) for k, v in same.items()},
            "negativeControlMedian": read(base / "negative-control/result.json")["cases"][0]["medianBytesPerOperation"],
            "jitCoverage": read(root / "unary-jit/coverage.json")}


def host_interval(logs):
    require(len(logs) == 3 and all(math.isfinite(x) for x in logs), "Exactly three finite host effects required")
    center = mean(logs)
    se = stdev(logs) / math.sqrt(3)
    radius = 4.302652729911275 * se
    if se == 0:
        p = 1.0 if center == 0 else 0.0
    else:
        t = abs(center / se)
        # Exact two-sided tail of Student-t(df=2), rationalized to avoid cancellation.
        z = math.hypot(t, math.sqrt(2))
        p = 2 / (z * (z + t))
    return {"hostLogEffects": logs, "hostGeometricRatios": [math.exp(x) for x in logs],
            "geometricRatio": math.exp(center), "host95Ci": [math.exp(center-radius), math.exp(center+radius)],
            "df": 2, "twoSidedP": p}


def holm(pvalues):
    ordered = sorted(pvalues, key=pvalues.get)
    result = {}
    previous = 0.0
    for i, key in enumerate(ordered):
        previous = max(previous, min(1.0, (len(ordered)-i) * pvalues[key]))
        result[key] = previous
    return result


def aggregate(hosts):
    require(len(hosts) == 3 and {h["replicate"] for h in hosts} == set(HOSTS), "All three planned hosts required")
    require(len({h["bootId"] for h in hosts}) == 3, "Same VM reused as independent host")
    require(len({h["driverSha"] for h in hosts}) == len({h["runId"] for h in hosts}) == 1, "Mixed cohorts")
    hosts = sorted(hosts, key=lambda h: h["replicate"])
    results = {}
    for key in hosts[0]["comparisons"]:
        measures = [h["comparisons"][key] for h in hosts]
        if not all("pairedGeometricRatio" in x for x in measures):
            continue  # Zero-valued drain remains in the per-host raw summaries, never divided.
        logs = [mean(math.log(b/a) for a, b in zip(x["baseSamples"], x["headSamples"])) for x in measures]
        value = host_interval(logs)
        value["positivePairsPerHost"] = [sum(b > a for a,b in zip(x["baseSamples"], x["headSamples"])) for x in measures]
        value["medianRatiosPerHost"] = [x["headOverBase"] for x in measures]
        results[key] = value
    adjusted = holm({key: results[key]["twoSidedP"] for key in PRIMARY})
    for key in PRIMARY:
        results[key]["holmAdjustedP"] = adjusted[key]
    return {"schema": 1, "stage2Authorized": False, "originalDevRetested": False,
            "independentHostCount": 3, "hosts": hosts, "aggregate": results, "primaryMetrics": PRIMARY,
            "canonicalRpcExecutions": 192, "aaRpcExecutions": 72, "rawLatencyValues": 52800000,
            "bdnActualMeasurements": 2160, "allocationCases": 96,
            "limits": "Replication of bridge versus old static only, not original-dev acceptance. Each host contributes mean of four log ratios; three host means (df=2) are the inference units. Twelve pairs are not treated as independent hosts. Holm adjustment for four predeclared endpoints only; all other intervals exploratory. Host effects assumed approximately independent/normal. AA occurs after paired timing and is not subtracted. No equivalence margin; nonsignificance does not prove equality. Discovery run kept separate."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("roots", nargs=3, type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = aggregate([audit_host(root) for root in args.roots])
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "confirmation-summary.json").write_text(json.dumps(result, indent=2) + "\n")
    lines = ["# Fixed bridge: three-host confirmation", "", result["limits"], "",
             "| Endpoint | VM1 B/A | VM2 B/A | VM3 B/A | Host geometric mean | Host 95% CI | Holm p |",
             "|---|---:|---:|---:|---:|---|---:|"]
    for key in PRIMARY:
        value = result["aggregate"][key]
        ratios = " | ".join(f"{x:.4f}" for x in value["hostGeometricRatios"])
        lo, hi = value["host95Ci"]
        lines.append(f"| {key} | {ratios} | {value['geometricRatio']:.4f} | {lo:.4f}–{hi:.4f} | {value['holmAdjustedP']:.4f} |")
    (args.output / "confirmation-summary.md").write_text("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
