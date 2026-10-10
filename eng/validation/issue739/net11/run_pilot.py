#!/usr/bin/env python3
"""Bounded .NET 11 lowering studies; pilot or preregistered repeated stages. No NativeAOT claim."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import re
import shutil
import statistics
import subprocess
import time

SOURCE = "eb99fe887cf2129d9b88441245ca0a4a6406b6c2"
SDK = "11.0.100-rc.1.26425.128"
RUNTIME = "11.0.0-rc.1.26425.128"
ENVIRONMENT_VERSION = "11.0.0"
PRODUCTION_PROJECTS = ["SharpLink.Abstractions", "SharpLink.Runtime", "SharpLink.Client",
                       "SharpLink.Server", "SharpLink.Sdk", "SharpLink.Serializer.SharpPack"]
CALIBRATIONS = ["logical-incomplete-wrapper", "logical-completion-control", "logical-completed-wrapper",
                "permit-plain", "permit-capacity-control", "context-null-transition", "context-null-control",
                "context-same-snapshot", "context-flow-transition", "context-flow-null-control"]
PINNED_ENV = {"DOTNET_TieredPGO": "1", "DOTNET_TieredCompilation": "1", "DOTNET_ReadyToRun": "1",
              "DOTNET_gcServer": "0", "COMPlus_gcServer": "0", "DOTNET_ROLL_FORWARD": "Disable",
              "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
              "ISSUE739_SOURCE_SHA": SOURCE}
READER_CASES = ["reader-wrapped-sync", "reader-direct-sync", "reader-wrapped-incomplete", "reader-direct-incomplete",
                "reader-wrapped-incomplete-burst32", "reader-direct-incomplete-burst32"]
TRADITIONAL_PROJECTS = ["Driver", "Calibration", "ReaderControl", "Metadata", "SharpLink.Generator"]
CREDENTIAL_ENVIRONMENT_NAMES = {"GITHUB_TOKEN", "GH_TOKEN", "ACTIONS_RUNTIME_TOKEN", "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
                                "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS", "NUGET_AUTH_TOKEN", "SYSTEM_ACCESSTOKEN"}


def sha(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def tracked_inputs(root):
    names = subprocess.check_output(["git", "ls-files", "-z", "src"], cwd=root).decode().split("\0")
    names += ["Directory.Build.props", "Directory.Packages.props", "global.json", ".editorconfig"]
    for extra in ("Directory.Build.targets", "NuGet.Config", "nuget.config", ".globalconfig"):
        if (root / extra).is_file():
            names.append(extra)
    return sorted(set(name for name in names if name))


def assert_original(root):
    subprocess.run(["git", "diff", "--exit-code", SOURCE, "--", "src", "Directory.Build.props",
                    "Directory.Packages.props", "global.json"], cwd=root, check=True, stdout=subprocess.PIPE)
    require(not subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "src"], cwd=root).strip(),
            "Untracked production source files")


def make_copy(root, here, target, variant, names):
    target.mkdir(parents=True)
    for name in names:
        destination = target / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(root / name, destination)
    relative = here.relative_to(root)
    for source in here.rglob("*"):
        if source.is_file() and not {"bin", "obj", "__pycache__"}.intersection(source.relative_to(here).parts):
            destination = target / relative / source.relative_to(here)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
    # Reuse the reviewed calibration source and project byte-for-byte before the TFM overlay.
    for name in ("Program.cs", "Calibration.csproj"):
        destination = target / relative.parent / "calibration" / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(here.parent / "calibration" / name, destination)
    overlay = {}
    for path in [target / "Directory.Build.props", *sorted((target / "src").rglob("*.csproj"))]:
        old = path.read_text(encoding="utf-8-sig")
        new = old.replace("<TargetFramework>net10.0</TargetFramework>", "<TargetFramework>net11.0</TargetFramework>")
        # Root props has a conditional element; leave the netstandard generator unchanged.
        new = new.replace(">net10.0</TargetFramework>", ">net11.0</TargetFramework>")
        if path.name == "Directory.Build.props" and path.parent == target:
            properties = '''  <PropertyGroup>
    <LangVersion>preview</LangVersion>
    <PathMap>$(MSBuildThisFileDirectory)=/_/</PathMap>
    <UseSharedCompilation>false</UseSharedCompilation>
    <UseRuntimeAsync>false</UseRuntimeAsync>
    <!-- Keep original explicit package references in both projections; SDK11
         pruning otherwise raises NU1510 for existing framework-provided refs. -->
    <RestoreEnablePackagePruning>false</RestoreEnablePackagePruning>
  </PropertyGroup>
'''
            new = new.replace("</Project>", properties + "</Project>")
        if old != new:
            path.write_text(new)
            overlay[str(path.relative_to(target))] = {"originalSha256": sha(root / path.relative_to(target)), "projectedSha256": sha(path)}
    global_path = target / "global.json"
    original_global = sha(global_path)
    write_json(global_path, {"sdk": {"version": SDK, "rollForward": "disable", "allowPrerelease": True}})
    overlay["global.json"] = {"originalSha256": original_global, "projectedSha256": sha(global_path)}
    # MSBuild's project-name allowlist is explicit: no measurement-driver lowering toggle.
    toggled = " Or ".join("'$(MSBuildProjectName)' == '" + name + "'" for name in PRODUCTION_PROJECTS + ["Fixture"])
    feature = "on" if variant == "B" else "off"
    targets = target / "Directory.Build.targets"
    require(not targets.exists(), "Unexpected existing Directory.Build.targets; overlay needs explicit review")
    targets.write_text(f'''<Project>
  <PropertyGroup>
    <Issue739Lowering>off</Issue739Lowering>
    <Issue739Lowering Condition="{toggled}">{feature}</Issue739Lowering>
    <UseRuntimeAsync Condition="'$(Issue739Lowering)' == 'on'">true</UseRuntimeAsync>
    <UseRuntimeAsync Condition="'$(Issue739Lowering)' == 'off'">false</UseRuntimeAsync>
  </PropertyGroup>
  <Target Name="Issue739CompilerLowering" BeforeTargets="CoreCompile">
    <PropertyGroup><Features>runtime-async=$(Issue739Lowering)</Features></PropertyGroup>
    <Message Importance="High" Text="ISSUE739_COMPILER $(MSBuildProjectName) TargetFramework=$(TargetFramework) UseRuntimeAsync=$(UseRuntimeAsync) Features=$(Features)" />
  </Target>
</Project>
''')
    overlay["Directory.Build.targets"] = {"projectedSha256": sha(targets), "variant": variant}
    return overlay


def compiler_commands(build_log):
    # SDKs can launch either the csc native apphost or dotnet exec csc.dll.
    return [line for line in build_log.splitlines()
            if re.search(r"[\\/]csc(?:\.dll|\.exe)?[\"\s]", line) and "/features:" in line]


def binary_manifest(directory):
    return {str(path.relative_to(directory)): sha(path) for path in sorted(directory.rglob("*")) if path.is_file()}


def package_manifest(root):
    result = {}
    for path in sorted(root.rglob("project.assets.json")):
        content = json.loads(path.read_text())
        result[str(path.relative_to(root))] = {
            key: {field: value.get(field) for field in ("type", "sha512", "path")}
            for key, value in content["libraries"].items() if value.get("type") == "package"}
    return result


def method_key(method):
    return (method["type"], method["name"], method["genericArity"], method["semanticSignature"])


def traditional(method):
    return not method["runtimeAsync"] and method["stateMachineType"] is not None and method["hasMoveNext"]


def verify_metadata(a, b):
    def assembly(report, suffix):
        rows = [x for x in report["assemblies"] if Path(x["path"]).name == suffix]
        require(len(rows) == 1, "Assembly ambiguity " + suffix)
        return rows[0]
    def method(report, suffix, type_name, name, arity):
        rows = [x for x in assembly(report, suffix)["methods"]
                if x["type"] == type_name and x["name"] == name and x["genericArity"] == arity]
        require(len(rows) == 1, "Method ambiguity " + type_name + "." + name)
        return rows[0]
    for filename in [name + ".dll" for name in PRODUCTION_PROJECTS + ["Fixture", "SharpLink.Benchmarks"]]:
        left, right = assembly(a, filename), assembly(b, filename)
        require(left["identity"] == right["identity"], "Assembly identity differs: " + filename)
        require(not any(x["runtimeAsync"] for x in left["methods"]), "A assembly unexpectedly contains runtime-async: " + filename)
        # Compiler-created state-machine types disappear intentionally; original methods keep signatures.
        left_signatures = {method_key(x) for x in left["methods"] if "<" not in x["type"] and "<" not in x["name"]}
        right_signatures = {method_key(x) for x in right["methods"] if "<" not in x["type"] and "<" not in x["name"]}
        require(left_signatures == right_signatures, "Original method signatures differ: " + filename)
    for report in (a, b):
        require(report["runtime"] == ENVIRONMENT_VERSION and RUNTIME in report["framework"], "Inspector runtime identity mismatch")
        require(Path(report["corelibPath"]).parent.name == RUNTIME, "Inspector did not load the exact shared framework")
    logical_a = method(a, "SharpLink.Client.dll", "SharpLink.Client.SharpLinkClient", "AwaitLogicalInvocationAsync", 1)
    logical_b = method(b, "SharpLink.Client.dll", "SharpLink.Client.SharpLinkClient", "AwaitLogicalInvocationAsync", 1)
    require(traditional(logical_a), "A logical helper is not proven traditional")
    require(logical_b["runtimeAsync"] and logical_b["stateMachineType"] is None, "B logical helper is not proven runtime-async")
    readers = [method(report, "SharpLink.Runtime.dll", "SharpLink.Runtime.ReadOwnershipPipeReader", "AwaitReadAsync", 0) for report in (a, b)]
    require(all(x["builderType"] and "PoolingAsyncValueTaskMethodBuilder" in x["builderType"] for x in readers),
            "Pooled reader lost its source custom-builder attribute")
    require(traditional(readers[0]), "A pooled reader is not proven traditional")
    require(readers[1]["runtimeAsync"] and readers[1]["stateMachineType"] is None and not readers[1]["hasMoveNext"],
            "B pooled reader does not match the observed exact-RC1 runtime-async lowering")
    require(method_key(readers[0]) == method_key(readers[1]) and readers[0]["builderType"] == readers[1]["builderType"],
            "Pooled reader semantic signature/builder differs")
    driver = []
    for report in (a, b):
        item = assembly(report, "SharpLink.Benchmarks.dll")
        require(not any(x["runtimeAsync"] for x in item["methods"]), "Driver contains runtime async")
        async_methods = [x for x in item["methods"] if x["stateMachineType"] is not None]
        require(async_methods and all(traditional(x) for x in async_methods), "Driver lowering not proven traditional")
        driver.append(item["sha256"])
    require(driver[0] == driver[1], "Measurement driver assembly differs")
    fixture_a = assembly(a, "Fixture.dll")
    fixture_b = assembly(b, "Fixture.dll")
    eligible = [x for x in fixture_a["methods"] if traditional(x) and x["builderType"] is None]
    lookup = {method_key(x): x for x in fixture_b["methods"]}
    require(len(lookup) == len(fixture_b["methods"]), "Fixture semantic signature ambiguity")
    for item in eligible:
        candidate = lookup.get(method_key(item))
        require(candidate is not None and candidate["runtimeAsync"] and candidate["stateMachineType"] is None,
                "Generated fixture helper did not change lowering: " + str(method_key(item)))
    return {"logicalHelperA": logical_a, "logicalHelperB": logical_b, "pooledReaderA": readers[0], "pooledReaderB": readers[1],
            "pooledReaderInterpretation": "exact RC1 keeps AsyncMethodBuilder attribute but lowers this method to runtime-async in B; no automatic pooled opt-out asserted; source unchanged",
            "driverSha256": driver[0], "fixtureEligibleMethods": eligible,
            "fixtureLoweringStatus": "verified" if eligible else "N/A: no eligible async generated method in fixed Add-only fixture",
            "bcl": "same pinned framework, already compiled with runtime async; compiler switch does not retoggle BCL"}


def validate_sample(row, category, case, sample, operations, warmup, expected_hash):
    require(row["schemaVersion"] == 1 and row["sourceSha"] == SOURCE and row["sample"] == sample, "Sample identity mismatch")
    require(row["runtime"] == ENVIRONMENT_VERSION and RUNTIME in row["framework"], "Sample runtime mismatch")
    require(all(type(row[name]) is int for name in ("operations", "warmup", "checks")), "Nonintegral counters")
    require(row["operations"] == operations and row["warmup"] == warmup and row["checks"] == operations, "Exact count mismatch")
    require(type(row["processId"]) is int and row["processId"] > 0, "Invalid PID")
    require(row["precise"] is True and row["driverIncluded"] is True and row["subtractionApplied"] is False, "Measurement mode mismatch")
    require(row["executableSha256"] == expected_hash, "Executed driver hash mismatch")
    require(row["architecture"] == "X64" and row["serverGc"] is False, "Runtime architecture/GC mismatch")
    require(type(row["bytes"]) is int and row["bytes"] >= 0, "Invalid allocation")
    require(math.isfinite(row["bytesPerOperation"]) and row["bytesPerOperation"] == row["bytes"] / operations, "Allocation denominator mismatch")
    require(math.isfinite(row["elapsedSeconds"]) and row["elapsedSeconds"] > 0, "Invalid elapsed time")
    if category in ("calibration", "reader"):
        require(row["kind"] == case and row["completed"] == operations and row["expectedRuntime"] == ENVIRONMENT_VERSION, "Calibration identity/completion mismatch")
        require(row["threadStart"] == row["threadEnd"], "Calibration changed threads")
        expected_incomplete = operations if case in ("logical-incomplete-wrapper", "logical-completion-control") or category == "reader" and "incomplete" in case else 0
        require(row["startedIncomplete"] == expected_incomplete, "Incomplete count mismatch")
        require(type(row["currentThreadBytes"]) is int and row["currentThreadBytes"] >= 0, "Invalid current-thread bytes")
        require(math.isfinite(row["currentThreadBytesPerOperation"]) and row["currentThreadBytesPerOperation"] == row["currentThreadBytes"] / operations,
                "Current-thread denominator mismatch")
        if case.startswith("context-flow-"):
            require(row["details"]["flowCallbackChecks"] == operations, "Flow context checks mismatch")
        if category == "reader":
            require(case in READER_CASES, "Unknown reader case")
            width = 32 if case.endswith("-burst32") else 1
            wrapped, incomplete = "wrapped" in case, "incomplete" in case
            require(row["burstWidth"] == width and row["bursts"] == operations // width and operations % width == warmup % width == 0, "Reader burst denominator mismatch")
            require(row["wrapped"] is wrapped and row["forcedIncomplete"] is incomplete, "Reader wrapper/completion identity mismatch")
            for name in ("checks", "completed", "reads", "resets", "sourceCompletions", "sourceGetResults", "advances"):
                require(type(row[name]) is int and row[name] == operations, "Reader lifecycle counter mismatch: " + name)
            expected_registrations = operations if wrapped and incomplete else 0
            require(row["continuationRegistrations"] == row["expectedContinuationRegistrations"] == expected_registrations, "Reader continuation count mismatch")
            require(row["sameThreadVerified"] is True and row["immediateCompletionVerified"] is True, "Reader scheduling mismatch")
            require(type(row["tokenWraps"]) is int and row["tokenWraps"] >= 0, "Reader token-wrap diagnostic invalid")
            for name in ("checks", "completed", "reads", "resets", "sourceCompletions", "sourceGetResults", "advances"):
                require(type(row["warmupCounts"][name]) is int and row["warmupCounts"][name] == warmup, "Reader warmup lifecycle mismatch: " + name)
            require(row["warmupCounts"]["startedIncomplete"] == (warmup if incomplete else 0), "Reader warmup input mismatch")
            require(row["warmupCounts"]["continuationRegistrations"] == (warmup if wrapped and incomplete else 0), "Reader warmup continuation mismatch")
    else:
        parts = case.split("-")
        transport, kind = parts[:2]
        concurrency = int(parts[2][1:]) if len(parts) == 3 else 1
        require(row["transport"] == transport and row["kind"] == kind and row["concurrency"] == concurrency and row["connectionCount"] == 1,
                "Tiny shape mismatch")
        expected = operations + 1 if kind == "add" else 1
        require(row["received"] == row["expectedReceived"] == expected, "Server receive/sentinel mismatch")
        require(row["diagnostic"] is False and row["traceEnabled"] is False, "Unexpected instrumentation")
        require(row["threadPoolMinimumWorkers"] == row["threadPoolMinimumIo"] == 132, "Thread-pool minimum mismatch")
        for name in ("qps", "cpuMilliseconds", "cpuNanosecondsPerOperation", "p50Nanoseconds", "p99Nanoseconds"):
            require(math.isfinite(row[name]) and row[name] >= 0, "Invalid metric " + name)
        require(row["ticksEnd"] > row["ticksStart"] and row["stopwatchFrequency"] > 0 and row["gen0"] >= 0, "Invalid timing/GC counters")


def stage_plan(stage):
    require(stage in ("pilot", "stable-tiny", "stable-micro"), "Unknown stage")
    if stage == "pilot":
        cases = [("tiny", x) for x in ("tcp-add", "tcp-control", "shm-add", "shm-control")]
        cases += [("calibration", x) for x in CALIBRATIONS]
    elif stage == "stable-tiny":
        cases = [("tiny", f"{transport}-{kind}-c{concurrency}") for transport in ("tcp", "shm")
                 for kind in ("add", "control") for concurrency in (1, 32, 128)]
    else:
        cases = [("calibration", x) for x in CALIBRATIONS] + [("reader", x) for x in READER_CASES]
    cycles = 1 if stage == "pilot" else 3
    sequence = [("ABBA", variant) for _ in range(cycles) for variant in ("A", "B", "B", "A")]
    sequence += [("AA", "A"), ("AA", "A")]
    return cases, sequence, (1920 if stage == "stable-tiny" else 1100)


def cv(values):
    require(len(values) >= 2 and all(math.isfinite(value) and value >= 0 for value in values), "Invalid CV inputs")
    mean = statistics.mean(values)
    return statistics.stdev(values) / mean if mean else 0.0


def summarize_cell(rows, category, case, stage, sequence):
    require([row["variant"] for row in rows] == [item[1] for item in sequence], "Balance mismatch")
    require([row["group"] for row in rows] == [item[0] for item in sequence], "Group balance mismatch")
    cohorts = {variant: [row for row in rows if row["group"] == "ABBA" and row["variant"] == variant] for variant in ("A", "B")}
    allocation_cv = {variant: cv([row["bytesPerOperation"] for row in cohort]) for variant, cohort in cohorts.items()}
    qps_cv = {variant: cv([row["qps"] for row in cohort]) for variant, cohort in cohorts.items()} if category == "tiny" else None
    counts_qualify = stage != "pilot" and all(len(cohort) >= 6 for cohort in cohorts.values())
    allocation_stable = counts_qualify and all(value <= .05 for value in allocation_cv.values())
    qps_stable = counts_qualify and all(value <= .10 for value in qps_cv.values()) if qps_cv is not None else None
    stable = allocation_stable and (qps_stable is not False)
    return {"category": category, "case": case,
            "rawBytesPerOperation": [{key: row[key] for key in ("variant", "group", "position", "bytesPerOperation", "processId")} for row in rows],
            "abbaMedian": {variant: statistics.median(row["bytesPerOperation"] for row in cohort) for variant, cohort in cohorts.items()},
            "abbaSampleCounts": {variant: len(cohort) for variant, cohort in cohorts.items()},
            "abbaMetricMedians": {variant: {metric: statistics.median(row[metric] for row in cohort)
                                           for metric in (["bytesPerOperation", "qps", "cpuNanosecondsPerOperation", "p50Nanoseconds", "p99Nanoseconds"]
                                                          if category == "tiny" else ["bytesPerOperation", "currentThreadBytesPerOperation"])}
                                  for variant, cohort in cohorts.items()},
            "allocationCv": allocation_cv, "qpsCv": qps_cv,
            "allocationStable": allocation_stable, "qpsStable": qps_stable, "stable": stable,
            "sameBinaryAA": [{key: row[key] for key in ("sample", "processId", "bytesPerOperation")} | ({"qps": row["qps"]} if category == "tiny" else {}) for row in rows if row["group"] == "AA"],
            "status": "pilot-only; two ABBA samples per variant, not stable acceptance" if stage == "pilot" else
                      "repeatability-gates-passed; no nonregression inference" if stable else "repeatability-gates-not-met; retain all samples"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--stage", choices=("pilot", "stable-tiny", "stable-micro"), default="pilot")
    parser.add_argument("--operations", type=int)
    parser.add_argument("--warmup", type=int)
    parser.add_argument("--calibration-operations", type=int, default=131072)
    parser.add_argument("--calibration-warmup", type=int, default=16384)
    args = parser.parse_args()
    cases, sequence, budget = stage_plan(args.stage)
    args.operations = args.operations if args.operations is not None else (8192 if args.stage == "pilot" else 131072)
    args.warmup = args.warmup if args.warmup is not None else (2048 if args.stage == "pilot" else 32768)
    if args.stage == "pilot":
        require(1 <= args.operations <= 32768 and 1 <= args.warmup <= 8192, "Tiny counts exceed pilot bound")
    else:
        require(args.operations == 131072 and args.warmup == 32768, "Repeated tiny counts are preregistered")
        require(args.calibration_operations == 131072 and args.calibration_warmup == 16384, "Repeated micro counts are preregistered")
    require(1 <= args.calibration_operations <= 262144 and 1 <= args.calibration_warmup <= 32768, "Calibration counts exceed pilot bound")
    here = Path(__file__).resolve().parent
    root = here.parents[3]
    output = Path(args.output).resolve()
    require(not output.exists() or not any(output.iterdir()), "Use a fresh output directory")
    output.mkdir(parents=True, exist_ok=True)
    deadline = time.monotonic() + budget
    env = {key: value for key, value in os.environ.items() if not key.startswith(("DOTNET_", "COMPlus_", "ISSUE739_"))
           and key.upper() not in CREDENTIAL_ENVIRONMENT_NAMES}
    env.update(PINNED_ENV)
    # Preserve DOTNET_ROOT used by setup-dotnet, but prohibit inherited runtime/compiler tuning.
    if "DOTNET_ROOT" in os.environ:
        env["DOTNET_ROOT"] = os.environ["DOTNET_ROOT"]
    commands = []
    def command(argv, cwd, log_name, timeout=600):
        remaining = deadline - time.monotonic()
        require(remaining > min(timeout, 60), "Pilot total time budget exhausted before next command")
        entry = {"argv": [str(x) for x in argv], "cwd": str(cwd), "log": log_name,
                 "timeoutSeconds": min(timeout, remaining), "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
        commands.append(entry)
        write_json(output / "commands.json", commands)
        with open(output / log_name, "w") as log:
            try:
                result = subprocess.run(entry["argv"], cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT,
                                        timeout=entry["timeoutSeconds"], check=False)
                entry["returnCode"] = result.returncode
            except subprocess.TimeoutExpired:
                entry["timedOut"] = True
                raise
            finally:
                write_json(output / "commands.json", commands)
        require(result.returncode == 0, "Command failed; inspect " + log_name)
        return (output / log_name).read_text()
    provenance = {"schemaVersion": 1, "status": "started", "sourceSha": SOURCE, "sdkPin": SDK, "runtimePin": RUNTIME,
                  "stage": args.stage,
                  "interpretation": "JIT same-host stage; " + ("one ABBA" if args.stage == "pilot" else "three ABBA cycles") + "; same-binary A/A retained separately; repeatability is not nonregression or cross-runtime/NativeAOT evidence",
                  "samplePlan": {"cases": cases, "sequence": sequence, "expectedSamples": len(cases) * len(sequence),
                                 "tinyOperations": args.operations, "tinyWarmup": args.warmup,
                                 "microOperations": args.calibration_operations, "microWarmup": args.calibration_warmup},
                  "repeatabilityPolicy": {"ABBA_samplesPerVariantMinimum": 6, "allocationCvMaximum": .05, "qpsCvMaximum": .10,
                                          "sameBinaryAA": "reported separately; never pooled into ABBA acceptance", "crossJobComparison": "not valid for stage deltas"},
                  "environment": PINNED_ENV,
                  "credentialVariableNamesRemoved": sorted(key for key in os.environ if key.upper() in CREDENTIAL_ENVIRONMENT_NAMES),
                  "productionProjectsToggled": PRODUCTION_PROJECTS,
                  "fixtureProjectToggled": "Fixture", "traditionalProjects": TRADITIONAL_PROJECTS,
                  "driverScope": "independent Add-only fixture; A traditional is a comparison control, B production+generated fixture runtime-async on; all measurement consumers stay traditional; original dev source/config untouched",
                  "restorePolicy": "RestoreEnablePackagePruning=false identically in both projected builds; original PackageReferences unchanged; framework asset conflict resolution still applies",
                  "readerLoweringPolicy": "exact-RC1 observed behavior: traditional pooled-builder method in A, runtime-async method with same retained builder attribute in B; no production opt-out attribute added",
                  "readerOptOutReference": "https://github.com/dotnet/runtime/pull/128943 adds separate RuntimeAsyncMethodGeneration(false) to BCL methods; not a universal AsyncMethodBuilder opt-out rule",
                  "driverBinaryPolicy": "one fixed traditional consumer compiled against A, reused in B; independently built B driver archived; only driver DLL/PDB/deps/runtimeconfig/apphost copied",
                  "sampleTimeoutSeconds": 60, "totalScriptBudgetSeconds": budget,
                  "outlierRemoval": "none", "rawControlSubtraction": False,
                  "cpuInfo": Path("/proc/cpuinfo").read_text() if Path("/proc/cpuinfo").exists() else platform.processor(),
                  "os": platform.platform()}
    results = []
    copies = {}
    try:
        assert_original(root)
        names = tracked_inputs(root)
        provenance["gitHead"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
        provenance["originalHashes"] = {name: sha(root / name) for name in names}
        provenance["harnessHashes"] = {str(x.relative_to(root)): sha(x) for x in sorted(here.rglob("*"))
                                       if x.is_file() and not {"bin", "obj", "__pycache__"}.intersection(x.relative_to(here).parts)}
        provenance["calibrationInputHashes"] = {name: sha(here.parent / "calibration" / name) for name in ("Program.cs", "Calibration.csproj")}
        copies = {variant: output / "work" / variant for variant in ("A", "B")}
        provenance["overlays"] = {variant: make_copy(root, here, target, variant, names) for variant, target in copies.items()}
        # Projection can differ only in the recorded lowering target, never C# source, packages, or TFM.
        for name in names:
            require(sha(copies["A"] / name) == sha(copies["B"] / name), "Variant source/config drift: " + name)
        write_json(output / "provenance.json", provenance)
        a_root = copies["A"]
        require(command([args.dotnet, "--version"], a_root, "sdk-version.log").strip() == SDK, "Exact SDK not selected")
        provenance["dotnetInfo"] = command([args.dotnet, "--info"], a_root, "dotnet-info.log")
        sdk_listing = command([args.dotnet, "--list-sdks"], a_root, "sdk-list.log")
        runtime_listing = command([args.dotnet, "--list-runtimes"], a_root, "runtime-list.log")
        sdk_lines = [line for line in sdk_listing.splitlines() if line.startswith(SDK + " [")]
        require(len(sdk_lines) == 1, "Exact SDK installation missing/ambiguous")
        sdk_dir = Path(sdk_lines[0].split("[", 1)[1].rstrip("]")) / SDK
        runtime_lines = [line for line in runtime_listing.splitlines() if line.startswith("Microsoft.NETCore.App " + RUNTIME + " [")]
        require(len(runtime_lines) == 1, "Exact runtime installation missing/ambiguous")
        runtime_dir = Path(runtime_lines[0].split("[", 1)[1].rstrip("]")) / RUNTIME
        provenance["compilerVersion"] = command([args.dotnet, "exec", sdk_dir / "Roslyn/bincore/csc.dll", "-version"], a_root, "compiler-version.log").strip()
        provenance["compilerHashes"] = binary_manifest(sdk_dir / "Roslyn/bincore")
        provenance["runtimeHashes"] = binary_manifest(runtime_dir)
        packs = sdk_dir.parent.parent / "packs/Microsoft.NETCore.App.Ref" / RUNTIME
        require(packs.is_dir(), "Exact reference pack missing")
        provenance["referencePackHashes"] = binary_manifest(packs)
        write_json(output / "provenance.json", provenance)
        variant_data = {}
        for variant, target in copies.items():
            require(command([args.dotnet, "--version"], target, variant + "-sdk-version.log").strip() == SDK, "Variant SDK mismatch")
            relative = here.relative_to(root)
            projects = {"tiny": target / relative / "Driver/Driver.csproj",
                        "calibration": target / relative.parent / "calibration/Calibration.csproj",
                        "metadata": target / relative / "Metadata/Metadata.csproj"}
            if args.stage == "stable-micro":
                projects["reader"] = target / relative / "ReaderControl/ReaderControl.csproj"
            for label, project in projects.items():
                build_log = command([args.dotnet, "build", project, "-c", "Release", "--nologo", "-v:diag",
                         "-bl:" + str(output / f"{variant}-{label}.binlog")], target, f"{variant}-{label}-build.log")
                compiler_lines = compiler_commands(build_log)
                (output / f"{variant}-{label}-compiler-commands.log").write_text("\n".join(compiler_lines) + "\n")
                require(compiler_lines, "No actual compiler invocation retained for " + variant + "/" + label)
            directories = {label: project.parent / "bin/Release/net11.0" for label, project in projects.items()}
            executables = {label: directory / ("Metadata.dll" if label == "metadata" else "SharpLink.Benchmarks.dll")
                           for label, directory in directories.items()}
            if variant == "B":
                archived = output / "independent-B-driver-artifacts"
                provenance["independentBDriverHashes"] = {}
                for label in ("tiny", "calibration", "reader") if args.stage == "stable-micro" else ("tiny", "calibration"):
                    destination = archived / label
                    destination.mkdir(parents=True)
                    allowed_names = {"SharpLink.Benchmarks.dll", "SharpLink.Benchmarks.pdb", "SharpLink.Benchmarks.deps.json",
                                     "SharpLink.Benchmarks.runtimeconfig.json", "SharpLink.Benchmarks", "SharpLink.Benchmarks.exe"}
                    original = sorted(path for path in directories[label].iterdir() if path.name in allowed_names)
                    dependency_hashes = {path.name: sha(path) for path in directories[label].iterdir() if path.is_file() and path.name not in allowed_names}
                    require({path.name for path in original} >= {"SharpLink.Benchmarks.dll", "SharpLink.Benchmarks.pdb", "SharpLink.Benchmarks.deps.json", "SharpLink.Benchmarks.runtimeconfig.json"}, "Missing B driver artifacts")
                    for path in original:
                        require(path.is_file(), "Unexpected driver artifact directory")
                        shutil.copy2(path, destination / path.name)
                    provenance["independentBDriverHashes"][label] = binary_manifest(destination)
                    for name in ("SharpLink.Benchmarks.deps.json", "SharpLink.Benchmarks.runtimeconfig.json"):
                        require(sha(destination / name) == sha(variant_data["A"]["dirs"][label] / name), "Independent A/B driver binding configuration differs")
                    for path in sorted(path for path in variant_data["A"]["dirs"][label].iterdir() if path.name in allowed_names):
                        require(path.is_file(), "Unexpected A driver artifact directory")
                        shutil.copy2(path, directories[label] / path.name)
                    require(dependency_hashes == {path.name: sha(path) for path in directories[label].iterdir() if path.is_file() and path.name not in allowed_names},
                            "Fixed-driver copy mutated B dependencies")
            variant_data[variant] = {"root": target, "dirs": directories, "exe": executables}
            provenance.setdefault("binaryHashes", {})[variant] = {label: binary_manifest(directory) for label, directory in directories.items()}
            provenance.setdefault("generatorHashes", {})[variant] = sha(target / "src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll")
            provenance.setdefault("packageGraphs", {})[variant] = package_manifest(target)
            provenance.setdefault("assetHashes", {})[variant] = {str(x.relative_to(target)): sha(x) for x in sorted(target.rglob("project.assets.json"))}
            provenance.setdefault("generatedSourceHashes", {})[variant] = {str(x.relative_to(target)): sha(x)
                for x in sorted((target / relative / "Fixture/obj/generated").rglob("*.cs"))}
            write_json(output / "provenance.json", provenance)
            assemblies = [directories["tiny"] / (name + ".dll") for name in PRODUCTION_PROJECTS + ["Fixture", "SharpLink.Benchmarks"]]
            command([args.dotnet, "exec", "--fx-version", RUNTIME, executables["metadata"], output / f"{variant}-metadata.json", *assemblies],
                    target, f"{variant}-metadata.log", 60)
            command([args.dotnet, "exec", "--fx-version", RUNTIME, executables["metadata"], output / f"{variant}-calibration-metadata.json", executables["calibration"]],
                    target, f"{variant}-calibration-metadata.log", 60)
            if args.stage == "stable-micro":
                command([args.dotnet, "exec", "--fx-version", RUNTIME, executables["metadata"], output / f"{variant}-reader-metadata.json", executables["reader"]],
                        target, f"{variant}-reader-metadata.log", 60)
        require(provenance["generatorHashes"]["A"] == provenance["generatorHashes"]["B"], "Generator binary changed")
        require(provenance["packageGraphs"]["A"] == provenance["packageGraphs"]["B"], "Resolved package graph differs")
        require(provenance["generatedSourceHashes"]["A"], "No emitted fixture generated source")
        require(provenance["generatedSourceHashes"]["A"] == provenance["generatedSourceHashes"]["B"], "Generated fixture source differs")
        for variant in ("A", "B"):
            tiny = provenance["binaryHashes"][variant]["tiny"]
            calibration = provenance["binaryHashes"][variant]["calibration"]
            for filename in tiny.keys() & calibration.keys():
                if filename.endswith(".dll") and filename != "SharpLink.Benchmarks.dll":
                    require(tiny[filename] == calibration[filename], "Tiny/calibration dependency mismatch: " + variant + "/" + filename)
            for name in ("SharpLink.Client", "SharpLink.Runtime", "SharpLink.Server", "SharpLink.Abstractions", "SharpLink.Sdk"):
                require(name + ".dll" in tiny and name + ".dll" in calibration, "Missing inspected shared dependency: " + name)
        proof = verify_metadata(*[json.loads((output / f"{variant}-metadata.json").read_text()) for variant in ("A", "B")])
        calibration_proof = []
        for variant in ("A", "B"):
            report = json.loads((output / f"{variant}-calibration-metadata.json").read_text())
            methods = report["assemblies"][0]["methods"]
            require(not any(m["runtimeAsync"] for m in methods), "Calibration driver runtime async detected")
            eligible = [m for m in methods if m["stateMachineType"] is not None]
            require(eligible and all(traditional(m) for m in eligible), "Calibration traditional lowering not proven")
            calibration_proof.append(report["assemblies"][0]["sha256"])
        require(calibration_proof[0] == calibration_proof[1], "Calibration executable changed")
        proof["calibrationSha256"] = calibration_proof[0]
        if args.stage == "stable-micro":
            reader_hashes = []
            for variant in ("A", "B"):
                report = json.loads((output / f"{variant}-reader-metadata.json").read_text())
                methods = report["assemblies"][0]["methods"]
                require(not any(method["runtimeAsync"] for method in methods), "Reader driver must remain traditional")
                require(all(traditional(method) for method in methods if method["stateMachineType"] is not None), "Reader state-machine metadata mismatch")
                reader_hashes.append(report["assemblies"][0]["sha256"])
                reader = provenance["binaryHashes"][variant]["reader"]
                tiny = provenance["binaryHashes"][variant]["tiny"]
                require("SharpLink.Runtime.dll" in reader and "SharpLink.Abstractions.dll" in reader, "Reader lacks inspected dependencies")
                for filename in reader.keys() & tiny.keys():
                    if filename.endswith(".dll") and filename != "SharpLink.Benchmarks.dll":
                        require(reader[filename] == tiny[filename], "Reader dependency differs from metadata-inspected variant: " + filename)
            require(reader_hashes[0] == reader_hashes[1], "Reader fixed consumer bytes differ")
            proof["readerDriverSha256"] = reader_hashes[0]
            proof["readerDriverLowering"] = "synchronous fixed consumer; no runtime-async methods; eligible async methods, if present, retain traditional state-machine metadata"
        write_json(output / "lowering-proof.json", proof)
        provenance["status"] = "lowering-verified-before-sampling"
        write_json(output / "provenance.json", provenance)
        for position, (group, variant) in enumerate(sequence, 1):
            ordered = cases if position % 2 else list(reversed(cases))
            for category, case in ordered:
                data = variant_data[variant]
                operations = args.operations if category == "tiny" else args.calibration_operations
                warmup = args.warmup if category == "tiny" else args.calibration_warmup
                sample = f"{category}-{case}-{group}-{position}-{variant}"
                destination = output / (sample + ".sample.json")
                argv = [args.dotnet, "exec", "--fx-version", RUNTIME, data["exe"][category]]
                argv += case.split("-")[:2] if category == "tiny" else [case]
                argv += [str(operations), str(warmup), sample, str(destination), ENVIRONMENT_VERSION]
                if category == "tiny" and args.stage != "pilot":
                    argv.append(case.split("-")[2][1:])
                command(argv, data["root"], sample + ".log", 60)
                row = json.loads(destination.read_text())
                expected_hash = provenance["binaryHashes"][variant][category]["SharpLink.Benchmarks.dll"]
                validate_sample(row, category, case, sample, operations, warmup, expected_hash)
                if category == "reader":
                    require(row["runtimeAssemblySha256"] == provenance["binaryHashes"][variant]["reader"]["SharpLink.Runtime.dll"], "Executed reader production dependency mismatch")
                    require(row["corelibSha256"] == provenance["runtimeHashes"]["System.Private.CoreLib.dll"] and Path(row["corelibPath"]).parent.name == RUNTIME, "Reader actual framework identity mismatch")
                require(row["processId"] not in {x["processId"] for x in results}, "Sample PID reused")
                results.append({**row, "category": category, "case": case, "variant": variant,
                                "group": group, "position": position, "cycle": (position - 1) // 4 + 1 if group == "ABBA" else None, "sampleSha256": sha(destination)})
                write_json(output / "completed-samples.json", results)
                print(sample, f'{row["bytesPerOperation"]:.6f} B/op', flush=True)
        summaries = []
        for category, case in cases:
            rows = [x for x in results if x["category"] == category and x["case"] == case]
            summaries.append(summarize_cell(rows, category, case, args.stage, sequence))
        require(len(results) == len(cases) * len(sequence), "Incomplete preregistered matrix")
        assert_original(root)
        require(provenance["originalHashes"] == {name: sha(root / name) for name in names}, "Original build inputs changed during pilot")
        # Detect post-proof mutation before declaring success, including all executed DLL/config dependencies.
        for variant, data in variant_data.items():
            for label, directory in data["dirs"].items():
                require(binary_manifest(directory) == provenance["binaryHashes"][variant][label], "Binaries/config changed after proof")
        provenance["status"] = "complete-pilot-only" if args.stage == "pilot" else "complete-repeated-stage"
        write_json(output / "summary.json", {"schemaVersion": 1, "status": provenance["status"],
                   "interpretation": provenance["interpretation"], "sourceSha": SOURCE, "samples": len(results),
                   "stage": args.stage, "stableAcceptance": args.stage != "pilot" and all(row["stable"] for row in summaries),
                   "stableAcceptanceMeaning": "repeatability gates only; does not accept a performance regression or production change",
                   "allAllocationStable": all(row["allocationStable"] for row in summaries),
                   "allQpsStable": all(row["qpsStable"] for row in summaries) if args.stage == "stable-tiny" else None,
                   "nativeAot": "not attempted; reflection calibration is JIT only", "cases": summaries})
    except Exception as error:
        provenance["status"] = "failed-closed"
        provenance["failure"] = str(error)
        provenance["completedSamples"] = len(results)
        raise
    finally:
        # Export inspectable evidence even after build/proof failure. The status above is authoritative;
        # partially present outputs are never classified as accepted samples or verified binaries.
        try:
            evidence_hashes = {}
            for variant, target in copies.items():
                projected = provenance.get("overlays", {}).get(variant, {})
                for name in projected:
                    source = target / name
                    if source.is_file():
                        destination = output / "build-overlays" / variant / name
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(source, destination)
                for name in ("Directory.Packages.props", ".editorconfig"):
                    source = target / name
                    if source.is_file():
                        destination = output / "build-overlays" / variant / name
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(source, destination)
                relative = here.relative_to(root)
                project_directories = {"tiny": target / relative / "Driver",
                                       "calibration": target / relative.parent / "calibration",
                                       "metadata": target / relative / "Metadata"}
                if args.stage == "stable-micro":
                    project_directories["reader"] = target / relative / "ReaderControl"
                for label, project_directory in project_directories.items():
                    source_directory = project_directory / "bin/Release/net11.0"
                    for source in sorted(source_directory.rglob("*")):
                        if source.is_file() and (source.suffix in (".dll", ".pdb", ".json", ".exe", ".so", ".dylib") or source.name in ("SharpLink.Benchmarks", "Metadata")):
                            destination = output / "executed-binaries" / variant / label / source.relative_to(source_directory)
                            destination.parent.mkdir(parents=True, exist_ok=True)
                            shutil.copy2(source, destination)
                            evidence_hashes[str(destination.relative_to(output))] = sha(destination)
                generator = target / "src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll"
                if generator.is_file():
                    destination = output / "executed-binaries" / variant / "generator" / generator.name
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(generator, destination)
                    evidence_hashes[str(destination.relative_to(output))] = sha(destination)
                generated = target / relative / "Fixture/obj/generated"
                for source in sorted(generated.rglob("*.cs")):
                    destination = output / "generated-fixture" / variant / source.relative_to(generated)
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, destination)
            provenance["exportedBinaryHashes"] = evidence_hashes
            provenance["exportStatus"] = "available files exported; acceptance depends on overall status and lowering proof"
        except Exception as error:
            provenance["status"] = "failed-closed"
            provenance["artifactCollectionFailure"] = str(error)
            raise
        finally:
            write_json(output / "provenance.json", provenance)


if __name__ == "__main__":
    main()
