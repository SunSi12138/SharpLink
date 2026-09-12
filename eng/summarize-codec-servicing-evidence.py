#!/usr/bin/env python3
"""Validate and summarize .NET 10 servicing compatibility evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

LANES = ("baseline", "latest")
PLATFORM_TAG = "linux-x64-hosted-desktop-coreclr-net10"
SCHEMA_VERSION = 1
IDENTITY_FIELDS = (
    "targetFramework",
    "runtimeFamily",
    "runtimeFamilySource",
    "runtimeIdentifier",
    "executionEnvironment",
    "os",
    "processArchitecture",
    "osArchitecture",
    "pointerSize",
    "isLittleEndian",
    "compilationMode",
)
REQUIRED_EXACT_FIELDS = (
    "sharpLinkCommit",
    "frameworkDescription",
    "runtimeVersion",
    "sdkVersion",
    "osVersion",
    "osArchitecture",
    "compilationMode",
)


def fail(message: str) -> None:
    raise ValueError(message)


def load_json(path: Path) -> dict[str, Any]:
    if not path.is_file():
        fail(f"Missing evidence file: {path}")
    with path.open("r", encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        fail(f"Expected JSON object in {path}")
    return value


def require_schema(value: dict[str, Any], label: str) -> None:
    if value.get("schemaVersion") != SCHEMA_VERSION:
        fail(f"{label} must have schemaVersion={SCHEMA_VERSION}")


def require_known(value: dict[str, Any], field: str, label: str) -> None:
    observed = value.get(field)
    if not isinstance(observed, str) or not observed.strip() or observed.lower() == "unknown":
        fail(f"{label} requires known {field}; observed {observed!r}")


def version_tuple(version: str, label: str) -> tuple[int, int, int]:
    match = re.match(r"^(\d+)\.(\d+)\.(\d+)", version)
    if match is None:
        fail(f"Unsupported {label} version: {version}")
    return tuple(int(part) for part in match.groups())  # type: ignore[return-value]


def hash_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_manifest(path: Path, lane: str) -> dict[str, Any]:
    manifest = load_json(path)
    require_schema(manifest, f"{lane} manifest")
    for field in REQUIRED_EXACT_FIELDS:
        require_known(manifest, field, f"{lane} manifest")

    if manifest.get("platformTag") != PLATFORM_TAG:
        fail(
            f"{lane} manifest must be {PLATFORM_TAG}; "
            f"observed {manifest.get('platformTag')!r}"
        )
    if manifest.get("targetFramework") != "net10.0":
        fail(f"{lane} manifest must target net10.0")
    if manifest.get("runtimeFamily") != "CoreCLR":
        fail(f"{lane} manifest must execute CoreCLR")
    if manifest.get("executionEnvironment") != "hosted-desktop":
        fail(f"{lane} manifest must execute on hosted-desktop")
    if manifest.get("os") != "linux" or manifest.get("processArchitecture") != "x64":
        fail(f"{lane} manifest must execute on Linux x64")
    if manifest.get("pointerSize") != 8:
        fail(f"{lane} manifest must have pointerSize=8")

    cases = manifest.get("cases")
    registry = manifest.get("fixtureRegistry")
    if not isinstance(cases, list) or not cases:
        fail(f"{lane} manifest has no cases")
    if not isinstance(registry, list) or not registry:
        fail(f"{lane} manifest has no fixtureRegistry")

    case_ids = [case.get("id") for case in cases if isinstance(case, dict)]
    registry_ids = [item.get("id") for item in registry if isinstance(item, dict)]
    if len(case_ids) != len(cases) or len(set(case_ids)) != len(case_ids):
        fail(f"{lane} manifest contains invalid or duplicate case IDs")
    if len(registry_ids) != len(registry) or len(set(registry_ids)) != len(registry_ids):
        fail(f"{lane} manifest contains invalid or duplicate fixture registry IDs")
    if set(case_ids) != set(registry_ids):
        fail(f"{lane} manifest cases and fixtureRegistry do not describe the same fixtures")

    root = path.parent
    for case in cases:
        wire_file = case.get("wireFile")
        wire_hash = case.get("wireSha256")
        if not isinstance(wire_file, str) or not isinstance(wire_hash, str):
            fail(f"{lane} manifest has invalid wire metadata for {case.get('id')}")
        wire_path = root / Path(wire_file)
        if not wire_path.is_file():
            fail(f"Missing {lane} wire artifact: {wire_path}")
        observed_hash = hash_file(wire_path)
        if observed_hash.lower() != wire_hash.lower():
            fail(
                f"{lane} wire hash mismatch for {case.get('id')}: "
                f"manifest={wire_hash}, observed={observed_hash}"
            )

    return manifest


def validate_non_servicing_identity(
    baseline: dict[str, Any], latest: dict[str, Any]
) -> None:
    for field in IDENTITY_FIELDS:
        if baseline.get(field) != latest.get(field):
            fail(
                f"Servicing lanes changed non-servicing identity field {field}: "
                f"baseline={baseline.get(field)!r}, latest={latest.get(field)!r}"
            )


def validate_consumer_identity(
    consumer: dict[str, Any], expected: dict[str, Any], consumer_lane: str
) -> None:
    require_schema(consumer, f"{consumer_lane} consumer")
    for field in REQUIRED_EXACT_FIELDS:
        require_known(consumer, field, f"{consumer_lane} consumer")

    for field in IDENTITY_FIELDS:
        if consumer.get(field) != expected.get(field):
            fail(
                f"{consumer_lane} consumer identity mismatch for {field}: "
                f"expected={expected.get(field)!r}, observed={consumer.get(field)!r}"
            )
    for field in (
        "sharpLinkCommit",
        "sdkVersion",
        "runtimeVersion",
        "frameworkDescription",
    ):
        if consumer.get(field) != expected.get(field):
            fail(
                f"{consumer_lane} consumer servicing identity mismatch for {field}: "
                f"expected={expected.get(field)!r}, observed={consumer.get(field)!r}"
            )


def validate_report(
    path: Path,
    producer_manifest: dict[str, Any],
    consumer_manifest: dict[str, Any],
    producer_lane: str,
    consumer_lane: str,
) -> dict[str, Any]:
    report = load_json(path)
    require_schema(report, f"{producer_lane}->{consumer_lane} report")

    consumer = report.get("consumer")
    if not isinstance(consumer, dict):
        fail(f"{producer_lane}->{consumer_lane} report has no consumer manifest")
    validate_consumer_identity(consumer, consumer_manifest, consumer_lane)

    cases = producer_manifest["cases"]
    cases_by_id = {case["id"]: case for case in cases}
    results = report.get("results")
    if not isinstance(results, list):
        fail(f"{producer_lane}->{consumer_lane} report has no results")
    if len(results) != len(cases):
        fail(
            f"{producer_lane}->{consumer_lane} result count mismatch: "
            f"expected={len(cases)}, actual={len(results)}"
        )

    seen: set[str] = set()
    blocking = 0
    for result in results:
        if not isinstance(result, dict):
            fail(f"{producer_lane}->{consumer_lane} report contains a non-object result")
        fixture = result.get("fixture")
        if not isinstance(fixture, str) or fixture not in cases_by_id:
            fail(f"{producer_lane}->{consumer_lane} contains unknown fixture {fixture!r}")
        if fixture in seen:
            fail(f"{producer_lane}->{consumer_lane} contains duplicate fixture {fixture}")
        seen.add(fixture)

        case = cases_by_id[fixture]
        if result.get("producer") != PLATFORM_TAG or result.get("consumer") != PLATFORM_TAG:
            fail(
                f"{producer_lane}->{consumer_lane}/{fixture} has unexpected platform tags: "
                f"producer={result.get('producer')!r}, consumer={result.get('consumer')!r}"
            )
        expected_pairs = (
            ("category", "category"),
            ("codecPath", "codecPath"),
            ("producerSize", "size"),
            ("producerFieldOffsets", "fieldOffsets"),
            ("producerWireHash", "wireSha256"),
            ("expectedLogicalValue", "expectedLogicalValue"),
        )
        for result_field, case_field in expected_pairs:
            if result.get(result_field) != case.get(case_field):
                fail(
                    f"{producer_lane}->{consumer_lane}/{fixture} producer evidence mismatch "
                    f"for {result_field}: expected={case.get(case_field)!r}, "
                    f"observed={result.get(result_field)!r}"
                )
        if result.get("producerPointerSize") != producer_manifest.get("pointerSize"):
            fail(f"{producer_lane}->{consumer_lane}/{fixture} producer pointer-size mismatch")
        if result.get("consumerPointerSize") != consumer_manifest.get("pointerSize"):
            fail(f"{producer_lane}->{consumer_lane}/{fixture} consumer pointer-size mismatch")

        is_blocking = result.get("blocking") is True
        blocking += int(is_blocking)
        if is_blocking:
            fail(
                f"{producer_lane}->{consumer_lane}/{fixture} is blocking: "
                f"classification={result.get('classification')!r}"
            )
        if result.get("crossDeserializeResult") is not True or result.get("logicalEquality") is not True:
            fail(f"{producer_lane}->{consumer_lane}/{fixture} lacks semantic cross-decode success")
        if case.get("size", 0) > 1 and (
            result.get("segmentedCrossDeserializeResult") is not True
            or result.get("segmentedLogicalEquality") is not True
        ):
            fail(f"{producer_lane}->{consumer_lane}/{fixture} lacks segmented decode success")

        byte_equal = result.get("byteForByteEquality") is True
        expected_classification = (
            "IDENTICAL_BYTES_AND_COMPATIBLE"
            if byte_equal
            else "DIFFERENT_BYTES_BUT_CROSS_COMPATIBLE"
        )
        if result.get("classification") != expected_classification:
            fail(
                f"{producer_lane}->{consumer_lane}/{fixture} classification mismatch: "
                f"expected={expected_classification}, observed={result.get('classification')!r}"
            )
        first_diff = result.get("firstDifferingByteOffset")
        if byte_equal and first_diff is not None:
            fail(f"{producer_lane}->{consumer_lane}/{fixture} byte-equal row has first diff")
        if not byte_equal and not isinstance(first_diff, int):
            fail(f"{producer_lane}->{consumer_lane}/{fixture} byte-different row lacks first diff")

    if seen != set(cases_by_id):
        fail(f"{producer_lane}->{consumer_lane} report is missing fixture results")

    return {
        "producerLane": producer_lane,
        "consumerLane": consumer_lane,
        "producerSdkVersion": producer_manifest["sdkVersion"],
        "producerRuntimeVersion": producer_manifest["runtimeVersion"],
        "consumerSdkVersion": consumer["sdkVersion"],
        "consumerRuntimeVersion": consumer["runtimeVersion"],
        "resultCount": len(results),
        "blockingFailures": blocking,
    }


def runtime_projection(manifest: dict[str, Any]) -> dict[str, Any]:
    return {
        "sdkVersion": manifest["sdkVersion"],
        "runtimeVersion": manifest["runtimeVersion"],
        "frameworkDescription": manifest["frameworkDescription"],
        "runtimeFamily": manifest["runtimeFamily"],
        "runtimeFamilySource": manifest["runtimeFamilySource"],
        "runtimeIdentifier": manifest["runtimeIdentifier"],
        "os": manifest["os"],
        "osVersion": manifest["osVersion"],
        "processArchitecture": manifest["processArchitecture"],
        "osArchitecture": manifest["osArchitecture"],
        "pointerSize": manifest["pointerSize"],
        "compilationMode": manifest["compilationMode"],
    }


def markdown(summary: dict[str, Any]) -> str:
    lines = [
        "# .NET 10 UnsafeBlit servicing compatibility evidence",
        "",
        f"SharpLink commit: `{summary['sharpLinkCommit']}`  ",
        f"Platform: `{summary['platformTag']}`  ",
        f"Fixture count: `{summary['fixtureCount']}`  ",
        f"Blocking failures: `{summary['blockingFailures']}`  ",
        f"Baseline SDK/runtime: `{summary['baseline']['sdkVersion']}` / `{summary['baseline']['runtimeVersion']}`  ",
        f"Latest SDK/runtime: `{summary['latest']['sdkVersion']}` / `{summary['latest']['runtimeVersion']}`",
        "",
        "| Producer lane | Consumer lane | Producer SDK | Producer runtime | Consumer SDK | Consumer runtime | Results | Blockers |",
        "|---|---|---|---|---|---|---:|---:|",
    ]
    for edge in summary["edges"]:
        lines.append(
            "|{producerLane}|{consumerLane}|{producerSdkVersion}|{producerRuntimeVersion}|"
            "{consumerSdkVersion}|{consumerRuntimeVersion}|{resultCount}|{blockingFailures}|".format(
                **edge
            )
        )
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--global-json", default=Path("global.json"), type=Path)
    args = parser.parse_args()

    manifests = {
        lane: validate_manifest(args.input / "producers" / lane / "manifest.json", lane)
        for lane in LANES
    }
    baseline = manifests["baseline"]
    latest = manifests["latest"]
    if baseline["sharpLinkCommit"] != latest["sharpLinkCommit"]:
        fail(
            "Servicing producer corpora cannot mix SharpLink commits: "
            f"baseline={baseline['sharpLinkCommit']}, latest={latest['sharpLinkCommit']}"
        )
    validate_non_servicing_identity(baseline, latest)

    global_json = load_json(args.global_json)
    configured_baseline = global_json.get("sdk", {}).get("version")
    if baseline["sdkVersion"] != configured_baseline:
        fail(
            "Baseline servicing lane must use the repository global.json SDK: "
            f"expected={configured_baseline!r}, observed={baseline['sdkVersion']!r}"
        )
    baseline_sdk = version_tuple(baseline["sdkVersion"], ".NET SDK")
    latest_sdk = version_tuple(latest["sdkVersion"], ".NET SDK")
    if baseline_sdk[0] != 10 or latest_sdk[0] != 10:
        fail(
            f"Servicing evidence must stay within .NET 10 SDKs: "
            f"baseline={baseline['sdkVersion']}, latest={latest['sdkVersion']}"
        )
    if latest_sdk < baseline_sdk:
        fail(
            f"Latest servicing SDK cannot be older than baseline: "
            f"baseline={baseline['sdkVersion']}, latest={latest['sdkVersion']}"
        )

    baseline_runtime = version_tuple(baseline["runtimeVersion"], ".NET runtime")
    latest_runtime = version_tuple(latest["runtimeVersion"], ".NET runtime")
    if baseline_runtime[0] != 10 or latest_runtime[0] != 10:
        fail(
            f"Servicing evidence must stay within .NET 10 runtimes: "
            f"baseline={baseline['runtimeVersion']}, latest={latest['runtimeVersion']}"
        )
    if latest_runtime < baseline_runtime:
        fail(
            f"Latest servicing runtime cannot be older than baseline: "
            f"baseline={baseline['runtimeVersion']}, latest={latest['runtimeVersion']}"
        )

    edges: list[dict[str, Any]] = []
    for consumer_lane in LANES:
        for producer_lane in LANES:
            report_path = (
                args.input
                / "verifications"
                / consumer_lane
                / f"from-{producer_lane}"
                / "verification.json"
            )
            edges.append(
                validate_report(
                    report_path,
                    manifests[producer_lane],
                    manifests[consumer_lane],
                    producer_lane,
                    consumer_lane,
                )
            )

    summary = {
        "schemaVersion": SCHEMA_VERSION,
        "sharpLinkCommit": baseline["sharpLinkCommit"],
        "platformTag": PLATFORM_TAG,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "fixtureCount": len(baseline["cases"]),
        "edgeCount": len(edges),
        "blockingFailures": sum(edge["blockingFailures"] for edge in edges),
        "distinctSdkVersion": baseline["sdkVersion"] != latest["sdkVersion"],
        "distinctRuntimeVersion": baseline["runtimeVersion"] != latest["runtimeVersion"],
        "baseline": runtime_projection(baseline),
        "latest": runtime_projection(latest),
        "edges": edges,
    }

    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "servicing-compatibility-summary.json").write_text(
        json.dumps(summary, indent=2) + "\n", encoding="utf-8"
    )
    (args.output / "servicing-compatibility-summary.md").write_text(
        markdown(summary), encoding="utf-8"
    )
    print(
        f"Summarized {summary['edgeCount']} servicing edges over "
        f"{summary['fixtureCount']} fixtures; blocking failures: "
        f"{summary['blockingFailures']}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
