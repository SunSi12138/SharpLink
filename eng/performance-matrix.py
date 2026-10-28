#!/usr/bin/env python3
"""Plan and check full-matrix structure, never infer performance equivalence."""
import argparse
import collections
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import re
import shlex
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
PLATFORMS = {"linux": "Linux", "macos": "Darwin", "windows": "MINGW64_NT"}
TRANSPORTS = {"tcp": 0, "uds": 1, "namedpipe": 2, "anonymous": 3, "sharedmemory": 4}
PROFILES = {"balanced": 0, "lowlatency": 1, "throughput": 2}
CONFIG = {"--operation": "Operation", "--payload-size": "PayloadSize",
          "--stream-size": "StreamSize", "--warmup": "WarmupSeconds",
          "--duration": "DurationSeconds", "--min-connections": "MinConnections",
          "--max-connections": "MaxConnections", "--max-send-queue-bytes": "MaxSendQueueBytes",
          "--maximum-recorded-operations": "MaximumRecordedOperations"}


def require(ok, message):
    if not ok:
        raise ValueError(message)


def read_json(path):
    # Duplicate keys can otherwise hide a prior cell/source value.
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, f"duplicate JSON key: {key}")
            result[key] = value
        return result
    return json.loads(Path(path).read_text(), object_pairs_hook=unique,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))


def write_new(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def host_platform():
    return {"Linux": "linux", "Darwin": "macos", "Windows": "windows"}[platform.system()]


def architecture():
    return {"x86_64": "X64", "amd64": "X64", "aarch64": "Arm64", "arm64": "Arm64"}.get(
        platform.machine().lower(), platform.machine())


def source_constants(root):
    # Numeric enum serialization is part of the report contract. Fail on schema
    # drift rather than accepting an old Python mapping silently.
    def members(path):
        return re.findall(r"^\s*([A-Z][A-Za-z]+),?\s*$", (root / path).read_text(), re.M)
    modes = members("test/SharpLink.LoadTestBase/Runtime/Mode.cs")
    require(modes == ["Local", "Server", "Client", "Tcp", "Uds", "NamedPipe", "AnonymousPipe", "SharedMemory"],
            "transport/mode enum changed; review report mapping")
    require(members("src/SharpLink.Runtime/SharpLinkRuntimeOptions.cs")[:3] == ["Balanced", "LowLatency", "Throughput"],
            "profile enum changed; review report mapping")
    require(members("test/SharpLink.LoadTestBase/Metrics/LatencyRecordingMode.cs") == ["Off", "Formal", "Diagnostic", "ValidationDual"],
            "recording enum changed; review report mapping")
    text = (root / "test/SharpLink.StreamLoadTest/Program.cs").read_text()
    match = re.search(r'=> op == "all" \? \[(.*?)\] : \[op\];', text)
    require(match is not None, "ResolveOperations changed; review the plan expansion")
    operations = re.findall(r'"([a-z0-9-]+)"', match[1])
    require(operations and len(set(operations)) == len(operations), "invalid stream operations")
    recorder = (root / "test/SharpLink.LoadTestBase/Metrics/StageLatencyRecorder.cs").read_text()
    version = re.search(r'const string Version = "([^"]+)";', recorder).group(1)
    schema_text = (root / "test/SharpLink.LoadTestBase/Metrics/PerformanceReportCompatibility.cs").read_text()
    schema = int(re.search(r'CurrentSchemaVersion = (\d+);', schema_text).group(1))
    return operations, version, schema


def runner_records(root, target_platform, overrides=None):
    # The production runner itself emits the exact invocation sequence. No dotnet
    # executable or workloads are used for planning.
    env = {k: v for k, v in os.environ.items() if not k.startswith("SHARPLINK_MATRIX_")}
    env.update(SHARPLINK_MATRIX_ROOT=str(root), SHARPLINK_MATRIX_TIER="full",
               SHARPLINK_MATRIX_RUNTIMES="jit,aot", SHARPLINK_MATRIX_REPETITIONS="5",
               SHARPLINK_MATRIX_OUTPUT="/matrix-output", SHARPLINK_MATRIX_PLAN_ONLY="1",
               SHARPLINK_MATRIX_PLAN_PLATFORM=PLATFORMS[target_platform])
    env.update(overrides or {})
    result = subprocess.run(["bash", str(root / "eng/run-performance-matrix.sh")],
                            env=env, capture_output=True, check=True)
    return [block.decode().split("\0") for block in result.stdout.split(b"\0\0") if block]


def invocation(record, stream_operations):
    runtime, project, *argv = record
    require(len(argv) % 2 == 0, "runner emitted unpaired arguments")
    options = dict(zip(argv[::2], argv[1::2]))
    require(len(options) * 2 == len(argv), "runner emitted duplicate arguments")
    require(set(options) <= set(CONFIG) | {"--mode", "--transport", "--profile", "--concurrency", "--json-output", "--metrics-port"},
            "runner options changed; review report validation")
    filename = Path(options["--json-output"]).name
    workload = ("streams" if project.endswith("StreamLoadTest") else
                "oneway-backpressure" if filename.endswith("-oneway-backpressure.json") else
                "oneway" if options["--operation"] == "oneway" else
                "async" if options["--operation"] in ("yield", "delay") else "unary")
    shard = "-".join([options["--transport"], options["--profile"], runtime, workload])
    operations = stream_operations if options["--operation"] == "all" else [options["--operation"]]
    cells = [[operation, int(c)] for operation in operations for c in options["--concurrency"].split(",")]
    return shard, {"file": filename, "project": Path(project).name, "runtime": runtime,
                   "argv": argv, "cells": cells}


def make_plan(root, target_platform, source):
    require(re.fullmatch(r"[0-9a-f]{40}", source) is not None, "source must be a full commit SHA")
    operations, recorder, schema = source_constants(root)
    shards = {}
    for record in runner_records(root, target_platform):
        key, report = invocation(record, operations)
        if key not in shards:
            options = dict(zip(report["argv"][::2], report["argv"][1::2]))
            workload = key.split("-", 3)[3]
            env = {"SHARPLINK_MATRIX_TIER": "full", "SHARPLINK_MATRIX_RUNTIMES": report["runtime"],
                   "SHARPLINK_MATRIX_REPETITIONS": "5", "SHARPLINK_MATRIX_TRANSPORTS": options["--transport"],
                   "SHARPLINK_MATRIX_PROFILES": options["--profile"], "SHARPLINK_MATRIX_WORKLOADS": workload}
            shards[key] = {"id": key, "environment": env, "reports": []}
        shards[key]["reports"].append(report)
    seen = set()
    total_cells = total_seconds = 0
    for shard in shards.values():
        seconds = 0
        for report in shard["reports"]:
            options = dict(zip(report["argv"][::2], report["argv"][1::2]))
            for cell in report["cells"]:
                key = (report["file"], *cell)
                require(key not in seen, f"duplicate planned cell {key}")
                seen.add(key)
                seconds += int(options["--warmup"]) + int(options["--duration"])
        shard["cellCount"] = sum(len(report["cells"]) for report in shard["reports"])
        shard["minimumSeconds"] = seconds
        assignments = " ".join(f"{k}={shlex.quote(v)}" for k, v in shard["environment"].items())
        shard["runnerCommand"] = (f'{assignments} SHARPLINK_COMMIT={source} '
                            'SHARPLINK_MATRIX_OUTPUT="$OUT" SHARPLINK_MATRIX_PROVENANCE="$OUT/provenance.json" '
                            'bash eng/run-performance-matrix.sh')
        shard["command"] = ('python3 eng/performance-matrix.py run-shard --plan "$PLAN" --evidence "$EVIDENCE" '
                            f'--shard {shlex.quote(shard["id"])}')
        total_cells += shard["cellCount"]
        total_seconds += seconds
    return {"schemaVersion": 1, "purpose": "coverage-and-correctness-only", "sourceCommit": source,
            "platform": target_platform, "reportSchemaVersion": schema, "recorderVersion": recorder,
            "reportCount": sum(len(s["reports"]) for s in shards.values()), "cellCount": total_cells,
            "minimumSeconds": total_seconds, "shards": list(shards.values())}


def checked_plan(path, root):
    plan = read_json(path)
    require(plan == make_plan(root, plan["platform"], plan["sourceCommit"]), "plan differs from the actual full runner")
    require(git(root, "rev-parse", "HEAD") == plan["sourceCommit"], "checkout does not match planned source")
    return plan


def run_shard(args):
    root = Path(args.root).resolve()
    plan = checked_plan(args.plan, root)
    require(plan["platform"] == host_platform(), "run platform differs from plan")
    require(not git(root, "status", "--porcelain", "--untracked-files=normal"), "run requires clean source")
    shards = [s for s in plan["shards"] if s["id"] == args.shard]
    require(len(shards) == 1, "unknown shard")
    shard = shards[0]
    output = Path(args.evidence).resolve() / args.shard
    output.mkdir(parents=True, exist_ok=False)
    env = {k: v for k, v in os.environ.items() if not k.startswith("SHARPLINK_MATRIX_")}
    env.update(shard["environment"])
    env.update(SHARPLINK_COMMIT=plan["sourceCommit"], SHARPLINK_MATRIX_ROOT=str(root),
               SHARPLINK_MATRIX_OUTPUT=str(output), SHARPLINK_MATRIX_PROVENANCE=str(output / "provenance.json"))
    execution = {"schemaVersion": 1, "sourceCommit": plan["sourceCommit"], "shardId": shard["id"],
                 "startedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                 "command": ["bash", "eng/run-performance-matrix.sh"], "environment": shard["environment"]}
    with (output / "runner.log").open("w") as log:
        result = subprocess.run(execution["command"], cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
    execution.update(exitCode=result.returncode, endedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                     finalSourceCommit=git(root, "rev-parse", "HEAD"),
                     sourceDirtyAfter=bool(git(root, "status", "--porcelain", "--untracked-files=normal")))
    write_new(output / "execution.json", execution)
    return result.returncode


def capture(args):
    root, output, binary = Path(args.root).resolve(), Path(args.output).resolve(), Path(args.binary).resolve()
    source = git(root, "rev-parse", "HEAD")
    require(not git(root, "status", "--porcelain", "--untracked-files=normal"), "capture requires clean source")
    require(os.environ.get("SHARPLINK_COMMIT", source) == source, "configured source differs from checkout")
    require(binary.is_file(), f"executed binary missing: {binary}")
    env = {k: v for k, v in os.environ.items() if k.startswith("SHARPLINK_MATRIX_") and
           k not in ("SHARPLINK_MATRIX_ROOT", "SHARPLINK_MATRIX_OUTPUT", "SHARPLINK_MATRIX_PROVENANCE")}
    # Capture actual bytes once after build/publish and immediately before first
    # execution of this runtime/project. Preserve copies for offline verification.
    paths = [binary] if args.runtime == "aot" else sorted(binary.parent.glob("SharpLink*.dll"))
    require(binary in paths, "entry binary not among captured SharpLink assemblies")
    destination = output.parent / "binaries" / f"{args.runtime}-{args.project}"
    destination.mkdir(parents=True, exist_ok=False)
    artifacts = []
    for path in paths:
        copy = destination / path.name
        shutil.copyfile(path, copy)
        artifacts.append({"path": str(copy.relative_to(output.parent)), "sha256": hashlib.sha256(copy.read_bytes()).hexdigest(),
                          "entryPoint": path == binary, "originalPath": str(path)})
    cpu = platform.processor()
    if platform.system() == "Darwin":
        cpu = subprocess.check_output(["sysctl", "-n", "machdep.cpu.brand_string"], text=True).strip()
    elif platform.system() == "Linux":
        cpu_text = Path("/proc/cpuinfo").read_text()
        match = re.search(r"^(?:model name|Hardware)\s*:\s*(.+)$", cpu_text, re.M)
        cpu = match[1] if match else platform.machine()
    data = read_json(output) if output.exists() else {
        "schemaVersion": 1, "sourceCommit": source, "sourceDirty": False,
        "platform": host_platform(), "architecture": architecture(), "cpu": cpu,
        "os": platform.platform(), "sdkVersion": subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip(),
        "toolchain": subprocess.check_output(["dotnet", "--info"], cwd=root, text=True),
        "runtimeInventory": subprocess.check_output(["dotnet", "--list-runtimes"], cwd=root, text=True),
        "environment": env, "capturedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "binaries": []}
    require(data["sourceCommit"] == source and data["environment"] == env, "provenance changed within shard")
    require(not any(x["runtime"] == args.runtime and x["project"] == args.project for x in data["binaries"]), "duplicate binary capture")
    data["binaries"].append({"runtime": args.runtime, "project": args.project, "artifacts": artifacts})
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(data, indent=2) + "\n")


def number(value, label, positive=False):
    require(type(value) in (int, float) and math.isfinite(value) and value >= 0 and
            (not positive or value > 0), f"invalid {label}: {value}")
    return value


def validate_result(result, plan):
    require(result["RecorderMode"] == "formal" and result["FormalComparable"] is True, "not formal-comparable")
    require(result["RecorderVersion"] == plan["recorderVersion"], "recorder version mismatch")
    for field in ("Failure", "ValidationFailure", "Cancelled", "TailObserverFailure"):
        require(type(result.get(field, 0)) is int and result.get(field, 0) == 0, f"nonzero/invalid {field}; no failure waiver")
    counts = [result[field] for field in ("OperationsStartedDuringMeasurement", "OperationsCompleted", "Success", "SampleCount")]
    require(all(type(n) is int and n > 0 for n in counts) and len(set(counts)) == 1, "incomplete operation/sample counts")
    require(type(result["MaximumSampleCapacity"]) is int and result["MaximumSampleCapacity"] >= counts[0], "sample capacity exceeded")
    for field in ("WorkerCount", "StopwatchFrequency", "MeasurementDurationSeconds"):
        number(result[field], field, positive=True)
    for field in ("WarmupDurationSeconds", "DrainDurationSeconds", "P50Us", "P95Us", "P99Us", "P999Us"):
        number(result[field], field)


def validate_report(data, expected, plan, provenance):
    require(data["SchemaVersion"] == plan["reportSchemaVersion"], "report schema mismatch")
    require(data["SourceCommit"] == plan["sourceCommit"], "report source mismatch")
    require(data["Workload"] == expected["project"], "wrong report workload")
    require(data["ProcessArchitecture"] == provenance["architecture"], "report architecture mismatch")
    for field in ("Runtime", "OperatingSystem", "OsArchitecture", "TimestampUtc"):
        require(isinstance(data[field], str) and data[field].strip() not in ("", "unknown"), f"missing {field}")
    os_markers = {"linux": ("linux",), "macos": ("darwin", "macos", "mac os"), "windows": ("windows",)}
    require(any(marker in data["OperatingSystem"].lower() for marker in os_markers[plan["platform"]]), "report OS mismatch")
    match = re.fullmatch(r"\.NET ([0-9]+\.[0-9]+\.[0-9]+(?:[-+][^ ]+)?)", data["Runtime"])
    require(match is not None and f"Microsoft.NETCore.App {match[1]} [" in provenance["runtimeInventory"],
            "report runtime absent from captured toolchain; inspect actual publish/runtime identity")
    number(data["ProcessorCount"], "processor count", positive=True)
    options = dict(zip(expected["argv"][::2], expected["argv"][1::2]))
    config = data["Configuration"]
    for key, field in CONFIG.items():
        value = options.get(key)
        if value is not None:
            expected_value = value if key == "--operation" else int(value)
            require(config[field] == expected_value, f"configuration {field} mismatch")
    require(config["Mode"] == 0 and config["Transport"] == TRANSPORTS[options["--transport"]], "mode/transport mismatch")
    require(config["PerformanceProfile"] == PROFILES[options["--profile"]], "profile mismatch")
    require(config["ConcurrencyConfig"] == [int(c) for c in options["--concurrency"].split(",")], "configured concurrency mismatch")
    if "--max-send-queue-bytes" not in options:
        require(config.get("MaxSendQueueBytes") is None, "backpressure must use profile-default queue")
    require(config["RecordingMode"] == 1, "configuration is not formal recording")
    results = data["Results"]
    require(isinstance(results, list), "results must be an array")
    actual = [(r["Operation"], r["Concurrency"]) for r in results]
    require(collections.Counter(actual) == collections.Counter(map(tuple, expected["cells"])), "missing, extra or duplicate result cell")
    for result in results:
        validate_result(result, plan)
        require(result["WorkerCount"] == result["Concurrency"], "worker/concurrency mismatch")
        require(result["MaximumSampleCapacity"] == int(options["--maximum-recorded-operations"]), "recorder capacity differs from plan")
        require(result["MeasurementDurationSeconds"] >= int(options["--duration"]) and
                result["WarmupDurationSeconds"] >= int(options["--warmup"]), "partial measurement/warmup duration")


def validate_provenance(provenance, shard, plan, directory):
    require(provenance["schemaVersion"] == 1 and provenance["sourceDirty"] is False, "invalid/dirty provenance")
    require(provenance["sourceCommit"] == plan["sourceCommit"], "provenance source mismatch")
    require(provenance["platform"] == plan["platform"], "provenance platform mismatch")
    require(provenance["environment"] == shard["environment"], "provenance invocation environment mismatch")
    for field in ("sdkVersion", "toolchain", "runtimeInventory", "cpu", "os", "architecture", "capturedAtUtc"):
        require(isinstance(provenance[field], str) and provenance[field].strip() not in ("", "unknown"), f"missing provenance {field}")
    expected = {(r["runtime"], r["project"]) for r in shard["reports"]}
    binaries = provenance["binaries"]
    require(collections.Counter((b["runtime"], b["project"]) for b in binaries) == collections.Counter(expected), "binary runtime/project identity mismatch")
    for binary in binaries:
        artifacts = binary["artifacts"]
        require(artifacts and sum(a["entryPoint"] is True for a in artifacts) == 1, "missing/duplicate entry binary")
        require(len({a["path"] for a in artifacts}) == len(artifacts), "duplicate binary artifact")
        for artifact in artifacts:
            path = (directory / artifact["path"]).resolve()
            require(path.is_relative_to((directory / "binaries").resolve()) and path.is_file(), "missing/outside binary artifact")
            require(re.fullmatch(r"[0-9a-f]{64}", artifact["sha256"]) is not None and
                    hashlib.sha256(path.read_bytes()).hexdigest() == artifact["sha256"], "binary integrity mismatch")
            require(bool(artifact["originalPath"]), "missing actual binary input path")
            if artifact["entryPoint"] and binary["runtime"] == "jit":
                require(path.name == binary["project"] + ".dll", "wrong JIT entry binary")
            if artifact["entryPoint"] and binary["runtime"] == "aot":
                suffix = ".exe" if plan["platform"] == "windows" else ""
                require(path.name == binary["project"] + suffix, "wrong AOT entry binary")


def validate(plan, evidence, root, selected=None):
    # Regeneration rejects hand-edited/shrunken plans, including "full" plans with
    # fewer repetitions or missing shards. Same runner/source is required.
    require(plan == make_plan(root, plan["platform"], plan["sourceCommit"]), "plan differs from the actual full runner")
    require(git(root, "rev-parse", "HEAD") == plan["sourceCommit"], "checkout does not match planned source")
    shards = plan["shards"]
    if selected:
        shards = [s for s in shards if s["id"] == selected]
        require(len(shards) == 1, "unknown shard")
    errors, reports, cells = [], 0, 0
    evidence = Path(evidence).resolve()
    expected_dirs = {s["id"] for s in shards}
    if not selected and evidence.exists():
        extra = {p.name for p in evidence.iterdir() if p.is_dir()} - expected_dirs
        if extra:
            errors.append(f"unexpected shard directories: {sorted(extra)}")
    for shard in shards:
        directory = evidence / shard["id"]
        try:
            execution = read_json(directory / "execution.json")
            require(execution["schemaVersion"] == 1 and execution["exitCode"] == 0 and
                    execution["sourceDirtyAfter"] is False, "execution failed, partial or source dirty")
            require(execution["sourceCommit"] == plan["sourceCommit"] == execution["finalSourceCommit"], "execution source mismatch")
            require(execution["shardId"] == shard["id"] and execution["environment"] == shard["environment"] and
                    execution["command"] == ["bash", "eng/run-performance-matrix.sh"], "execution invocation mismatch")
            require(execution["startedAtUtc"] and execution["endedAtUtc"], "missing execution times")
            provenance = read_json(directory / "provenance.json")
            validate_provenance(provenance, shard, plan, directory)
            expected_files = {r["file"] for r in shard["reports"]}
            actual_files = {p.name for p in directory.glob("*.json")} - {"provenance.json", "execution.json"}
            require(actual_files == expected_files, f"report inventory differs: missing={sorted(expected_files-actual_files)}, extra={sorted(actual_files-expected_files)}")
            for report in shard["reports"]:
                try:
                    validate_report(read_json(directory / report["file"]), report, plan, provenance)
                    reports += 1
                    cells += len(report["cells"])
                except (KeyError, TypeError, ValueError, OSError) as exc:
                    errors.append(f"{shard['id']}/{report['file']}: {exc}")
        except (KeyError, TypeError, ValueError, OSError) as exc:
            errors.append(f"{shard['id']}: {exc}")
    return {"schemaVersion": 1, "sourceCommit": plan["sourceCommit"], "scope": "single-shard" if selected else "full",
            "coverageAndCorrectnessPassed": not errors, "performanceDecision": "not-assessed",
            "acceptedReports": reports, "acceptedCells": cells, "failures": errors}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    plan_parser = sub.add_parser("plan", help="no-load enumeration from the real shell runner")
    plan_parser.add_argument("--platform", choices=PLATFORMS, default=host_platform())
    plan_parser.add_argument("--output", required=True)
    plan_parser.add_argument("--root", type=Path, default=ROOT)
    check = sub.add_parser("validate", help="offline correctness/coverage; no performance verdict")
    check.add_argument("--plan", required=True)
    check.add_argument("--evidence", required=True)
    check.add_argument("--output", required=True)
    check.add_argument("--shard")
    check.add_argument("--root", type=Path, default=ROOT)
    run = sub.add_parser("run-shard", help="execute one planned shard in a fresh directory; this runs real load")
    run.add_argument("--plan", required=True)
    run.add_argument("--evidence", required=True)
    run.add_argument("--shard", required=True)
    run.add_argument("--root", type=Path, default=ROOT)
    cap = sub.add_parser("capture", help="runner-internal: capture built binary before execution")
    for name in ("output", "runtime", "project", "binary", "root"):
        cap.add_argument("--" + name, required=True)
    args = parser.parse_args()
    try:
        if args.command == "plan":
            require(not git(args.root, "status", "--porcelain", "--untracked-files=normal"), "plan requires clean committed source")
            value = make_plan(args.root, args.platform, git(args.root, "rev-parse", "HEAD"))
            write_new(args.output, value)
            print(f"{len(value['shards'])} shards; {value['reportCount']} reports; {value['cellCount']} cells; {value['minimumSeconds']/3600:.3f} timing hours")
        elif args.command == "run-shard":
            return run_shard(args)
        elif args.command == "capture":
            capture(args)
        else:
            result = validate(read_json(args.plan), args.evidence, args.root, args.shard)
            write_new(args.output, result)
            print(json.dumps(result, indent=2))
            return 0 if result["coverageAndCorrectnessPassed"] else 1
    except (KeyError, TypeError, ValueError, OSError, subprocess.CalledProcessError) as exc:
        print(f"performance matrix: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
