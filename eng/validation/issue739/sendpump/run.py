#!/usr/bin/env python3
"""Finite source-pinned SendPump controls. No production optimization or RPC owner claim."""
import argparse
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import shutil
import statistics
import subprocess
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
_spec = importlib.util.spec_from_file_location("issue739_reviewed_net11", HERE.parent / "net11/run_pilot.py")
reviewed = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(reviewed)
SOURCE = reviewed.SOURCE
CASES = ["pump-idle-sync", "pump-force-sync", "pump-flush-gated", "pump-capacity-completed",
         "pump-capacity-incomplete", "fixture-sync", "fixture-gated", "fixture-pair"]
PINS = {"local-pilot": ("10.0.102", "10.0.2", "10.0.2", "net10.0"),
        "net10": ("10.0.112", "10.0.12", "10.0.12", "net10.0"),
        "net11": (reviewed.SDK, reviewed.RUNTIME, reviewed.ENVIRONMENT_VERSION, "net11.0")}
PRODUCTION = ["SharpLink.Abstractions", "SharpLink.Runtime"]
DRIVER_FILES = {"SharpLink.Benchmarks.dll", "SharpLink.Benchmarks.pdb", "SharpLink.Benchmarks.deps.json",
                "SharpLink.Benchmarks.runtimeconfig.json", "SharpLink.Benchmarks", "SharpLink.Benchmarks.exe"}
sha, require, write_json = reviewed.sha, reviewed.require, reviewed.write_json


def own_inputs():
    result = [p for p in HERE.rglob("*") if p.is_file() and not {"bin", "obj", "__pycache__"}.intersection(p.relative_to(HERE).parts)]
    result += [HERE.parent / "net11/run_pilot.py", HERE.parent / "net11/Metadata/Program.cs",
               HERE.parent / "net11/Metadata/Metadata.csproj"]
    return sorted(result)


def make_copy(target, variant, sdk, tfm, names):
    target.mkdir(parents=True)
    for name in names:
        source, destination = ROOT / name, target / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
    overlays = {}
    for path in [target / "Directory.Build.props", *sorted((target / "src").rglob("*.csproj"))]:
        old = path.read_text(encoding="utf-8-sig")
        new = old.replace(">net10.0</TargetFramework>", ">" + tfm + "</TargetFramework>")
        if path == target / "Directory.Build.props":
            new = new.replace("</Project>", '''  <PropertyGroup>
    <LangVersion>preview</LangVersion><PathMap>$(MSBuildThisFileDirectory)=/_/</PathMap>
    <UseSharedCompilation>false</UseSharedCompilation><UseRuntimeAsync>false</UseRuntimeAsync>
    <RestoreEnablePackagePruning>false</RestoreEnablePackagePruning>
  </PropertyGroup>
</Project>''')
        if old != new:
            path.write_text(new)
            overlays[str(path.relative_to(target))] = {"originalSha256": sha(ROOT / path.relative_to(target)), "projectedSha256": sha(path)}
    global_json = target / "global.json"
    original_global = sha(global_json)
    write_json(global_json, {"sdk": {"version": sdk, "rollForward": "disable", "allowPrerelease": tfm == "net11.0"}})
    overlays["global.json"] = {"originalSha256": original_global, "projectedSha256": sha(global_json)}
    targets = target / "Directory.Build.targets"
    require(not targets.exists(), "Unexpected existing targets requires review")
    toggle = " Or ".join("'$(MSBuildProjectName)' == '" + p + "'" for p in PRODUCTION)
    flag = "on" if variant == "B" else "off"
    targets.write_text(f'''<Project>
  <PropertyGroup>
    <Issue739Lowering>off</Issue739Lowering>
    <Issue739Lowering Condition="{toggle}">{flag}</Issue739Lowering>
    <UseRuntimeAsync Condition="'$(Issue739Lowering)' == 'on'">true</UseRuntimeAsync>
    <UseRuntimeAsync Condition="'$(Issue739Lowering)' == 'off'">false</UseRuntimeAsync>
  </PropertyGroup>
  <Target Name="Issue739CompilerLowering" BeforeTargets="CoreCompile">
    <PropertyGroup><Features>runtime-async=$(Issue739Lowering)</Features></PropertyGroup>
    <Message Importance="High" Text="ISSUE739_COMPILER $(MSBuildProjectName) TargetFramework=$(TargetFramework) UseRuntimeAsync=$(UseRuntimeAsync) Features=$(Features)" />
  </Target>
</Project>
''')
    overlays["Directory.Build.targets"] = {"projectedSha256": sha(targets), "variant": variant}
    return overlays


def prove_metadata(reports, runtime, environment_version):
    selected = [("SharpLink.Runtime.RpcSession+SendPump", name) for name in
                ("RunAsync", "FlushAndReleaseAsync", "EnqueueAsync", "ReserveAsync")]
    selected += [("SharpLink.Runtime.RpcSession", name) for name in
                 ("SendPacketAndFlushAsync", "SendPacketAsync", "AwaitBackpressureEnqueueAsync")]
    result = {"selected": {}, "driverPolicy": "synchronous traditional fixed-A IL; no async driver methods", "bcl": "same precompiled pinned shared framework"}
    for variant, report in reports.items():
        require(report["runtime"] == environment_version and runtime in report["framework"] and Path(report["corelibPath"]).parent.name == runtime,
                "Inspector runtime mismatch")
        assemblies = {Path(x["path"]).stem: x for x in report["assemblies"]}
        require(set(assemblies) == set(PRODUCTION + ["SharpLink.Benchmarks"]), "Inspector assembly inventory mismatch")
        for name, assembly in assemblies.items():
            if variant == "A" or name == "SharpLink.Benchmarks":
                require(not any(x["runtimeAsync"] for x in assembly["methods"]), "Unexpected runtime async: " + variant + "/" + name)
            if name == "SharpLink.Benchmarks":
                require(not any(x["stateMachineType"] is not None for x in assembly["methods"]), "Synchronous measurement driver gained an async state machine")
        methods = assemblies["SharpLink.Runtime"]["methods"]
        result["selected"][variant] = []
        for type_name, name in selected:
            matches = [m for m in methods if m["type"] == type_name and m["name"] == name]
            require(len(matches) == 1, "Selected method ambiguity: " + type_name + "." + name)
            method = matches[0]
            require(reviewed.traditional(method) if variant == "A" else
                    method["runtimeAsync"] and method["stateMachineType"] is None and not method["hasMoveNext"],
                    "Expected lowering absent: " + variant + "/" + name)
            result["selected"][variant].append(method)
        pooled = [m for m in methods if m["type"] == "SharpLink.Runtime.ReadOwnershipPipeReader" and m["name"] == "AwaitReadAsync"]
        require(len(pooled) == 1 and "PoolingAsyncValueTaskMethodBuilder" in (pooled[0]["builderType"] or ""), "Pooled reader annotation changed")
        require(reviewed.traditional(pooled[0]) if variant == "A" else
                pooled[0]["runtimeAsync"] and pooled[0]["stateMachineType"] is None and not pooled[0]["hasMoveNext"], "Exact compiler pooled-reader lowering mismatch")
        result.setdefault("pooledReader", {})[variant] = pooled[0]
    if "B" in reports:
        for name in PRODUCTION + ["SharpLink.Benchmarks"]:
            a, b = [next(x for x in reports[v]["assemblies"] if Path(x["path"]).stem == name) for v in "AB"]
            require(a["identity"] == b["identity"], "Assembly identity changed")
            sig = lambda x: {reviewed.method_key(m) for m in x["methods"] if "<" not in m["type"] and "<" not in m["name"]}
            require(sig(a) == sig(b), "Semantic method signatures changed: " + name)
            if name == "SharpLink.Benchmarks": require(a["sha256"] == b["sha256"], "Fixed driver differs")
        result["pooledReaderInterpretation"] = "Exact RC1 retains custom-builder annotation but B is runtime-async; annotation is not automatic opt-out. Reader is metadata witness only, not a measured SendPump owner."
    return result


def validate_sample(row, kind, sample, cycles, warmup, runtime, environment_version, binary_hashes, runtime_hashes):
    require(row["schemaVersion"] == 1 and row["sourceSha"] == SOURCE and row["kind"] == kind and row["sample"] == sample, "Sample identity mismatch")
    require(row["runtime"] == row["expectedRuntime"] == environment_version and runtime in row["framework"], "Sample runtime mismatch")
    require(Path(row["corelibPath"]).parent.name == runtime and row["corelibSha256"] == runtime_hashes["System.Private.CoreLib.dll"], "Executed shared framework mismatch")
    require(row["executableSha256"] == binary_hashes["SharpLink.Benchmarks.dll"] and row["runtimeAssemblySha256"] == binary_hashes["SharpLink.Runtime.dll"], "Executed binary mismatch")
    require(row["cycles"] == cycles and row["warmup"] == warmup and row["threadStart"] == row["threadEnd"], "Controller/count mismatch")
    require(type(row["processId"]) is int and row["processId"] > 0 and row["architecture"] == "X64" and row["serverGc"] is False, "Process configuration mismatch")
    require(row["precise"] is True and row["driverIncluded"] is True and row["subtractionApplied"] is False, "Measurement policy mismatch")
    fixture, force = kind.startswith("fixture-"), kind == "pump-force-sync"
    pair = kind in ("pump-capacity-completed", "pump-capacity-incomplete", "fixture-pair")
    gated = kind not in ("pump-idle-sync", "pump-force-sync", "fixture-sync")
    fpc = 2 if pair else 1
    require(row["fixtureOnly"] == fixture and row["forceFlushCompletionContract"] == force and row["controlledIncompleteFlush"] == gated, "Case activation mismatch")
    require(row["framesPerCycle"] == fpc and row["frames"] == cycles*fpc and row["frameBytes"] == 15 and
            row["queueCapacity"] == (15 if kind == "pump-capacity-incomplete" else 30), "Frame/capacity mismatch")
    require(row["profile"] == "Balanced" and row["explicitTimedBatch"] is False and row["callerCancellationToken"] == "None", "Policy mismatch")
    require(row["writerPendingEnd"] == row["writerRegistrationInFlightEnd"] == 0 and row["writerConsumedEnd"] == 1 and
            row["maximumRegistrationInFlight"] == int(gated), "Source end-state mismatch")
    for label, n in (("counts", cycles), ("warmupCounts", warmup)):
        c = row[label]
        expected = {"Cycles": n, "Frames": n*fpc, "LeaseActiveObservations": n*fpc*(2 if gated else 1),
                    "LeaseReturnedObservations": n*fpc, "PreParked": 0 if fixture else n, "PostParked": 0 if fixture else n,
                    "FirstAdmissionsCompleted": n if not fixture and not force else 0,
                    "SecondCompleted": n if kind == "pump-capacity-completed" else 0,
                    "SecondIncomplete": n if kind == "pump-capacity-incomplete" else 0,
                    "ForceCompletionConsumed": n if force else 0, "HeldQueueChecks": n*fpc if gated and not fixture else 0}
        for key, value in expected.items(): require(type(c[key]) is int and c[key] == value, label + " exact lifecycle mismatch: " + key)
        w = c["Writer"]
        expected_writer = {"Flushes": n*fpc, "Frames": n*fpc, "Bytes": n*fpc*15, "SyncFlushes": n*(fpc-int(gated))}
        expected_writer.update({key: n*int(gated) for key in ("PendingFlushes", "Resets", "RegistrationEntries", "RegistrationsAccepted", "RegistrationsReturned", "Releases", "GetResults")})
        for key, value in expected_writer.items(): require(type(w[key]) is int and w[key] == value, label + " exact writer mismatch: " + key)
        start_resets = 0 if label == "warmupCounts" else warmup
        expected_wraps = ((start_resets+n+32768)//65536-(start_resets+32768)//65536) if gated else 0
        require(type(w["TokenWraps"]) is int and w["TokenWraps"] == expected_wraps, "Exact signed-token rollover count mismatch")
    for key in ("bytes", "gen0Collections"): require(type(row[key]) is int and row[key] >= 0, "Invalid counter")
    for key in ("elapsedSeconds", "nanosecondsPerCycle", "nanosecondsPerFrame", "cpuSeconds", "bytesPerCycle", "bytesPerFrame"):
        require(math.isfinite(row[key]) and row[key] >= 0, "Invalid measured value")
    require(row["elapsedSeconds"] > 0 and row["bytesPerCycle"] == row["bytes"]/cycles and row["bytesPerFrame"] == row["bytes"]/(cycles*fpc), "Invalid denominator")
    require(math.isclose(row["elapsedSeconds"], (row["ticksEnd"]-row["ticksStart"])/row["stopwatchFrequency"], rel_tol=1e-12), "Timer identity mismatch")
    require(math.isclose(row["nanosecondsPerCycle"], row["elapsedSeconds"]*1e9/cycles, rel_tol=1e-12) and
            math.isclose(row["nanosecondsPerFrame"], row["elapsedSeconds"]*1e9/(cycles*fpc), rel_tol=1e-12), "Time denominator mismatch")


def summarize(rows, stage):
    result = []
    for kind in CASES:
        cell = {"kind": kind, "variants": {}}
        for variant in sorted({r["variant"] for r in rows}):
            values = [r for r in rows if r["kind"] == kind and r["variant"] == variant and (stage != "net11" or r["group"] == "ABBA")]
            cell["variants"][variant] = {key: {"values": [v[key] for v in values], "median": statistics.median(v[key] for v in values),
                "minimum": min(v[key] for v in values), "maximum": max(v[key] for v in values)}
                for key in ("bytesPerCycle", "bytesPerFrame", "nanosecondsPerCycle", "cpuSeconds", "gen0Collections")}
        if stage == "net11":
            cell["abbaCycleMedianDifferenceBMinusA"] = [statistics.median(r["bytesPerCycle"] for r in rows if r["kind"] == kind and r["group"] == "ABBA" and r["cycle"] == cycle and r["variant"] == "B")-
                statistics.median(r["bytesPerCycle"] for r in rows if r["kind"] == kind and r["group"] == "ABBA" and r["cycle"] == cycle and r["variant"] == "A") for cycle in (1,2,3)]
            aa = [r["bytesPerCycle"] for r in rows if r["kind"] == kind and r["group"] == "AA"]
            cell["aaBytesPerCycle"] = aa
            cell["aaDifferenceSecondMinusFirst"] = aa[1]-aa[0]
        result.append(cell)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", choices=PINS, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work-root", type=Path, required=True)
    parser.add_argument("--budget-seconds", type=int, default=840)
    parser.add_argument("--nuget-source")
    parser.add_argument("--cycles", type=int)
    parser.add_argument("--warmup", type=int)
    args = parser.parse_args()
    require(args.budget_seconds in range(30, 3601), "Budget must be30–3600seconds")
    require(args.stage == "local-pilot" or args.nuget_source is None, "Offline source/audit override is local-only")
    require(args.stage == "local-pilot" or args.cycles is None and args.warmup is None, "Hosted count overrides forbidden")
    cycles = args.cycles if args.cycles is not None else (128 if args.stage == "local-pilot" else 16384)
    warmup = args.warmup if args.warmup is not None else (64 if args.stage == "local-pilot" else 4096)
    require(0 < cycles <= 1048576 and 0 < warmup <= 65536, "Invalid cycle bounds")
    output, work = args.output.resolve(), args.work_root.resolve()
    require(not output.exists() and not work.exists(), "Output/work roots must be fresh; failures may not be overwritten")
    output.mkdir(parents=True); work.mkdir(parents=True)
    started = time.monotonic(); deadline = started+args.budget_seconds
    sdk, runtime, environment_version, tfm = PINS[args.stage]
    variants = "AB" if args.stage == "net11" else "A"
    sequence = ([("ABBA", v, c) for c in (1,2,3) for v in "ABBA"]+[("AA", "A", None)]*2) if args.stage == "net11" else [("fresh", "A", i+1) for i in range(1 if args.stage == "local-pilot" else 6)]
    names = sorted(set(reviewed.tracked_inputs(ROOT)+[str(p.relative_to(ROOT)) for p in own_inputs()]))
    provenance = {"schemaVersion": 1, "sourceSha": SOURCE, "stage": args.stage, "sdk": sdk, "runtime": runtime,
                  "cycles": cycles, "warmup": warmup, "caseOrder": CASES, "sequence": sequence,
                  "expectedRecords": len(sequence)*len(CASES), "budgetSeconds": args.budget_seconds,
                  "processTimeoutSeconds": 60, "noRetries": True, "noOutlierPruning": True,
                  "status": "starting", "originalHashes": {name: sha(ROOT/name) for name in names},
                  "interpretation": "local-pilot correctness only; hosted gross controlled SendPump shapes with matching fixture raw bytes. No baseline subtraction or ordinary RPC ownership claim."}
    rows, copies, binaries, metadata_reports = [], {}, {}, {}
    env = {k:v for k,v in os.environ.items() if k not in reviewed.CREDENTIAL_ENVIRONMENT_NAMES}
    env.update(reviewed.PINNED_ENV)
    def command(argv, cwd, log, timeout=600):
        remaining = deadline-time.monotonic()
        require(remaining > 0, "Whole-run budget expired")
        with (output/log).open("w") as stream:
            process = subprocess.run([str(x) for x in argv], cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT,
                                     timeout=min(timeout,remaining), check=False)
        require(process.returncode == 0, f"Command failed ({process.returncode}); retained {log}")
        return (output/log).read_text()
    def save(): write_json(output/"provenance.json", provenance)
    try:
        reviewed.assert_original(ROOT); save()
        for variant in variants:
            target = work/variant; copies[variant] = target
            provenance.setdefault("overlays", {})[variant] = make_copy(target,variant,sdk,tfm,names)
        aroot = copies["A"]
        require(command([args.dotnet,"--version"],aroot,"sdk-version.log").strip() == sdk, "Exact SDK not selected")
        provenance["dotnetInfo"] = command([args.dotnet,"--info"],aroot,"dotnet-info.log")
        sdk_list = command([args.dotnet,"--list-sdks"],aroot,"sdk-list.log")
        runtime_list = command([args.dotnet,"--list-runtimes"],aroot,"runtime-list.log")
        def listed_dir(listing, prefix, version):
            lines = [line for line in listing.splitlines() if line.startswith(prefix+" [")]
            require(len(lines) == 1, "Exact toolchain installation missing or ambiguous: "+prefix)
            return Path(lines[0].split("[",1)[1].rstrip("]"))/version
        sdk_dir = listed_dir(sdk_list,sdk,sdk)
        runtime_dir = listed_dir(runtime_list,"Microsoft.NETCore.App "+runtime,runtime)
        provenance["compilerVersion"] = command([args.dotnet,"exec",sdk_dir/"Roslyn/bincore/csc.dll","-version"],aroot,"compiler-version.log").strip()
        provenance["compilerHashes"] = reviewed.binary_manifest(sdk_dir/"Roslyn/bincore")
        provenance["runtimeHashes"] = reviewed.binary_manifest(runtime_dir)
        reference = sdk_dir.parent.parent/"packs/Microsoft.NETCore.App.Ref"/runtime
        require(reference.is_dir(), "Exact reference pack unavailable")
        provenance["referencePackHashes"] = reviewed.binary_manifest(reference); save()
        for variant,target in copies.items():
            require(command([args.dotnet,"--version"],target,variant+"-sdk.log").strip() == sdk, "Variant SDK mismatch")
            projects = {"driver": target/HERE.relative_to(ROOT)/"Driver/SendPumpDriver.csproj",
                        "metadata": target/HERE.parent.relative_to(ROOT)/"net11/Metadata/Metadata.csproj"}
            for label,project in projects.items():
                argv = [args.dotnet,"build",project,"-c","Release","--nologo","-v:diag","-m:1","-bl:"+str(output/f"{variant}-{label}.binlog")]
                if args.nuget_source: argv += ["--source",args.nuget_source,"-p:NuGetAudit=false"]
                log = command(argv,target,f"{variant}-{label}-build.log")
                commands = reviewed.compiler_commands(log)
                require(commands, "No actual compiler invocations: "+variant+"/"+label)
                (output/f"{variant}-{label}-compiler-commands.log").write_text("\n".join(commands)+"\n")
                if label == "driver":
                    for project_name in PRODUCTION+["SendPumpDriver"]:
                        wanted = "on" if variant == "B" and project_name in PRODUCTION else "off"
                        require(f"ISSUE739_COMPILER {project_name} TargetFramework={tfm} UseRuntimeAsync={str(wanted=='on').lower()} Features=runtime-async={wanted}" in log,
                                "Compiler project toggle not witnessed: "+project_name)
                        assembly_name = "SharpLink.Benchmarks" if project_name == "SendPumpDriver" else project_name
                        actual = [line for line in commands if re.search(r"/out:[^\s]*"+re.escape(assembly_name)+r"\.dll(?:[\s\"]|$)", line)]
                        require(len(actual) == 1 and "runtime-async="+wanted in actual[0], "Actual csc toggle not witnessed: "+project_name)
            directory = projects["driver"].parent/"bin/Release"/tfm
            if variant == "B":
                archived = output/"independent-B-driver-artifacts"; archived.mkdir()
                dependency_hashes = {p.name:sha(p) for p in directory.iterdir() if p.is_file() and p.name not in DRIVER_FILES}
                for name in DRIVER_FILES:
                    if (directory/name).is_file(): shutil.copy2(directory/name,archived/name)
                require({p.name for p in archived.iterdir()} >= {"SharpLink.Benchmarks.dll","SharpLink.Benchmarks.pdb","SharpLink.Benchmarks.deps.json","SharpLink.Benchmarks.runtimeconfig.json"}, "Incomplete independent B artifacts")
                for name in ("SharpLink.Benchmarks.deps.json","SharpLink.Benchmarks.runtimeconfig.json"):
                    require(sha(archived/name) == sha(binaries["A"]/name), "Independent driver binding configs differ")
                provenance["independentBDriverHashes"] = reviewed.binary_manifest(archived)
                for name in DRIVER_FILES:
                    if (binaries["A"]/name).is_file(): shutil.copy2(binaries["A"]/name,directory/name)
                require(dependency_hashes == {p.name:sha(p) for p in directory.iterdir() if p.is_file() and p.name not in DRIVER_FILES}, "Driver reuse changed B dependencies")
            binaries[variant] = directory
            provenance.setdefault("binaryHashes",{})[variant] = reviewed.binary_manifest(directory)
            provenance.setdefault("packageGraphs",{})[variant] = reviewed.package_manifest(target)
            provenance.setdefault("assetHashes",{})[variant] = {str(p.relative_to(target)):sha(p) for p in target.rglob("project.assets.json")}
            inspector = projects["metadata"].parent/"bin/Release"/tfm/"Metadata.dll"
            provenance.setdefault("inspectorHashes",{})[variant] = reviewed.binary_manifest(inspector.parent)
            command([args.dotnet,"exec","--fx-version",runtime,inspector,output/f"{variant}-metadata.json",
                    *[directory/(name+".dll") for name in PRODUCTION+["SharpLink.Benchmarks"]]],target,f"{variant}-metadata.log",60)
            metadata_reports[variant] = json.loads((output/f"{variant}-metadata.json").read_text()); save()
        if "B" in variants: require(provenance["packageGraphs"]["A"] == provenance["packageGraphs"]["B"], "Resolved package graph differs")
        proof = prove_metadata(metadata_reports,runtime,environment_version)
        write_json(output/"lowering-proof.json",proof)
        provenance["status"] = "activation-ready-before-sampling"; save()
        for position,(group,variant,cycle) in enumerate(sequence,1):
            for kind in CASES if position%2 else reversed(CASES):
                sample = f"{position:02d}-{group}-{variant}-{kind}"
                path = output/(sample+".sample.json")
                command([args.dotnet,"exec","--fx-version",runtime,binaries[variant]/"SharpLink.Benchmarks.dll",
                         kind,cycles,warmup,sample,path,environment_version],copies[variant],sample+".log",60)
                row = json.loads(path.read_text())
                validate_sample(row,kind,sample,cycles,warmup,runtime,environment_version,
                                provenance["binaryHashes"][variant],provenance["runtimeHashes"])
                require(row["processId"] not in {r["processId"] for r in rows}, "Fresh sample PID reused")
                rows.append({**row,"variant":variant,"group":group,"cycle":cycle,"position":position,"sampleSha256":sha(path)})
                write_json(output/"completed-samples.json",rows)
                print(sample,f'{row["bytesPerCycle"]:.6f} B/cycle; {row["elapsedSeconds"]:.6f}s; exact lifecycle passed',flush=True)
        require(len(rows) == len(CASES)*len(sequence), "Incomplete matrix")
        reviewed.assert_original(ROOT)
        require(names == sorted(set(reviewed.tracked_inputs(ROOT)+[str(p.relative_to(ROOT)) for p in own_inputs()])), "Input inventory changed during run")
        require(provenance["originalHashes"] == {name:sha(ROOT/name) for name in names}, "Source/harness changed during run")
        for variant,directory in binaries.items():
            require(provenance["binaryHashes"][variant] == reviewed.binary_manifest(directory), "Executed binaries/config changed")
            inspector_dir = copies[variant]/HERE.parent.relative_to(ROOT)/"net11/Metadata/bin/Release"/tfm
            require(provenance["inspectorHashes"][variant] == reviewed.binary_manifest(inspector_dir), "Inspector binaries changed")
        require(provenance["compilerHashes"] == reviewed.binary_manifest(sdk_dir/"Roslyn/bincore") and
                provenance["runtimeHashes"] == reviewed.binary_manifest(runtime_dir) and
                provenance["referencePackHashes"] == reviewed.binary_manifest(reference), "Toolchain changed")
        provenance["status"] = "complete-local-correctness-only" if args.stage == "local-pilot" else "complete-finite-measurement"
        write_json(output/"summary.json", {"schemaVersion":1,"sourceSha":SOURCE,"stage":args.stage,"status":provenance["status"],
            "records":len(rows),"allLifecycleChecksPassed":True,"subtractionApplied":False,"outlierPruning":False,
            "interpretation":provenance["interpretation"],"cases":summarize(rows,args.stage)})
    except BaseException as error:
        provenance["status"] = "failed-closed"; provenance["failure"] = repr(error)
        raise
    finally:
        provenance["completedSamples"] = len(rows); provenance["wallSeconds"] = time.monotonic()-started
        try:
            for variant,target in copies.items():
                for name in provenance.get("overlays",{}).get(variant,{}):
                    source = target/name
                    if source.is_file():
                        dest = output/"build-overlays"/variant/name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source,dest)
                for label,directory in (("driver",target/HERE.relative_to(ROOT)/"Driver/bin/Release"/tfm),
                                        ("metadata",target/HERE.parent.relative_to(ROOT)/"net11/Metadata/bin/Release"/tfm)):
                    if directory.is_dir(): shutil.copytree(directory,output/"executed-binaries"/variant/label)
            for variant,directory in binaries.items():
                exported = output/"executed-binaries"/variant/"driver"
                require(reviewed.binary_manifest(exported) == provenance["binaryHashes"][variant], "Exported executed binary/config mismatch")
            provenance["exportedBinaryHashes"] = reviewed.binary_manifest(output/"executed-binaries")
            provenance["rawSampleHashes"] = {p.name:sha(p) for p in sorted(output.glob("*.sample.json"))}
            provenance["endInputHashes"] = {name:sha(ROOT/name) for name in names}
            for name in names:
                if name.startswith("eng/validation/issue739/"):
                    dest=output/"harness-source"/name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(ROOT/name,dest)
        except BaseException as error:
            provenance["status"] = "failed-closed"; provenance["exportFailure"] = repr(error)
            raise
        finally: save()


if __name__ == "__main__":
    main()
