#!/usr/bin/env python3
"""Summarize all complete pairs, preserving ranges and descriptive uncertainty."""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import random
import statistics


METRICS = ("operationsPerSecond", "p50Us", "p99Us", "allocatedBytesPerOperation", "cpuUsPerOperation")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    args = parser.parse_args()
    root = args.input.resolve(strict=True)
    groups = defaultdict(dict)
    raw_samples = 0
    for path in sorted((root / "runs").glob("*.json")):
        if path.name.endswith(".host.json"):
            continue
        data = json.loads(path.read_text())
        round_text, variant = path.stem.rsplit("-r", 1)[1].split("-", 1)
        key = (data["mode"], data["itemCount"], data["workers"])
        sample_key = (int(round_text), variant)
        if sample_key in groups[key]:
            raise ValueError(f"Duplicate sample: {key}/{sample_key}")
        groups[key][sample_key] = data
        raw_samples += 1
    summary = []
    incomplete = []
    for key, runs in sorted(groups.items()):
        rounds = sorted({round_number for round_number, _ in runs})
        pairs = [round_number for round_number in rounds
                 if (round_number, "baseline") in runs and (round_number, "candidate") in runs]
        incomplete.extend({"mode": key[0], "items": key[1], "workers": key[2], "round": r}
                          for r in rounds if r not in pairs)
        if not pairs:
            continue
        record = {"mode": key[0], "items": key[1], "workers": key[2],
                  "pairs": len(pairs), "metrics": {}}
        for metric in METRICS:
            baseline = [runs[(r, "baseline")][metric] for r in pairs]
            candidate = [runs[(r, "candidate")][metric] for r in pairs]
            ratios = [c / b for b, c in zip(baseline, candidate)]
            rng = random.Random(529)
            bootstrap = sorted(statistics.mean(rng.choices(ratios, k=len(ratios)))
                               for _ in range(20_000))
            record["metrics"][metric] = {
                "baselineMedian": statistics.median(baseline),
                "candidateMedian": statistics.median(candidate),
                "baselineRange": [min(baseline), max(baseline)],
                "candidateRange": [min(candidate), max(candidate)],
                "ratioOfMedians": statistics.median(candidate) / statistics.median(baseline),
                "pairedRatios": ratios,
                "pairedMeanRatio": statistics.mean(ratios),
                "pairedMeanBootstrap95": [bootstrap[499], bootstrap[19_499]],
            }
        summary.append(record)
        print(key, "pairs", len(pairs))
        for metric, data in record["metrics"].items():
            interval = [(value - 1) * 100 for value in data["pairedMeanBootstrap95"]]
            print(f" {metric}: B {data['baselineMedian']:.3f} {data['baselineRange']}, "
                  f"C {data['candidateMedian']:.3f} {data['candidateRange']}, "
                  f"paired mean delta {100 * (data['pairedMeanRatio'] - 1):+.2f}%, "
                  f"descriptive bootstrap95 {interval}")
    if not summary:
        raise ValueError("No complete baseline/candidate pairs found")
    (root / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    (root / "summary-integrity.json").write_text(json.dumps({
        "completedRawSamples": raw_samples,
        "incompletePairs": incomplete,
        "bootstrapReplicates": 20_000,
        "bootstrapSeed": 529,
        "interpretation": "Descriptive paired intervals, not an equivalence test or host-noise correction.",
    }, indent=2) + "\n")
    if incomplete:
        print(f"WARNING: {len(incomplete)} incomplete pairs were preserved but not compared.")


if __name__ == "__main__":
    main()
