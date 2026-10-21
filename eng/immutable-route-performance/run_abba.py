#!/usr/bin/env python3
"""Bounded actual-transport comparison; never interprets noise as equivalence."""
import argparse
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess

SOURCES = ["eng/immutable-route-performance/Program.cs", "eng/immutable-route-performance/GlobalUsings.cs",
           "eng/immutable-route-performance/ImmutableRoutePerformance.csproj",
           "test/SharpLink.UnitTests/Runtime/ImmutableRouteTransportSupport.cs"]
BINARY = "eng/immutable-route-performance/bin/Release/net10.0/SharpLink.UnitTests.dll"
METRICS = ["unary.operationsPerSecond", "unary.latencyMicroseconds.p50", "unary.latencyMicroseconds.p95",
           "unary.latencyMicroseconds.p99", "unary.processAllocatedBytesPerOperation",
           "oneWay.senderOperationsPerSecond", "oneWay.serviceExecutionsPerSecond",
           "oneWay.processAllocatedBytesPerServiceExecution"]

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args]).decode().strip()

def snapshot(root):
    untracked = git(root, "ls-files", "--others", "--exclude-standard").splitlines()
    return {"head": git(root, "rev-parse", "HEAD"), "headTree": git(root, "rev-parse", "HEAD^{tree}"),
            "status": git(root, "status", "--porcelain=v1"),
            "trackedDiffSha256": hashlib.sha256(subprocess.check_output(
                ["git", "-C", str(root), "diff", "HEAD", "--binary"])).hexdigest(),
            "untrackedFileSha256": {p: digest(root / p) for p in untracked if (root / p).is_file()},
            "harnessSourceSha256": {p: digest(root / p) for p in SOURCES},
            "binarySha256": {p.name: digest(p) for p in sorted((root / BINARY).parent.glob("*.dll"))}}

def write(path, data):
    path.write_text(json.dumps(data, indent=2, allow_nan=False) + "\n")

def require(condition, message):
    if not condition:
        raise ValueError(message)

def metric(data, name):
    value = data["measured"]
    for part in name.split("."):
        value = value[part]
    require(type(value) in (int, float) and math.isfinite(value) and value >= 0, f"Invalid metric {name}")
    return value

def validate(data, label, mode):
    require(data["schema"] == "immutable-route-transport-evidence-v1", "Wrong evidence schema")
    require(data["label"] == label and data["mode"] == mode, "Run identity mismatch")
    require(data["warmup"] == 10000 and data["totalBudgetSeconds"] == 120, "Wrong run configuration")
    require(data["measured"]["unary"]["operations"] == 10000, "Missing unary operations")
    ow = data["measured"]["oneWay"]
    require(ow["operations"] == 100000, "Missing OneWay operations")
    require(ow["finalExpectedServiceCounter"] == ow["finalObservedServiceCounter"] == 110000, "Incomplete OneWay execution")
    for name in METRICS:
        metric(data, name)
    for field in ["runtime", "buildSdk", "architecture", "os", "processorCount", "gcServer",
                  "tieredCompilation", "tieredPgo", "stopwatchFrequency", "fixture", "descriptorBytes"]:
        require(field in data and data[field] is not None, f"Missing {field}")

def summarize(output):
    manifest = json.loads((output / "manifest.json").read_text())
    require(manifest.get("completed") is True, "Run set incomplete; raw samples retained")
    groups = {"baseline": [], "candidate": []}
    configuration = None
    for entry in manifest["schedule"]:
        data = json.loads((output / entry["file"]).read_text())
        validate(data, entry["label"], entry["mode"])
        current = {k: data[k] for k in ["runtime", "buildSdk", "architecture", "os", "processorCount", "gcServer",
                    "tieredCompilation", "tieredPgo", "stopwatchFrequency", "fixture"]}
        if configuration is None:
            configuration = current
        require(current == configuration, "Runtime/config mismatch between samples")
        groups.setdefault(entry["group"], []).append(data)
    require(len(groups["baseline"]) == len(groups["candidate"]) == 8, "Expected 8 primary samples per arm")
    result = {"interpretation": "Descriptive process-run medians/ranges only. Not a confidence interval, equivalence test, or zero-overhead claim.",
              "configuration": configuration, "groups": {}, "candidateToBaselineMedianRatio": {}}
    for group, samples in groups.items():
        result["groups"][group] = {"runs": len(samples), "descriptorBytes": sorted({s["descriptorBytes"] for s in samples}), "metrics": {}}
        for name in METRICS:
            values = [metric(s, name) for s in samples]
            result["groups"][group]["metrics"][name] = {"samples": values, "median": statistics.median(values), "min": min(values), "max": max(values)}
    for name in METRICS:
        baseline = result["groups"]["baseline"]["metrics"][name]["median"]
        require(baseline > 0, f"Zero baseline metric {name}")
        result["candidateToBaselineMedianRatio"][name] = result["groups"]["candidate"]["metrics"][name]["median"] / baseline
    write(output / "summary.json", result)

def run(args):
    output = args.output.resolve()
    roots = {"baseline": args.baseline.resolve(), "candidate": args.candidate.resolve()}
    require(all(not output.is_relative_to(root) for root in roots.values()), "Evidence output must be outside both source trees")
    output.mkdir(parents=True, exist_ok=False)
    before = {key: snapshot(root) for key, root in roots.items()}
    require(before["baseline"]["head"] == "13928a35df4bff3cd99b14bd1df131aa3eb3bcdb", "Wrong baseline HEAD")
    require(before["baseline"]["trackedDiffSha256"] == hashlib.sha256(b"").hexdigest(), "Baseline tracked source modified")
    require(before["baseline"]["harnessSourceSha256"] == before["candidate"]["harnessSourceSha256"], "Harness/helper sources differ")
    environment = os.environ.copy()
    for name in environment:
        require(not any(token in name.lower() for token in ("jitdisasm", "jitdump", "enable_profiling", "startupeventpipe")), f"Remove diagnostic variable {name}")
    environment.update({"DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"})
    schedule = []
    for block in range(1, 5):
        for position, group in enumerate(("baseline", "candidate", "candidate", "baseline"), 1):
            label = f"block{block}-{position}-{group}"
            schedule.append({"group": group, "label": label, "mode": "ordinary" if group == "baseline" else "prepared", "file": label + ".json"})
    if args.candidate_ordinary:
        for index in range(1, 3):
            label = f"diagnostic-{index}-candidate-ordinary"
            schedule.append({"group": "candidateOrdinary", "label": label, "mode": "ordinary", "file": label + ".json"})
    manifest = {"schema": "immutable-route-abba-v1", "startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "completed": False, "sourceBefore": before, "dotnet": str(args.dotnet.resolve()),
                "dotnetInfo": subprocess.check_output([str(args.dotnet), "--info"], env=environment).decode(),
                "environment": {k: environment[k] for k in ("DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_gcServer", "DOTNET_GCHeapCount", "COMPlus_gcServer", "COMPlus_GCHeapCount") if k in environment}, "schedule": schedule}
    write(output / "manifest.json", manifest)
    for entry in schedule:
        root = roots["baseline" if entry["group"] == "baseline" else "candidate"]
        command = [str(args.dotnet.resolve()), str(root / BINARY), "--mode", entry["mode"], "--label", entry["label"],
                   "--warmup", "10000", "--unary", "10000", "--oneway", "100000", "--timeout-seconds", "120"]
        print(entry["label"], flush=True)
        with (output / entry["file"]).open("w") as stdout, (output / (entry["label"] + ".stderr.txt")).open("w") as stderr:
            subprocess.run(command, cwd=root, env=environment, stdout=stdout, stderr=stderr, timeout=180, check=True)
        validate(json.loads((output / entry["file"]).read_text()), entry["label"], entry["mode"])
    after = {key: snapshot(root) for key, root in roots.items()}
    manifest["sourceAfter"] = after
    require(before == after, "Source or binaries changed during comparison")
    manifest["completed"] = True
    manifest["finishedUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    write(output / "manifest.json", manifest)
    summarize(output)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    execute = commands.add_parser("run")
    for name in ("baseline", "candidate", "dotnet", "output"):
        execute.add_argument("--" + name, type=Path, required=True)
    execute.add_argument("--candidate-ordinary", action="store_true")
    summary = commands.add_parser("summary")
    summary.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "run":
        run(args)
    else:
        summarize(args.output)
