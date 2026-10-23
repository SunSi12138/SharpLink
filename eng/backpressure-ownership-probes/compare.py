#!/usr/bin/env python3
"""Compare raw artifacts without dropping nonzero, missing or failed samples."""
import difflib
import json
from pathlib import Path
import re
import statistics
import sys

MODES = ("FullOpts", "Tier0", "Tier1NoPGO", "DefaultTiered")


def normalize(text):
    # Address-shaped long hex operands and generated state-machine sequence suffixes
    # differ across processes/builds. Raw listings and token-resolved IL remain available:
    # this heuristic is not a proof that every replaced immediate was an address.
    text = re.sub(r"0x[0-9a-fA-F]{9,}", "<LONG_HEX>", text)
    return re.sub(r"d__\d+", "d__N", text)


def bodies(path):
    result = {}
    for block in path.read_text().split("; Assembly listing for method ")[1:]:
        title = block.splitlines()[0]
        size = re.search(r"; Total bytes of code (\d+)", block)
        if size and title.startswith("SharpLink."):
            result.setdefault(normalize(title), []).append(
                {"codeBytes": int(size.group(1)), "body": normalize(block).strip() + "\n"})
    return result


def main():
    root = Path(sys.argv[1]).resolve()
    report = {"methods": [], "allocations": {}, "observedNativeHeaders": {}, "missing": []}
    for mode in MODES:
        paths = {side: root / side / f"jit-{mode}.asm" for side in ("baseline", "candidate")}
        if not all(path.exists() for path in paths.values()):
            report["missing"].extend(str(path.relative_to(root)) for path in paths.values() if not path.exists())
            continue
        baseline, candidate = (bodies(paths[side]) for side in ("baseline", "candidate"))
        report["observedNativeHeaders"][mode] = {"baseline": sorted(baseline), "candidate": sorted(candidate)}
        for key in sorted(baseline.keys() | candidate.keys()):
            left, right = baseline.get(key, []), candidate.get(key, [])
            report["methods"].append({"mode": mode, "method": key,
                                      "baselineCodeBytes": [item["codeBytes"] for item in left],
                                      "candidateCodeBytes": [item["codeBytes"] for item in right],
                                      "normalizedNativeIdentical": left == right})
            if left != right:
                diff = difflib.unified_diff("\n".join(item["body"] for item in left).splitlines(True),
                                            "\n".join(item["body"] for item in right).splitlines(True),
                                            fromfile="baseline", tofile="candidate")
                name = re.sub(r"[^A-Za-z0-9._-]", "_", key)
                (root / "candidate" / f"{mode}-{name[:130]}.diff").write_text("".join(diff))
    for side in ("baseline", "candidate"):
        report["allocations"][side] = {}
        for path in (root / side).glob("*.json"):
            if not path.name.startswith(("fast-", "saturated-", "late-recovery")):
                continue
            samples = json.loads(path.read_text()).get("samples", [])
            if not samples:
                continue
            metrics = {}
            for key in samples[0]:
                if key in ("batch", "operations", "contenders") or not isinstance(samples[0][key], (int, float)):
                    continue
                values = [sample[key] for sample in samples]
                metrics[key] = {"min": min(values), "median": statistics.median(values), "max": max(values),
                                "sum": sum(values), "zeroSamples": sum(value == 0 for value in values)}
            report["allocations"][side][path.name] = {"completedSamples": len(samples), "metrics": metrics}
    structures = {side: json.loads((root / side / "structure.json").read_text()) for side in ("baseline", "candidate")}
    left, right = ({method["signature"]: method for method in structures[side]["methods"]}
                   for side in ("baseline", "candidate"))
    report["il"] = [{"signature": key, "resolvedIlIdentical": left[key]["il"] == right[key]["il"],
                     "baselineLength": left[key]["ilLength"], "candidateLength": right[key]["ilLength"]}
                    for key in sorted(left.keys() & right.keys())]
    report["layouts"] = {side: data["layouts"] for side, data in structures.items()}
    report["runtimeBinaries"] = {side: {key: data[key] for key in ("runtime", "architecture", "assemblySha256")}
                                for side, data in structures.items()}
    report["probeStatuses"] = json.loads((root / "status.json").read_text())
    expected_tier = {"FullOpts": "(FullOpts)", "Tier0": "(Tier0)",
                     "Tier1NoPGO": "(Tier1)", "DefaultTiered": "(Tier1)"}
    report["nativeProfileCoverage"] = {}
    for side, statuses in report["probeStatuses"].items():
        for mode, tier in expected_tier.items():
            if f"fast-{mode}" in statuses:
                headers = report["observedNativeHeaders"].get(mode, {}).get(side, [])
                report["nativeProfileCoverage"][f"{side}/{mode}"] = any(
                    ":SendPacketWithBackpressureAsync" in header and tier in header for header in headers)
    report["partialSamples"] = {str(path.relative_to(root)): json.loads(path.read_text())
                                for path in root.glob("*/*.partial")}
    (root / "comparison.json").write_text(json.dumps(report, indent=2) + "\n")
    print("Comparison preserves all completed and partial samples; missing profiles are explicitly listed.")
    for item in report["methods"]:
        print(item["mode"], item["normalizedNativeIdentical"], item["baselineCodeBytes"],
              item["candidateCodeBytes"], item["method"])
    return 0 if all(report["nativeProfileCoverage"].values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
