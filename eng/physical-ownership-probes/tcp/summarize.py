#!/usr/bin/env python3
"""Summarize all complete pairs, preserving ranges and descriptive uncertainty."""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import random
import statistics


METRICS = ("operationsPerSecond", "p50Us", "p99Us", "p999Us", "allocatedBytesPerOperation", "cpuUsPerOperation")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    args = parser.parse_args()
    root = args.input.resolve(strict=True)
    groups = defaultdict(dict)
    observed = defaultdict(set)
    failed = []
    raw_paths = {p.stem: p for p in (root / "runs").glob("*.json")
                 if not p.name.endswith(".host.json")}
    host_paths = {p.name.removesuffix(".host.json"): p
                  for p in (root / "runs").glob("*.host.json")}
    for sample in sorted(raw_paths.keys() | host_paths.keys()):
        scenario, repetition = sample.rsplit("-r", 1)
        round_text, variant = repetition.split("-", 1)
        mode, count_workers = scenario.rsplit("-n", 1)
        count, workers = count_workers.split("-c", 1)
        key = (mode, int(count), int(workers))
        round_number = int(round_text)
        observed[key].add(round_number)
        host = json.loads(host_paths[sample].read_text()) if sample in host_paths else None
        status, reason = None, None
        if host is None:
            status, reason = "missing-host", "Process completion was not recorded"
        elif host.get("exitCode") != 0 or host.get("status") not in (None, "success"):
            status = host.get("status", "failed")
            reason = host.get("reason", f"Process exited with code {host.get('exitCode')}")
        elif sample not in raw_paths:
            status, reason = "missing-sample", "Successful process did not write a sample"
        else:
            try:
                data = json.loads(raw_paths[sample].read_text())
                if (data["mode"], data["itemCount"], data["workers"]) != key:
                    raise ValueError("Sample scenario does not match its filename")
                for metric in METRICS:
                    if not isinstance(data[metric], (int, float)):
                        raise ValueError(f"Non-numeric metric: {metric}")
            except (json.JSONDecodeError, KeyError, TypeError, ValueError) as error:
                status, reason = "invalid-sample", str(error)
        if status:
            failed.append({"sample": sample, "mode": key[0], "items": key[1],
                           "workers": key[2], "round": round_number, "variant": variant,
                           "status": status, "reason": reason,
                           "exitCode": host.get("exitCode") if host else None,
                           "rawSamplePresent": sample in raw_paths})
            continue
        sample_key = (round_number, variant)
        if sample_key in groups[key]:
            raise ValueError(f"Duplicate sample: {key}/{sample_key}")
        groups[key][sample_key] = data
    summary = []
    incomplete = []
    for key, observed_rounds in sorted(observed.items()):
        runs = groups[key]
        rounds = sorted(observed_rounds)
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
    (root / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    (root / "summary-integrity.json").write_text(json.dumps({
        "rawSamples": len(raw_paths),
        "completedRawSamples": sum(len(runs) for runs in groups.values()),
        "failedSamples": failed,
        "incompletePairs": incomplete,
        "bootstrapReplicates": 20_000,
        "bootstrapSeed": 529,
        "interpretation": "Descriptive paired intervals, not an equivalence test or host-noise correction.",
    }, indent=2) + "\n")
    if not summary:
        raise ValueError("No complete successful baseline/candidate pairs found; inspect summary-integrity.json")
    if incomplete:
        print(f"WARNING: {len(incomplete)} incomplete pairs were preserved but not compared.")


if __name__ == "__main__":
    main()
