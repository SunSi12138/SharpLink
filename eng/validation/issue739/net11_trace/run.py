#!/usr/bin/env python3
"""Preregistered net11 Add-c1 trace feasibility; 20 workload processes, 8 captures, no retries."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import shlex
import shutil
import statistics
import subprocess
import sys
import time

from project import HERE, ROOT, NET11, TRACE, pilot, project, require, summary
from validate import CASES, SEQUENCE, OPERATIONS, WARMUP, TRACE_VERSION, DECODER_PACKAGE, TOOL_SDK, TOOL_RUNTIME, validate_sample, validate_trace

write_json, sha, manifest = pilot.write_json, pilot.sha, pilot.binary_manifest
DRIVER_FILES = {"SharpLink.Benchmarks.dll", "SharpLink.Benchmarks.pdb", "SharpLink.Benchmarks.deps.json",
                "SharpLink.Benchmarks.runtimeconfig.json", "SharpLink.Benchmarks", "SharpLink.Benchmarks.exe"}
INTERPRETATION = ("Same-host JIT diagnostic feasibility only; two traced observations per lowering arm per cell; "
                  "no stable acceptance, exact object/owner coverage, control subtraction, cross-job/runtime or AOT inference")


def contrasts(rows):
    """Positions 9/10 are same-binary A/A controls, never pooled into the 2x2 comparison."""
    require(len(rows) == 10 and [(row["variant"], row["traceEnabled"]) for row in rows] == list(SEQUENCE), "Incomplete or reordered diagnostic cell")
    groups = {}
    for variant in ("A", "B"):
        for traced in (False, True):
            selected = [row for row in rows[:8] if row["variant"] == variant and row["traceEnabled"] is traced]
            require(len(selected) == 2, "Diagnostic group is not exactly two samples")
            key = variant + ("-traced" if traced else "-untraced")
            groups[key] = {"samples": [row["sample"] for row in selected], "count": 2,
                           "mean": {metric: statistics.mean(row[metric] for row in selected) for metric in
                                    ("bytesPerOperation", "qps", "cpuNanosecondsPerOperation", "p50Nanoseconds", "p99Nanoseconds")}}
    def delta(left, right):
        return {metric: {"rightMinusLeft": groups[right]["mean"][metric] - value,
                         "rightOverLeft": groups[right]["mean"][metric] / value if value else None}
                for metric, value in groups[left]["mean"].items()}
    return {"case": rows[0]["case"], "groups": groups,
            "withinLoweringObserverContrasts": {variant: delta(variant + "-untraced", variant + "-traced") for variant in ("A", "B")},
            "betweenLoweringContrasts": {mode: delta("A-" + mode, "B-" + mode) for mode in ("traced", "untraced")},
            "sameBinaryAA": [{key: row[key] for key in ("sample", "processId", "bytesPerOperation", "qps")} for row in rows[8:]],
            "interpretation": INTERPRETATION, "stableAcceptance": False}


def decoder_source(original):
    edits = [
        ('long inWindow = 0, outsideWindow = 0, otherProcess = 0;',
         'long inWindow = 0, outsideWindow = 0, otherProcess = 0, unrecognizedAllocationEvents = 0;'),
        ('        if (e is not GCAllocationTickTraceData tick) continue;',
         '''        if (e.ProviderName == "Microsoft-Windows-DotNETRuntime" && (int)e.ID == 10 && e is not GCAllocationTickTraceData)
            unrecognizedAllocationEvents++;
        if (e is not GCAllocationTickTraceData tick) continue;'''),
        ('    decoderRuntime = Environment.Version.ToString(),',
         '''    decoderRuntime = Environment.Version.ToString(),
    decoderFramework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    decoderCorelibPath = typeof(object).Assembly.Location,
    decoderCorelibSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(object).Assembly.Location))).ToLowerInvariant(),
    unrecognizedAllocationEvents,'''),
    ]
    for old, new in edits:
        require(original.count(old) == 1, "Decoder overlay anchor changed")
        original = original.replace(old, new)
    return original


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--trace-tool-dll", required=True, help="installed pinned dotnet-trace9 tools/net8.0/any/dotnet-trace.dll")
    parser.add_argument("--build-only", action="store_true", help="build/proof only; no workload launch or capture")
    args = parser.parse_args()
    output = Path(args.output).resolve()
    require(not output.exists() or not any(output.iterdir()), "Use a fresh output directory")
    output.mkdir(parents=True, exist_ok=True)
    dotnet = shutil.which(args.dotnet)
    require(dotnet is not None, "dotnet executable missing")
    dotnet = str(Path(dotnet).resolve())
    tool_dll = Path(args.trace_tool_dll).resolve()
    require(tool_dll.is_file() and tool_dll.name == "dotnet-trace.dll", "Pinned trace-tool DLL missing")
    env = {key: value for key, value in os.environ.items() if not key.startswith(("DOTNET_", "COMPlus_", "ISSUE739_"))
           and key.upper() not in pilot.CREDENTIAL_ENVIRONMENT_NAMES}
    env.update(pilot.PINNED_ENV)
    env["DOTNET_ROOT"] = str(Path(dotnet).parent)
    commands, results, variants, copies = [], [], {}, {}
    deadline = time.monotonic() + 2400
    provenance = {"schemaVersion": 1, "status": "started", "sourceSha": pilot.SOURCE,
                  "sdkPin": pilot.SDK, "runtimePin": pilot.RUNTIME, "toolSdkPin": TOOL_SDK, "toolRuntimePin": TOOL_RUNTIME,
                  "dotnetTracePin": TRACE_VERSION, "traceEventPin": DECODER_PACKAGE, "interpretation": INTERPRETATION,
                  "plan": {"cases": CASES, "sequencePerCase": SEQUENCE, "workloadProcesses": 20, "captures": 8,
                           "operations": OPERATIONS, "warmup": WARMUP, "caseOrder": "TCP then SHM; complete sequence within each cell",
                           "firstCapture": "TCP B traced position3; compatibility must pass before continuing", "reruns": 0},
                  "environment": env | {}, "cpuInfo": Path("/proc/cpuinfo").read_text() if Path("/proc/cpuinfo").exists() else platform.processor(),
                  "os": platform.platform(), "totalBudgetSeconds": 2400, "captureSeconds": 120, "captureMiB": 256,
                  "stableAcceptance": False, "rawControlSubtraction": False, "outlierRemoval": "none", "nativeAot": "not attempted",
                  "driverBinaryPolicy": "one traditional marker-capable consumer compiled against A; same bytes reused with B dependencies; independent B artifacts archived",
                  "ownerRulesStatus": "frozen conservative concrete-object and traditional A state-machine rules; runtime-async B signatures left unknown until actual trace review; no old net10/main percentages reused",
                  "readerLoweringPolicy": "same retained pooled-builder annotation; actual exact-RC1 runtime-async lowering in B verified by unchanged metadata gate"}
    # Record tuning and removed credential NAMES only, never arbitrary environment values/secrets.
    provenance["environment"] = {key: env[key] for key in pilot.PINNED_ENV}
    provenance["credentialVariableNamesRemoved"] = sorted(key for key in os.environ if key.upper() in pilot.CREDENTIAL_ENVIRONMENT_NAMES)

    def command(argv, cwd, log_name, timeout=600, trace_mode=None):
        remaining = deadline - time.monotonic()
        require(remaining > timeout, "Total diagnostic budget exhausted before bounded command")
        entry = {"argv": [str(item) for item in argv], "cwd": str(cwd), "log": log_name,
                 "timeoutSeconds": min(timeout, remaining), "traceMode": trace_mode,
                 "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
        commands.append(entry)
        write_json(output / "commands.json", commands)
        active_env = env | ({"ISSUE739_TRACE": "1" if trace_mode else "0"} if trace_mode is not None else {})
        with open(output / log_name, "w") as log:
            try:
                result = subprocess.run(entry["argv"], cwd=cwd, env=active_env, stdout=log, stderr=subprocess.STDOUT,
                                        timeout=entry["timeoutSeconds"], check=False)
                entry["returnCode"] = result.returncode
            except subprocess.TimeoutExpired:
                entry["timedOut"] = True
                raise
            finally:
                write_json(output / "commands.json", commands)
        require(result.returncode == 0, "Command failed; inspect " + log_name)
        return (output / log_name).read_text()

    def toolchain(cwd, sdk, runtime, label):
        require(command([dotnet, "--version"], cwd, label + "-sdk-version.log").strip() == sdk, "SDK selection mismatch: " + label)
        sdk_listing = command([dotnet, "--list-sdks"], cwd, label + "-sdk-list.log")
        runtime_listing = command([dotnet, "--list-runtimes"], cwd, label + "-runtime-list.log")
        sdk_rows = [line for line in sdk_listing.splitlines() if line.startswith(sdk + " [")]
        runtime_rows = [line for line in runtime_listing.splitlines() if line.startswith("Microsoft.NETCore.App " + runtime + " [")]
        require(len(sdk_rows) == len(runtime_rows) == 1, "Pinned SDK/runtime unavailable or ambiguous: " + label)
        sdk_dir = Path(sdk_rows[0].split("[", 1)[1].rstrip("]")) / sdk
        runtime_dir = Path(runtime_rows[0].split("[", 1)[1].rstrip("]")) / runtime
        ref = sdk_dir.parent.parent / "packs/Microsoft.NETCore.App.Ref" / runtime
        require(ref.is_dir(), "Reference pack missing: " + label)
        return {"sdk": sdk, "runtime": runtime,
                "info": command([dotnet, "--info"], cwd, label + "-dotnet-info.log"),
                "compilerVersion": command([dotnet, "exec", "--fx-version", runtime, sdk_dir / "Roslyn/bincore/csc.dll", "-version"], cwd, label + "-compiler-version.log").strip(),
                "compilerHashes": manifest(sdk_dir / "Roslyn/bincore"), "runtimeHashes": manifest(runtime_dir), "referencePackHashes": manifest(ref)}

    try:
        pilot.assert_original(ROOT)
        names = pilot.tracked_inputs(ROOT)
        # Strengthen the source gate to every copied tracked input, including .editorconfig.
        for name in names:
            original = subprocess.check_output(["git", "show", pilot.SOURCE + ":" + name], cwd=ROOT)
            require(hashlib.sha256(original).hexdigest() == sha(ROOT / name), "Pinned source/build input drift: " + name)
        provenance["gitHead"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        provenance["originalHashes"] = {name: sha(ROOT / name) for name in names}
        harness_files = [path for base in (HERE, NET11, TRACE) for path in base.rglob("*")
                         if path.is_file() and not {"bin", "obj", "__pycache__", ".local-smoke"}.intersection(path.relative_to(base).parts)]
        provenance["harnessHashes"] = {str(path.relative_to(ROOT)): sha(path) for path in sorted(set(harness_files))}
        copies = {variant: output / "work" / variant for variant in ("A", "B")}
        provenance["overlays"] = {variant: project(ROOT, target, variant, names) for variant, target in copies.items()}
        for name in names:
            require(sha(copies["A"] / name) == sha(copies["B"] / name), "A/B production input drift: " + name)
        for name in ("Program.cs", "Markers.cs"):
            relative = NET11.relative_to(ROOT) / "Driver" / name
            require(sha(copies["A"] / relative) == sha(copies["B"] / relative), "A/B driver source drift")
        provenance["targetToolchain"] = toolchain(copies["A"], pilot.SDK, pilot.RUNTIME, "target")
        decoder = output / "work/decoder"
        decoder.mkdir(parents=True)
        write_json(decoder / "global.json", {"sdk": {"version": TOOL_SDK, "rollForward": "disable"}})
        shutil.copy2(HERE / "Decode/Decode.csproj", decoder / "Decode.csproj")
        (decoder / "Program.cs").write_text(decoder_source((TRACE / "Program.cs").read_text()))
        provenance["toolToolchain"] = toolchain(decoder, TOOL_SDK, TOOL_RUNTIME, "tool")
        provenance["traceToolBinaryHashes"] = manifest(tool_dll.parent)
        wrapper = output / "trace-tool-pinned.sh"
        wrapper.write_text("#!/bin/sh\nexec " + " ".join(shlex.quote(str(x)) for x in (dotnet, "exec", "--fx-version", TOOL_RUNTIME, tool_dll)) + ' "$@"\n')
        wrapper.chmod(0o755)
        version = command([wrapper, "--version"], decoder, "trace-tool-version.log", 30).strip()
        require(version.split("+", 1)[0] == TRACE_VERSION, "Actual collector version differs from pin")
        provenance["actualTraceToolVersion"] = version
        command([dotnet, "build", decoder / "Decode.csproj", "-c", "Release", "--nologo", "-v:diag", "-bl:" + str(output / "decoder.binlog")], decoder, "decoder-build.log")
        decode_bin = decoder / "bin/Release/net10.0"
        decoder_assets = json.loads((decoder / "obj/project.assets.json").read_text())
        require("Microsoft.Diagnostics.Tracing.TraceEvent/" + DECODER_PACKAGE in decoder_assets["libraries"], "Decoder package pin not resolved")
        provenance["decoderPackageGraph"] = pilot.package_manifest(decoder)
        provenance["decoderHashes"] = manifest(decode_bin)
        provenance["decoderSourceHash"] = sha(decoder / "Program.cs")
        provenance["decoderOriginalSourceHash"] = sha(TRACE / "Program.cs")
        write_json(output / "provenance.json", provenance)
        relative = NET11.relative_to(ROOT)
        for variant, target in copies.items():
            require(command([dotnet, "--version"], target, variant + "-sdk-version.log").strip() == pilot.SDK, "Variant SDK mismatch")
            projects = {"tiny": target / relative / "Driver/Driver.csproj", "metadata": target / relative / "Metadata/Metadata.csproj"}
            for label, path in projects.items():
                log = command([dotnet, "build", path, "-c", "Release", "--nologo", "-v:diag", "-bl:" + str(output / f"{variant}-{label}.binlog")], target, f"{variant}-{label}-build.log")
                lines = pilot.compiler_commands(log)
                require(lines, "Actual compiler commands missing: " + variant + "/" + label)
                (output / f"{variant}-{label}-compiler-commands.log").write_text("\n".join(lines) + "\n")
            directories = {label: path.parent / "bin/Release/net11.0" for label, path in projects.items()}
            if variant == "B":
                archived = output / "independent-B-driver-artifacts"
                archived.mkdir()
                b_files = {path.name for path in directories["tiny"].iterdir() if path.name in DRIVER_FILES}
                require(b_files >= {"SharpLink.Benchmarks.dll", "SharpLink.Benchmarks.pdb", "SharpLink.Benchmarks.deps.json", "SharpLink.Benchmarks.runtimeconfig.json"}, "B consumer artifact incomplete")
                dependencies = {path.name: sha(path) for path in directories["tiny"].iterdir() if path.is_file() and path.name not in DRIVER_FILES}
                for name in sorted(b_files):
                    shutil.copy2(directories["tiny"] / name, archived / name)
                provenance["independentBDriverHashes"] = manifest(archived)
                for name in ("SharpLink.Benchmarks.deps.json", "SharpLink.Benchmarks.runtimeconfig.json"):
                    require(sha(archived / name) == sha(variants["A"]["dirs"]["tiny"] / name), "A/B binding config differs")
                for name in sorted(b_files):
                    shutil.copy2(variants["A"]["dirs"]["tiny"] / name, directories["tiny"] / name)
                require(dependencies == {path.name: sha(path) for path in directories["tiny"].iterdir() if path.is_file() and path.name not in DRIVER_FILES}, "Fixed consumer copy changed B dependencies")
            variants[variant] = {"root": target, "dirs": directories, "driver": directories["tiny"] / "SharpLink.Benchmarks.dll"}
            provenance.setdefault("binaryHashes", {})[variant] = {label: manifest(directory) for label, directory in directories.items()}
            provenance.setdefault("packageGraphs", {})[variant] = pilot.package_manifest(target)
            provenance.setdefault("assetHashes", {})[variant] = {str(path.relative_to(target)): sha(path) for path in sorted(target.rglob("project.assets.json"))}
            provenance.setdefault("generatorHashes", {})[variant] = sha(target / "src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll")
            provenance.setdefault("generatedSourceHashes", {})[variant] = {str(path.relative_to(target)): sha(path) for path in sorted((target / relative / "Fixture/obj/generated").rglob("*.cs"))}
            assemblies = [directories["tiny"] / (name + ".dll") for name in pilot.PRODUCTION_PROJECTS + ["Fixture", "SharpLink.Benchmarks"]]
            command([dotnet, "exec", "--fx-version", pilot.RUNTIME, directories["metadata"] / "Metadata.dll", output / f"{variant}-metadata.json", *assemblies], target, f"{variant}-metadata.log", 60)
        require(provenance["packageGraphs"]["A"] == provenance["packageGraphs"]["B"], "Resolved A/B package graph differs")
        require(provenance["generatorHashes"]["A"] == provenance["generatorHashes"]["B"], "Generator binary differs")
        require(provenance["generatedSourceHashes"]["A"] and provenance["generatedSourceHashes"]["A"] == provenance["generatedSourceHashes"]["B"], "Generated Fixture source missing/different")
        proof = pilot.verify_metadata(*[json.loads((output / f"{variant}-metadata.json").read_text()) for variant in ("A", "B")])
        write_json(output / "lowering-proof.json", proof)
        rules = json.loads((HERE / "owners.json").read_text())
        shutil.copy2(HERE / "owners.json", output / "owners.json")
        provenance["ownerRulesSha256"] = sha(output / "owners.json")
        provenance["status"] = "lowering-verified-before-sampling"
        write_json(output / "provenance.json", provenance)
        if not args.build_only:
            for case in CASES:
                for position, (variant, traced) in enumerate(SEQUENCE, 1):
                    data = variants[variant]
                    sample = f"{case}-{position:02}-{variant}-" + ("traced" if traced else "untraced")
                    destination = output / (sample + ".sample.json")
                    argv = [dotnet, "exec", "--fx-version", pilot.RUNTIME, data["driver"], case.split("-")[0], "add", str(OPERATIONS), str(WARMUP), sample, destination, pilot.ENVIRONMENT_VERSION, "1"]
                    trace = output / (sample + ".nettrace")
                    if traced:
                        command([sys.executable, TRACE / "collect.py", "--trace-tool", wrapper, "--output", trace, "--seconds", "120", "--max-mib", "256", "--", *argv], data["root"], sample + ".launch.log", 200, True)
                        require(trace.is_file() and 0 < trace.stat().st_size <= 256 * 1024 * 1024, "Trace artifact size bound failed")
                    else:
                        command(argv, data["root"], sample + ".launch.log", 60, False)
                    row = json.loads(destination.read_text())
                    validate_sample(row, case, sample, traced, proof["driverSha256"], provenance["targetToolchain"]["runtimeHashes"]["System.Private.CoreLib.dll"])
                    require(row["processId"] not in {prior["processId"] for prior in results}, "Workload PID reused")
                    if traced:
                        prefix = output / "work/decode-results" / sample
                        command([dotnet, "exec", "--fx-version", TOOL_RUNTIME, decode_bin / "Decode.dll", trace, sample, prefix], decoder, sample + ".decode.log", 120)
                        for suffix in (".decode.json", ".ticks.jsonl"):
                            source = Path(str(prefix) + suffix)
                            require(source.stat().st_size <= 512 * 1024 * 1024, "Decoded artifact size bound failed")
                            shutil.copy2(source, output / (sample + suffix))
                        meta_path, ticks_path = output / (sample + ".decode.json"), output / (sample + ".ticks.jsonl")
                        meta = json.loads(meta_path.read_text())
                        with open(ticks_path) as raw:
                            ticks = [json.loads(line) for line in raw]
                        capture_path = Path(str(trace) + ".capture.json")
                        capture = json.loads(capture_path.read_text())
                        # Retain conservative loss/missing/overcapture report before compatibility gate.
                        trace_summary = summary.summarize(meta, row, ticks, rules)
                        trace_summary["evidence"] = {"traceSha256": sha(trace), "decodeSha256": sha(meta_path), "ticksSha256": sha(ticks_path), "sampleSha256": sha(destination), "rulesSha256": provenance["ownerRulesSha256"], "captureSha256": sha(capture_path)}
                        trace_summary["capture"] = capture
                        trace_summary["markerWindow"] = {key: meta[key] for key in ("markerStartMilliseconds", "markerStopMilliseconds", "markerDurationMilliseconds", "markerBoundary")}
                        trace_summary["workloadContext"].update(case=case, variant=variant, traceEnabled=True, markerWarmupCompleted=row["markerWarmupCompleted"])
                        trace_summary["interpretation"] = INTERPRETATION
                        write_json(output / (sample + ".summary.json"), trace_summary)
                        compatible = validate_trace(meta, row, ticks, capture, sha(trace), provenance["toolToolchain"]["runtimeHashes"]["System.Private.CoreLib.dll"])
                        write_json(output / (sample + ".compatibility.json"), compatible)
                        if not (output / "first-capture-compatibility.json").exists():
                            write_json(output / "first-capture-compatibility.json", compatible)
                    results.append({**row, "case": case, "variant": variant, "position": position,
                                    "group": "same-binary-AA" if position > 8 else "diagnostic-2x2", "sampleSha256": sha(destination)})
                    write_json(output / "completed-samples.json", results)
                    print(sample, f'{row["bytesPerOperation"]:.6f} B/op', flush=True)
            require(len(results) == 20 and sum(row["traceEnabled"] for row in results) == 8, "Incomplete preregistered diagnostic plan")
            final_summary = {"schemaVersion": 1, "status": "complete-diagnostic-only", "sourceSha": pilot.SOURCE,
                       "interpretation": INTERPRETATION, "samples": 20, "captures": 8, "stableAcceptance": False,
                       "cases": [contrasts([row for row in results if row["case"] == case]) for case in CASES]}
        pilot.assert_original(ROOT)
        require(provenance["originalHashes"] == {name: sha(ROOT / name) for name in names}, "Original input changed during run")
        for variant, data in variants.items():
            for label, directory in data["dirs"].items():
                require(manifest(directory) == provenance["binaryHashes"][variant][label], "Executed binaries changed after proof")
        require(manifest(decode_bin) == provenance["decoderHashes"], "Decoder binaries changed")
        require(manifest(tool_dll.parent) == provenance["traceToolBinaryHashes"], "Collector binaries changed")
        provenance["status"] = "build-proof-only-no-captures" if args.build_only else "complete-diagnostic-only"
        if not args.build_only:
            write_json(output / "summary.json", final_summary)
    except Exception as error:
        provenance["status"] = "failed-closed"
        provenance["failure"] = str(error)
        provenance["completedSamples"] = len(results)
        raise
    finally:
        try:
            for variant, target in copies.items():
                for name in list(provenance.get("overlays", {}).get(variant, {})) + ["Directory.Packages.props", ".editorconfig"]:
                    source = target / name
                    if source.is_file():
                        destination = output / "build-overlays" / variant / name
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(source, destination)
                for label, source in (("tiny", target / NET11.relative_to(ROOT) / "Driver/bin/Release/net11.0"),
                                      ("metadata", target / NET11.relative_to(ROOT) / "Metadata/bin/Release/net11.0"),
                                      ("generator", target / "src/SharpLink.Generator/bin/Release/netstandard2.0")):
                    if source.is_dir():
                        shutil.copytree(source, output / "executed-binaries" / variant / label, dirs_exist_ok=True)
                generated = target / NET11.relative_to(ROOT) / "Fixture/obj/generated"
                if generated.is_dir():
                    shutil.copytree(generated, output / "generated-fixture" / variant, dirs_exist_ok=True)
            decoder = output / "work/decoder"
            if decoder.is_dir():
                for name in ("Program.cs", "Decode.csproj", "global.json"):
                    if (decoder / name).is_file():
                        destination = output / "build-overlays/decoder" / name
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(decoder / name, destination)
                if (decoder / "bin/Release/net10.0").is_dir():
                    shutil.copytree(decoder / "bin/Release/net10.0", output / "executed-binaries/decoder", dirs_exist_ok=True)
            # Export actual tool payload without package caches; no authentication material.
            shutil.copytree(tool_dll.parent, output / "executed-binaries/collector", dirs_exist_ok=True)
            provenance["exportedBinaryHashes"] = manifest(output / "executed-binaries")
            provenance["exportStatus"] = "available evidence exported; authoritative status determines whether proof/capture completed"
        except Exception as error:
            provenance["status"] = "failed-closed"
            provenance["artifactCollectionFailure"] = str(error)
            raise
        finally:
            write_json(output / "provenance.json", provenance)


if __name__ == "__main__":
    main()
