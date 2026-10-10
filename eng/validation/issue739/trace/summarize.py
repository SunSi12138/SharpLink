#!/usr/bin/env python3
"""Conservative allocation-tick diagnostics. No owner-coverage pass gate."""
import argparse
import collections
import hashlib
import json
import math
from pathlib import Path


def sha256(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def require_int(value, name, minimum=0):
    if type(value) is not int or value < minimum:
        raise ValueError(f"{name} must be an integer >= {minimum}")
    return value


def classify(tick, rules):
    # Explicit ordered rules, exactly one winner. Type-only rules are permitted but
    # still classify a sampled crossing object, not its entire allocation interval.
    for rule in rules:
        types = rule.get("typeContains", [])
        frames = rule.get("frameContains", [])
        if not types and not frames:
            raise ValueError("Owner rule requires typeContains or frameContains")
        if (not types or any(s in (tick.get("typeName") or "") for s in types)) and (
            not frames or any(s in frame for s in frames for frame in tick["frames"])
        ):
            return rule["owner"]
    return None


def summarize(meta, measured, ticks, rules):
    if meta.get("schemaVersion") != 1 or measured.get("schemaVersion") != 1:
        raise ValueError("Unsupported metadata/sample schema")
    if meta["sample"] != measured["sample"]:
        raise ValueError("Sample mismatch")
    a = require_int(measured["bytes"], "bytes")
    n = require_int(measured["operations"], "operations", 1)
    if meta["markerBytes"] != a or meta["operations"] != n:
        raise ValueError("Trace Stop payload does not match precise-counter sample")
    measured_pid = measured.get("processId", measured.get("pid"))
    if measured_pid != meta["pid"]:
        raise ValueError("Missing or mismatched sample processId (pid accepted as explicit fixture alias)")
    loss = meta.get("eventsLostReported")
    if loss is not None:
        require_int(loss, "eventsLostReported")
    t = k = count = invalid = missing_stack = missing_type = unresolved = 0
    object_estimate = 0.0
    object_unknown_weight = 0
    groups = collections.defaultdict(lambda: {"sampledTickWeightBytes": 0, "ticks": 0, "weightedObjectCountEstimate": 0.0, "objectSizeMissingWeightBytes": 0})
    signatures = collections.defaultdict(lambda: [0, 0])
    for tick in ticks:
        if tick.get("schemaVersion") != 1 or tick["sample"] != meta["sample"] or tick["pid"] != meta["pid"]:
            raise ValueError("Tick schema/sample/PID mismatch")
        ts = tick["timeRelativeMilliseconds"]
        if not isinstance(ts, (int, float)) or not math.isfinite(ts) or not meta["markerStartMilliseconds"] < ts < meta["markerStopMilliseconds"]:
            raise ValueError("Tick outside exclusive marker interval")
        if not isinstance(tick["frames"], list) or not all(isinstance(f, str) for f in tick["frames"]):
            raise ValueError("Invalid frame schema")
        count += 1
        w = tick.get("allocationAmount64")
        if type(w) is not int or w <= 0 or tick["eventVersion"] < 2:
            invalid += 1
            continue  # Unknown weight is never treated as zero or repaired to 100 KB.
        t += w
        if not tick["frames"]:
            missing_stack += w
        if not tick.get("typeName"):
            missing_type += w
        if tick.get("unresolvedFrames", 0):
            unresolved += w
        owner = classify(tick, rules)
        if owner is not None:
            k += w
        row = groups[owner or "UNATTRIBUTED"]
        row["sampledTickWeightBytes"] += w
        row["ticks"] += 1
        size = tick.get("objectSize")
        if type(size) is int and size > 0 and tick["eventVersion"] >= 4:
            estimate = w / size
            object_estimate += estimate
            row["weightedObjectCountEstimate"] += estimate
        else:
            object_unknown_weight += w
            row["objectSizeMissingWeightBytes"] += w
        # Raw JSONL remains the exhaustive evidence; summary keeps highest mass signatures.
        signature = (owner or "UNATTRIBUTED", tick.get("typeName"), tuple(tick["frames"]))
        signatures[signature][0] += w
        signatures[signature][1] += 1
    if count != meta["tickEvents"]:
        raise ValueError("Decoded event count differs from JSONL")
    ratio = lambda x, y: x / y if y else None
    for row in groups.values():
        row["sampledBytesPerOperationEstimate"] = row["sampledTickWeightBytes"] / n
        row["weightedObjectsPerOperationEstimate"] = row["weightedObjectCountEstimate"] / n if row["sampledTickWeightBytes"] > row["objectSizeMissingWeightBytes"] else None
    warnings = ["Tick type/stack describes the threshold-crossing object, not all bytes in its approximately 100 KB interval.",
                "Start/Stop marker window is wider than precise counter interval; crossing ticks can include allocation before start or omit allocation after the last tick.",
                "No rescaling, subtraction of controls, or 90% exact owner-coverage claim is performed.",
                "weight/ObjectSize is a biased sampled object-count estimate, never an exact object count."]
    if loss is None: warnings.append("Event-loss accounting unavailable; unknown is not zero.")
    elif loss: warnings.append("Trace reports lost events; allocation/type/stack completeness is degraded.")
    if invalid: warnings.append("Some allocation ticks have unknown/invalid weight; represented mass is incomplete.")
    if t > a: warnings.append("Tick weight exceeds precise interval bytes; retain overcapture without clamping.")
    return {
        "schemaVersion": 1, "sample": meta["sample"], "pid": meta["pid"], "operations": n,
        "preciseProcessBytesA": a, "preciseBytesPerOperation": a / n,
        "representedTickWeightBytesT": t, "classifiedSampleWeightBytesK": k,
        "captureMassRatioTOverA": ratio(t, a), "classifiedSampleFractionKOverT": ratio(k, t),
        "sampleSupportedMassRatioKOverA": ratio(k, a),
        "signedPreciseMinusTickBytesAminusT": a - t,
        "unclassifiedTickWeightBytesTminusK": t - k,
        "signedPreciseMinusClassifiedBytesAminusK": a - k,
        "tickEvents": count, "invalidOrUnknownWeightTickEvents": invalid,
        "missingStackWeightBytes": missing_stack, "missingTypeWeightBytes": missing_type,
        "partiallyUnresolvedStackWeightBytes": unresolved,
        "eventsLostReported": loss, "lossStatus": "unknown" if loss is None else "reported-zero" if loss == 0 else "reported-loss",
        "exactObjectsPerOperation": None, "exactOwnerCoverage": None,
        "weightedObjectsPerOperationEstimate": object_estimate / n if t > object_unknown_weight else None,
        "objectEstimateStatus": "unavailable" if t == object_unknown_weight else "partial-sample-estimate" if object_unknown_weight or invalid else "sample-estimate",
        "objectSizeMissingWeightBytes": object_unknown_weight,
        "ownerRulePolicy": "ordered first match; AND across nonempty type/frame groups, OR within each group",
        "owners": dict(sorted(groups.items(), key=lambda x: -x[1]["sampledTickWeightBytes"])),
        "topSampleSignatures": [{"owner": s[0], "typeName": s[1], "frames": s[2], "sampledTickWeightBytes": v[0], "ticks": v[1]} for s, v in sorted(signatures.items(), key=lambda x: -x[1][0])[:100]],
        "warnings": warnings,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("prefix", help="decoder output prefix")
    parser.add_argument("sample_json")
    parser.add_argument("--capture", help="capture metadata; defaults beside decoder metadata using trace filename")
    parser.add_argument("--rules", default=str(Path(__file__).with_name("owners.json")))
    args = parser.parse_args()
    prefix = args.prefix
    meta_path, ticks_path = prefix + ".decode.json", prefix + ".ticks.jsonl"
    meta = json.loads(Path(meta_path).read_text())
    measured = json.loads(Path(args.sample_json).read_text())
    rules = json.loads(Path(args.rules).read_text())
    with open(ticks_path) as raw:
        summary = summarize(meta, measured, (json.loads(line) for line in raw), rules)
    summary["evidence"] = {"traceSha256": meta["traceSha256"], "decodeSha256": sha256(meta_path), "ticksSha256": sha256(ticks_path), "sampleSha256": sha256(args.sample_json), "rulesSha256": sha256(args.rules), "decoderVersion": meta["decoderVersion"], "runtime": measured.get("runtime"), "framework": measured.get("framework"), "sourceSha": measured.get("sourceSha")}
    capture_path = Path(args.capture) if args.capture else Path(meta_path).parent / (meta["traceFile"] + ".capture.json")
    if capture_path.exists():
        capture = json.loads(capture_path.read_text())
        if capture.get("schemaVersion") != 1 or capture.get("traceSha256") != meta["traceSha256"]:
            raise ValueError("Capture metadata schema or trace hash mismatch")
        if capture.get("exitCode") != 0 or capture.get("abortReason") is not None:
            raise ValueError("Capture did not complete successfully")
        summary["capture"] = capture
        summary["evidence"]["captureSha256"] = sha256(capture_path)
    else:
        summary["capture"] = None
        summary["warnings"].append("Capture metadata unavailable: provider/start-ready/drain/tool status unknown.")
    summary["markerWindow"] = {key: meta[key] for key in ("markerStartMilliseconds", "markerStopMilliseconds", "markerDurationMilliseconds", "markerBoundary")}
    Path(prefix + ".summary.json").write_text(json.dumps(summary, indent=2, allow_nan=False) + "\n")


if __name__ == "__main__":
    main()
