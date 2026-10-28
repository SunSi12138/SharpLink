#!/usr/bin/env python3
"""Manual hosted scheduling/assembly for structural evidence, NOT performance acceptance."""
import argparse
import datetime
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("performance_matrix", ROOT / "eng/performance-matrix.py")
matrix = importlib.util.module_from_spec(spec)
spec.loader.exec_module(matrix)
PLATFORMS = ("linux", "macos", "windows")
MODES = ("plan-only", "selected-shards", "full-structural")
CORE_FILES = ("eng/performance-matrix.py", "eng/run-performance-matrix.sh")


def sha(value):
    matrix.require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{40}", value), "expected full lowercase commit SHA")
    return value


def identity(root, expected):
    matrix.require(matrix.git(root, "rev-parse", "HEAD") == sha(expected), "checkout SHA mismatch")
    matrix.require(not matrix.git(root, "status", "--porcelain", "--untracked-files=normal"), "checkout must be clean")


def compatible(root):
    # run-shard invokes the candidate's shell and its capture hook. Do not let
    # those silently differ from the independently reviewed tool checkout.
    for name in CORE_FILES:
        matrix.require((root / name).read_bytes() == (ROOT / name).read_bytes(),
                       f"candidate/tool contract differs: {name}; review compatibility before scheduling")


def selection(plans, mode, requested, resources_confirmed):
    matrix.require(mode in MODES, "unknown mode")
    available = {f"{p}:{s['id']}" for p in PLATFORMS for s in plans[p]["shards"]}
    selected = [s.strip() for s in requested.split(",") if s.strip()]
    matrix.require(len(selected) == len(set(selected)), "duplicate selected shard")
    matrix.require(set(selected) <= available, "unknown platform/shard selection")
    if mode == "plan-only":
        matrix.require(not selected, "plan-only does not accept selected shards")
        return []
    matrix.require(resources_confirmed, "execution requires explicit resource confirmation")
    if mode == "full-structural":
        matrix.require(not selected, "full mode cannot shorten the plan")
        selected = sorted(available)
    else:
        matrix.require(1 <= len(selected) <= 6, "selected-shards requires 1..6 explicit platform:shard IDs")
    return sorted(selected)


def schedule(plans, mode, requested, resources_confirmed, baseline, tool, run_id, attempt, parallel):
    if baseline:
        sha(baseline)
    sha(tool)
    matrix.require(re.fullmatch(r"[1-9][0-9]*", run_id) and re.fullmatch(r"[1-9][0-9]*", attempt), "invalid run identity")
    matrix.require(type(parallel) is int and 1 <= parallel <= 4, "parallelism must be 1..4 per platform")
    matrix.require(type(resources_confirmed) is bool, "resource confirmation must be boolean")
    matrix.require(len({plans[p]["sourceCommit"] for p in PLATFORMS}) == 1, "platform source mismatch")
    selected = selection(plans, mode, requested, resources_confirmed)
    return {"schemaVersion": 1, "purpose": "coverage-and-correctness-only", "performanceDecision": "not-assessed",
            "candidateCommit": plans["linux"]["sourceCommit"], "baselineCommit": baseline or None,
            "baselineRole": "reference-only-not-executed-or-compared", "toolCommit": tool,
            "runId": run_id, "runAttempt": attempt, "mode": mode,
            "resourcesConfirmed": resources_confirmed, "maxParallelPerPlatform": parallel,
            "timeoutMinutes": 240, "selected": selected,
            "platformTotals": {p: {"shards": len(plans[p]["shards"]), "reports": plans[p]["reportCount"],
                                   "cells": plans[p]["cellCount"], "minimumSeconds": plans[p]["minimumSeconds"]}
                               for p in PLATFORMS}}


def create(args):
    identity(ROOT, args.tool_sha)
    identity(args.root, args.candidate_sha)
    compatible(args.root)
    plans = {p: matrix.make_plan(args.root, p, args.candidate_sha) for p in PLATFORMS}
    manifest = schedule(plans, args.mode, args.shards, args.resources_confirmed, args.baseline_sha,
                        args.tool_sha, args.run_id, args.run_attempt, args.max_parallel)
    args.campaign.mkdir(parents=True, exist_ok=False)
    for p, plan in plans.items():
        matrix.write_new(args.campaign / f"{p}.json", plan)
    matrix.write_new(args.campaign / "campaign.json", manifest)
    if args.github_output:
        with args.github_output.open("a") as output:
            for p in PLATFORMS:
                rows = [{"shard": item.split(":", 1)[1]} for item in manifest["selected"] if item.startswith(p + ":")]
                matrix.require(len(rows) <= 256, "platform matrix exceeds GitHub limit")
                output.write(f"{p}={json.dumps({'include': rows}, separators=(',', ':'))}\n")
                output.write(f"{p}_count={len(rows)}\n")
    print(json.dumps(manifest, indent=2))


def checked(args):
    manifest = matrix.read_json(args.campaign / "campaign.json")
    matrix.require(manifest["toolCommit"] == args.tool_sha and manifest["runId"] == args.run_id and
                   manifest["runAttempt"] == args.run_attempt, "campaign/tool/run/attempt mismatch; do not mix reruns")
    identity(ROOT, args.tool_sha)
    identity(args.root, manifest["candidateCommit"])
    compatible(args.root)
    plans = {p: matrix.checked_plan(args.campaign / f"{p}.json", args.root) for p in PLATFORMS}
    # Regenerate scheduling metadata too, not just the workload plans.
    requested = ",".join(manifest["selected"]) if manifest["mode"] == "selected-shards" else ""
    expected = schedule(plans, manifest["mode"], requested, manifest["resourcesConfirmed"],
                        manifest["baselineCommit"], args.tool_sha, args.run_id, args.run_attempt,
                        manifest["maxParallelPerPlatform"])
    matrix.require(manifest == expected, "campaign metadata differs from regenerated schedule")
    return manifest, plans


def job_identity(manifest, target, shard):
    matrix.require(f"{target}:{shard}" in manifest["selected"], "job not selected by campaign")
    return {key: manifest[key] for key in ("candidateCommit", "baselineCommit", "toolCommit", "runId", "runAttempt")} | {
        "platform": target, "shard": shard}


def receipt_path(args, suffix):
    return args.output / "jobs" / args.platform / f"{args.shard}.{suffix}.json"


def prepare(args):
    manifest, _ = checked(args)
    expected = job_identity(manifest, args.platform, args.shard)
    matrix.require(matrix.host_platform() == args.platform, "runner platform does not match job")
    matrix.write_new(receipt_path(args, "start"), {"identity": expected,
        "startedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "runnerName": os.environ.get("RUNNER_NAME", "local"),
        "runnerArch": os.environ.get("RUNNER_ARCH", matrix.architecture())})


def execute(args):
    manifest, plans = checked(args)
    expected = job_identity(manifest, args.platform, args.shard)
    start = matrix.read_json(receipt_path(args, "start"))
    matrix.require(start["identity"] == expected, "job receipt mismatch")
    # New receipt and new shard directory are both required. Existing evidence
    # cannot be overwritten or reused by rerun-failed-jobs.
    matrix.require(not receipt_path(args, "finish").exists(), "job already finished")
    exit_code = 2
    error = None
    try:
        exit_code = matrix.run_shard(SimpleNamespace(root=args.root,
            plan=args.campaign / f"{args.platform}.json", evidence=args.output / "evidence" / args.platform,
            shard=args.shard))
    except (ValueError, OSError) as exc:
        error = str(exc)
    result = matrix.validate(plans[args.platform], args.output / "evidence" / args.platform, args.root, args.shard)
    matrix.write_new(args.output / "checks" / args.platform / f"{args.shard}.json", result)
    matrix.write_new(receipt_path(args, "finish"), {"identity": expected, "runExitCode": exit_code,
        "error": error, "endedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "coverageAndCorrectnessPassed": result["coverageAndCorrectnessPassed"]})
    return 0 if exit_code == 0 and result["coverageAndCorrectnessPassed"] else 1


def aggregate(args):
    manifest, plans = checked(args)
    matrix.require(manifest["mode"] != "plan-only", "plan-only is not execution evidence")
    failures, results = [], []
    for item in manifest["selected"]:
        target, shard = item.split(":", 1)
        expected = job_identity(manifest, target, shard)
        try:
            start = matrix.read_json(args.output / "jobs" / target / f"{shard}.start.json")
            finish = matrix.read_json(args.output / "jobs" / target / f"{shard}.finish.json")
            matrix.require(start["identity"] == expected == finish["identity"], "job receipt identity mismatch")
            matrix.require(start["startedAtUtc"] and finish["endedAtUtc"] and finish["runExitCode"] == 0 and
                           finish["coverageAndCorrectnessPassed"] is True and finish["error"] is None,
                           "failed/incomplete job receipt")
        except (KeyError, TypeError, ValueError, OSError) as exc:
            failures.append(f"{item}: {exc}")
    for target in PLATFORMS:
        evidence = args.output / "evidence" / target
        selected = [item.split(":", 1)[1] for item in manifest["selected"] if item.startswith(target + ":")]
        actual = {p.name for p in evidence.iterdir()} if evidence.exists() else set()
        if actual != set(selected):
            failures.append(f"{target}: unexpected/missing shard directories: {sorted(actual ^ set(selected))}")
        if manifest["mode"] == "full-structural":
            results.append(dict(platform=target, **matrix.validate(plans[target], evidence, args.root)))
        else:
            for shard in selected:
                results.append(dict(platform=target, shard=shard, **matrix.validate(plans[target], evidence, args.root, shard)))
    passed = not failures and all(r["coverageAndCorrectnessPassed"] for r in results)
    result = {"schemaVersion": 1, "scope": manifest["mode"], "candidateCommit": manifest["candidateCommit"],
        "toolCommit": manifest["toolCommit"], "runId": manifest["runId"], "runAttempt": manifest["runAttempt"],
        "performanceDecision": "not-assessed", "selectedCoverageAndCorrectnessPassed": passed,
        "fullThreePlatformCoverageAndCorrectnessPassed": passed and manifest["mode"] == "full-structural",
        "failures": failures, "results": results}
    matrix.write_new(args.summary, result)
    print(json.dumps(result, indent=2))
    return 0 if passed else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for command in ("plan", "prepare-job", "run-job", "aggregate"):
        child = sub.add_parser(command)
        child.add_argument("--root", type=Path, required=True)
        child.add_argument("--campaign", type=Path, required=True)
        child.add_argument("--tool-sha", required=True)
        child.add_argument("--run-id", required=True)
        child.add_argument("--run-attempt", required=True)
        if command == "plan":
            child.add_argument("--candidate-sha", required=True)
            child.add_argument("--baseline-sha", default="")
            child.add_argument("--mode", choices=MODES, default="plan-only")
            child.add_argument("--shards", default="")
            child.add_argument("--resources-confirmed", action="store_true")
            child.add_argument("--max-parallel", type=int, default=1)
            child.add_argument("--github-output", type=Path)
        else:
            child.add_argument("--output", type=Path, required=True)
            if command == "aggregate":
                child.add_argument("--summary", type=Path, required=True)
            else:
                child.add_argument("--platform", choices=PLATFORMS, required=True)
                child.add_argument("--shard", required=True)
    args = parser.parse_args()
    args.root = args.root.resolve()
    args.campaign = args.campaign.resolve()
    try:
        return {"plan": create, "prepare-job": prepare, "run-job": execute, "aggregate": aggregate}[args.command](args) or 0
    except (KeyError, TypeError, ValueError, OSError, matrix.subprocess.CalledProcessError) as exc:
        print(f"performance scheduler: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
