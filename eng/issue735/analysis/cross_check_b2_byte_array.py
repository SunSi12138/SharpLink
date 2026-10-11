#!/usr/bin/env python3
"""Compare two already independently derived results; never used as raw input.

The independent reader must finish first. This script does not import either
reader. root's strict-root-recomputed.json is only a final comparison target.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require(value, label):
    if not value:
        raise ValueError(label)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("population_dir", type=Path)
    args = p.parse_args()
    folder = args.population_dir.resolve()
    independent_path = folder / "independent-processes.json"
    comparisons_path = folder / "independent-paired-comparisons.json"
    verification_path = folder / "independent-verification.json"
    strict_path = folder / "strict-root-recomputed.json"
    rows = json.loads(independent_path.read_text())
    comparisons = json.loads(comparisons_path.read_text())
    independent = json.loads(verification_path.read_text())
    strict = json.loads(strict_path.read_text())
    require(independent["statusCounts"] == {"Passed": 72, "Failed": 0, "NotRun": 0, "InvalidEvidence": 0},
            "this final cross-check expects the actual complete 72-process population")
    require(strict["completedReports"] == 72 and strict["verifiedPerRpcQuantileReports"] == 72,
            "strict report count")
    require(strict["plan"]["runtimePopulation"] == independent["population"]
            and strict["payloadKind"] == "byte-array", "population mismatch")
    indexed = {r["name"]: r for r in rows}
    counters = 0
    numerical_values = 0
    numerical_max_absolute_difference = 0.0

    def equal_number(a, b, label):
        nonlocal numerical_values, numerical_max_absolute_difference
        require(math.isfinite(a) and math.isfinite(b)
                and math.isclose(a, b, rel_tol=2e-11, abs_tol=1e-7),
                f"{label}: {a!r} != {b!r}")
        numerical_values += 1
        numerical_max_absolute_difference = max(numerical_max_absolute_difference, abs(a - b))

    for record in strict["processRows"]:
        own = indexed[record["name"]]
        require(record["status"] == own["status"] == "Passed"
                and record["successfulRpcs"] == own["successfulRpcs"]
                and record["reportSha256"] == own["reportSha256"]
                and record["rawSampleSha256"] == own["rawSamplesSha256"], "original process identity/count/hash")
        require(record["b2Counters"] == own["pathCounters"] and all(v == 0 for v in own["pathCounters"].values()),
                "counter observation")
        counters += len(own["pathCounters"])
        runtime = strict["verifiedRuntimeBinaries"][own["arm"]]
        require(runtime["measuredBinarySha256"] == own["identity"]["measuredBinarySha256"]
                and runtime.get("runtimeAssemblySha256") == own["runtimeAssemblySha256"], "actual binary identity")
    matched_pairs = 0
    aa_pairs = 0
    alias = {"threadPoolCompletedWorkItemsPerRpc": "threadPoolWorkItemsPerRpc"}
    for cell in comparisons:
        name = f"{cell['transport']}-{cell['shape']}-{cell['items']}x{cell['bytes']}-c{cell['concurrency']}"
        if cell["kind"] == "AB":
            other = strict["cells"][name]
            require(len(other["pairs"]) == cell["validPairs"] == 4, "AB pair count")
            for own, target in zip(cell["pairs"], other["pairs"]):
                require(own["left"] == target["A"] and own["right"] == target["B"]
                        and own["round"] == target["round"], "A/B pairing labels and polarity")
                left, right = indexed[own["left"]], indexed[own["right"]]
                for metric, values in target["metrics"].items():
                    key = alias.get(metric, metric)
                    a, b = left["metrics"][key], right["metrics"][key]
                    equal_number(a, values["A"], metric + " A")
                    equal_number(b, values["B"], metric + " B")
                    equal_number(b - a, values["absoluteDelta"], metric + " delta")
                    if a != 0 and values["changePercent"] is not None:
                        equal_number((b / a - 1) * 100, values["changePercent"], metric + " percent")
                matched_pairs += 1
            for metric, values in other["metricChangesPercent"].items():
                key = alias.get(metric, metric)
                if key not in cell["summary"]:
                    continue
                derived = cell["summary"][key]
                require(values["n"] == len(derived["pairs"]), "aggregate pair count")
                for actual, expected in zip(values["raw"], derived["pairs"]):
                    equal_number(actual, expected, "aggregate original pair percent")
                equal_number(values["median"], derived["medianPairChangePct"], "median pair change")
                equal_number(values["minimum"], derived["minPairChangePct"], "minimum pair change")
                equal_number(values["maximum"], derived["maxPairChangePct"], "maximum pair change")
        else:
            other = strict["aaCells"][name]
            require(cell["validPairs"] == 2, "AA pair count")
            for key, derived in cell["summary"].items():
                metric = next((k for k, v in alias.items() if v == key), key)
                if metric not in other:
                    continue
                values = other[metric]
                require(values["n"] == len(derived["pairs"]) == 2, "AA aggregate count")
                for actual, expected in zip(values["raw"], derived["pairs"]):
                    equal_number(actual, expected, "AA original right/left percent")
                equal_number(values["median"], statistics.median(derived["pairs"]), "AA median")
                equal_number(values["minimum"], min(derived["pairs"]), "AA minimum")
                equal_number(values["maximum"], max(derived["pairs"]), "AA maximum")
            aa_pairs += cell["validPairs"]
    result = {"status": "Passed", "population": independent["population"], "processes": len(rows),
              "rawRpcRowsRecomputedBeforeThisComparison": independent["rawRpcRowsIndependentlyRecomputed"],
              "zeroCounterObservationsCompared": counters, "abPairsCompared": matched_pairs,
              "aaPairsCompared": aa_pairs, "numericalComparisons": numerical_values,
              "maximumAbsoluteFloatingPointDifference": numerical_max_absolute_difference,
              "inputs": {str(path.name): digest(path) for path in
                         [independent_path, comparisons_path, verification_path, strict_path]},
              "crossCheckScriptSha256": digest(Path(__file__)),
              "method": "Only after both readers derived results independently, compare raw-source hashes, process counters, identities, every shared AB pair value and same-cell AA ratios/aggregates.",
              "productionGo": False}
    (folder / "independent-versus-strict-cross-check.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))


if __name__ == "__main__":
    main()
