#!/usr/bin/env python3
"""One bounded NativeAOT feasibility pilot. No stable/JIT-parity/nonregression claim."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import shlex
import shutil
import subprocess
import time

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("issue739_jit", HERE.parent / "run_pilot.py")
jit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(jit)
require, sha, write_json = jit.require, jit.sha, jit.write_json
RID = "linux-x64"
BUDGET = 1380
CASES = [("reader", name) for name in jit.READER_CASES] + [("tiny", "tcp-add-c1"), ("tiny", "tcp-control-c1")]
SEQUENCE = [("ABBA", "A"), ("ABBA", "B"), ("ABBA", "B"), ("ABBA", "A"), ("AA", "A"), ("AA", "A")]
OPTIONS = ["-c", "Release", "-r", RID, "--self-contained", "true",
           "-p:RuntimeFrameworkVersion=" + jit.RUNTIME, "-p:IlcOptimizationPreference=Speed",
           "-p:StripSymbols=false", "-p:IlcTreatWarningsAsErrors=true", "-p:TrimmerSingleWarn=false"]


def copy_fixed_consumer(source, destination):
    # Keep A's bytes, not A's older timestamp: native publish still traverses
    # CoreCompile, whose B-generated inputs must not look newer than this PDB/DLL.
    shutil.copyfile(source, destination)


def assert_copy_origins():
    origins = json.loads((HERE / "copy-origins.json").read_text())
    for name, digest in origins["originSha256"].items():
        require(sha(HERE.parent / name) == digest, "Copy origin drift: " + name)
    # These are the exact source mechanics, not a lookalike calibration.
    original = (HERE.parent / "ReaderControl/Program.cs").read_text()
    copied = (HERE / "ReaderCases.cs").read_text()
    marker = "    private static void WriteCounts"
    require(original[original.index(marker):] == copied[copied.index(marker):], "Reader mechanics changed")
    original = (HERE.parent / "Driver/Program.cs").read_text()
    copied = (HERE / "TinyCases.cs").read_text()
    for begin, end in (("        var service =", "        var result = new"),
                       ("        async Task Worker", None)):
        old = original[original.index(begin):original.index(end) if end else None]
        new = copied[copied.index(begin):copied.index("        using (var output =") if end else None]
        require(old == new, "Tiny measurement mechanics changed")
    return origins


def validate_native_sample(row, category, case, sample, operations, warmup, executable):
    jit.validate_sample(row, category, case, sample, operations, warmup, sha(executable))
    require(row["executionMode"] == "nativeaot" and row["dynamicCodeSupported"] is False
            and row["dynamicCodeCompiled"] is False, "JIT fallback or dynamic code detected")
    require(row["nativeImageFormat"] == "ELF64-x86-64" and Path(row["processPath"]).resolve() == executable.resolve(),
            "Executed native image identity mismatch")
    require(row["nativeImageSha256"] == sha(executable), "External native image SHA mismatch")
    if category == "reader":
        width = row["burstWidth"]
        expected_wraps = ((operations + warmup) // (width * 65536) - warmup // (width * 65536)) * width
        require(row["tokenWraps"] == expected_wraps and row["warmupCounts"]["tokenWraps"] == warmup // (width * 65536) * width,
                "Reader source version-wrap diagnostic mismatch")
    return row


def elf_identity(path):
    header = path.read_bytes()[:20]
    require(len(header) == 20 and header[:6] == b"\x7fELF\x02\x01" and header[18:20] == b"\x3e\x00", "Native image is not ELF64 x86-64")
    return {"path": str(path), "sha256": sha(path), "bytes": path.stat().st_size, "format": "ELF64-x86-64"}


def capture_ilc_inputs(project, expected):
    paths = list((project.parent / "obj/Release/net11.0" / RID).rglob("*.ilc.rsp"))
    require(len(paths) == 1, "Expected one actual ILC response file; inspect publish binlog")
    response = paths[0]
    inputs = []
    for token in shlex.split(response.read_text()):
        token = re.sub(r"^(?:-r:|--reference:)", "", token)
        if not token.endswith(".dll"):
            continue
        path = Path(token)
        if not path.is_absolute():
            path = project.parent / path
        require(path.is_file(), "Unresolved actual ILC input " + str(path))
        inputs.append({"path": str(path.resolve()), "name": path.name, "sha256": sha(path)})
    for name, digest in expected.items():
        selected = [row for row in inputs if row["name"] == name]
        require(selected and all(row["sha256"] == digest for row in selected), "ILC did not consume proven bytes for " + name)
    snapshots = []
    for phase in ("before", "after"):
        snapshot = project.parent / "obj/Release/net11.0" / RID / ("issue739-ilc-inputs-" + phase + ".txt")
        require(snapshot.is_file(), "Missing " + phase + " ILC input snapshot")
        mapping = {}
        for line in snapshot.read_text(encoding="utf-8-sig").splitlines():
            path, digest = line.rsplit("|", 1)
            mapping[str(Path(path).resolve())] = digest.lower()
        require(mapping, "Empty ILC input snapshot")
        snapshots.append(mapping)
    require(snapshots[0] == snapshots[1], "ILC inputs changed during native compilation")
    require(all(snapshots[0].get(row["path"]) == row["sha256"] for row in inputs),
            "ILC response references an input not proved unchanged before/after compilation")
    return {"responsePath": str(response), "responseSha256": sha(response), "inputs": inputs}


def selected_native_tools(publish, inputs, packages, clang, linker):
    calls = re.findall(r'(?:^|\s)"?(/[^"\s]+/ilc)"?\s+([^\n]+)', publish, re.MULTILINE)
    calls = [(path, arguments) for path, arguments in calls if Path(inputs["responsePath"]).name in arguments]
    require(calls, "Actual ILC command could not be identified")
    paths = {str(Path(path).resolve()) for path, _ in calls}
    require(len(paths) == 1, "ILC executable ambiguous")
    path = Path(paths.pop())
    package = Path(packages["runtime.linux-x64.microsoft.dotnet.ilcompiler"]["path"]).resolve()
    require(path.is_relative_to(package), "Actual ILC executable is outside exact pinned compiler package")
    # BCL/private runtime inputs must come from the selected exact-version AOT/runtime packages.
    roots = [Path(item["path"]).resolve() for item in packages.values()]
    runtime_inputs = [row for row in inputs["inputs"] if row["name"] == "System.Private.CoreLib.dll"]
    require(len(runtime_inputs) == 1 and any(Path(runtime_inputs[0]["path"]).is_relative_to(root) for root in roots),
            "Actual AOT CoreLib input is outside pinned runtime packages")
    links = [line.strip() for line in publish.splitlines() if clang in line and "-fuse-ld=bfd" in line and " -o " in line]
    require(links, "Actual pinned clang/bfd link invocation not retained")
    return {"ilcPath": str(path), "ilcSha256": sha(path), "ilcCommands": [path + " " + arguments for path, arguments in calls],
            "corelibInput": runtime_inputs[0], "linkCommands": links, "linkerResolvedByClang": linker}


def export_evidence(output, copies, data, manifest, relative):
    exported = {}
    def copy(source, destination):
        if source.is_file():
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
            exported[str(destination.relative_to(output))] = sha(destination)
    for variant, target in copies.items():
        proof = output / "proof" / variant
        for path in target.rglob("project.assets.json"):
            copy(path, proof / "restore" / path.relative_to(target))
        for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"):
            copy(target / name, proof / "overlays" / name)
        generated = target / relative.parent / "Fixture/obj/generated"
        for path in generated.rglob("*.cs"):
            copy(path, proof / "generated-fixture" / path.relative_to(generated))
        aot = target / relative
        for path in (aot / "obj").rglob("*.rsp"):
            copy(path, proof / "response-files" / path.relative_to(aot))
        for path in (aot / "obj").rglob("issue739-ilc-inputs-*.txt"):
            copy(path, proof / "input-snapshots" / path.name)
        for name in jit.PRODUCTION_PROJECTS + ["Fixture", "SharpLink.Benchmarks"]:
            path = aot / "bin/Release/net11.0" / RID / (name + ".dll")
            copy(path, proof / "pre-aot-bin" / path.name)
        for path in (aot / "obj/Release/net11.0" / RID).glob("SharpLink.Benchmarks.*"):
            if path.suffix in (".dll", ".pdb"):
                copy(path, proof / "pre-aot-obj" / path.name)
        for row in manifest.get("ilcInputs", {}).get(variant, {}).get("inputs", []):
            path = Path(row["path"])
            if path.is_relative_to(target):
                copy(path, proof / "consumed-project-il" / path.relative_to(target))
    manifest["exportedEvidenceHashes"] = exported


def aot_packages(target):
    assets = list(target.rglob("project.assets.json"))
    folders = set()
    for path in assets:
        folders.update(json.loads(path.read_text()).get("packageFolders", {}))
    identities = {}
    # AOT packages are allowed to be restore download dependencies, not ordinary libraries.
    for folder in sorted(folders):
        root = Path(folder)
        for package in sorted(root.glob("*")):
            name = package.name.lower()
            if "ilcompiler" not in name and not name.startswith("microsoft.netcore.app.runtime."):
                continue
            version = package / jit.RUNTIME
            if version.is_dir():
                identities[name] = {"version": jit.RUNTIME, "path": str(version), "files": jit.binary_manifest(version)}
    require("microsoft.dotnet.ilcompiler" in identities, "Exact Microsoft.DotNet.ILCompiler package missing")
    require("runtime.linux-x64.microsoft.dotnet.ilcompiler" in identities, "Exact host ILCompiler package missing")
    require(any(name.startswith("microsoft.netcore.app.runtime.") for name in identities), "Exact NativeAOT/runtime pack missing")
    return identities


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    root = HERE.parents[4]
    output = Path(args.output).resolve()
    require(not output.exists() or not any(output.iterdir()), "Use a fresh output directory")
    output.mkdir(parents=True, exist_ok=True)
    deadline = time.monotonic() + BUDGET
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(("DOTNET_", "COMPlus_", "ISSUE739_")) and key.upper() not in jit.CREDENTIAL_ENVIRONMENT_NAMES}
    env.update({"DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
                "DOTNET_ROLL_FORWARD": "Disable", "DOTNET_gcServer": "0", "COMPlus_gcServer": "0", "ISSUE739_SOURCE_SHA": jit.SOURCE})
    if "DOTNET_ROOT" in os.environ:
        env["DOTNET_ROOT"] = os.environ["DOTNET_ROOT"]
    commands, results, copies, data = [], [], {}, {}
    relative = HERE.relative_to(root)
    manifest = {"schemaVersion": 1, "status": "started", "sourceSha": jit.SOURCE, "sdkPin": jit.SDK, "runtimePin": jit.RUNTIME,
                "rid": RID, "publishOptions": OPTIONS, "totalScriptBudgetSeconds": BUDGET,
                "samplePlan": {"cases": CASES, "sequence": SEQUENCE, "expectedSamples": 48,
                               "readerOperations": 131072, "readerWarmup": 16384, "tinyOperations": 8192, "tinyWarmup": 2048},
                "environment": env | {}, "credentialVariableNamesRemoved": sorted(key for key in os.environ if key.upper() in jit.CREDENTIAL_ENVIRONMENT_NAMES),
                "interpretation": "NativeAOT feasibility only; fixed traditional measurement source and IL, independently compiled native images; no stability, nonregression, cancellation/fault, JIT parity or end-to-end owner-closure claim",
                "fixedConsumerPolicy": "A consumer DLL/PDB in both bin and RID-specific obj used by --no-build publish; independently built B consumer retained; actual ILC response inputs must match metadata-proven hashes",
                "readerPolicy": "exact RC1 A traditional pooled builder, B runtime-async with unchanged builder attribute/source/signature; no assumed opt-out",
                "warningPolicy": "No trimming/IL warnings suppressed; all warnings remain errors; no reflection fallback",
                "subtractionApplied": False, "outlierRemoval": "none", "os": platform.platform(),
                "packageEvidenceLimit": "Exact selected package/compiler/reference-pack versions and file hashes retained; package bytes are not duplicated in proof/"}
    # Never export the full inherited environment (it can contain unrelated secrets).
    manifest["environment"] = {key: env[key] for key in env if key.startswith(("DOTNET_", "COMPlus_", "ISSUE739_"))}

    def save():
        write_json(output / "provenance.json", manifest)

    def command(argv, cwd, name, timeout=600):
        remaining = deadline - time.monotonic()
        require(remaining > 1, "NativeAOT total time budget exhausted")
        entry = {"argv": [str(x) for x in argv], "cwd": str(cwd), "log": name,
                 "timeoutSeconds": min(timeout, remaining), "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
        commands.append(entry)
        write_json(output / "commands.json", commands)
        with (output / name).open("w") as stream:
            try:
                result = subprocess.run(entry["argv"], cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT,
                                        timeout=entry["timeoutSeconds"], check=False)
                entry["returnCode"] = result.returncode
            except subprocess.TimeoutExpired:
                entry["timedOut"] = True
                raise
            finally:
                write_json(output / "commands.json", commands)
        require(result.returncode == 0, "Command failed; exact toolchain/support gap retained in " + name)
        return (output / name).read_text()

    try:
        require(platform.system() == "Linux" and platform.machine() == "x86_64", "Linux x64 host required")
        jit.assert_original(root)
        names = jit.tracked_inputs(root)
        manifest["originalHashes"] = {name: sha(root / name) for name in names}
        manifest["copyOrigins"] = assert_copy_origins()
        manifest["harnessHashes"] = {str(path.relative_to(root)): sha(path) for path in HERE.parent.rglob("*")
            if path.is_file() and not {"bin", "obj", "__pycache__"}.intersection(path.relative_to(HERE.parent).parts)}
        manifest["gitHead"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
        copies = {variant: output / "work" / variant for variant in ("A", "B")}
        manifest["overlays"] = {variant: jit.make_copy(root, HERE.parent, target, variant, names) for variant, target in copies.items()}
        for name in names:
            require(sha(copies["A"] / name) == sha(copies["B"] / name), "Source/config differs across arms: " + name)
        aroot = copies["A"]
        require(command([args.dotnet, "--version"], aroot, "sdk-version.log").strip() == jit.SDK, "Exact SDK not selected")
        manifest["dotnetInfo"] = command([args.dotnet, "--info"], aroot, "dotnet-info.log")
        sdk_lines = command([args.dotnet, "--list-sdks"], aroot, "sdk-list.log").splitlines()
        selected = [line for line in sdk_lines if line.startswith(jit.SDK + " [")]
        require(len(selected) == 1, "SDK installation ambiguous")
        sdk = Path(selected[0].split("[", 1)[1].rstrip("]")) / jit.SDK
        framework_lines = command([args.dotnet, "--list-runtimes"], aroot, "runtime-list.log").splitlines()
        selected = [line for line in framework_lines if line.startswith("Microsoft.NETCore.App " + jit.RUNTIME + " [")]
        require(len(selected) == 1, "Metadata inspector exact shared runtime missing")
        framework = Path(selected[0].split("[", 1)[1].rstrip("]")) / jit.RUNTIME
        manifest["compilerVersion"] = command([args.dotnet, "exec", sdk / "Roslyn/bincore/csc.dll", "-version"], aroot, "compiler-version.log").strip()
        manifest["compilerHashes"] = jit.binary_manifest(sdk / "Roslyn/bincore")
        manifest["inspectorRuntimeHashes"] = jit.binary_manifest(framework)
        refpack = sdk.parent.parent / "packs/Microsoft.NETCore.App.Ref" / jit.RUNTIME
        require(refpack.is_dir(), "Exact reference pack missing")
        manifest["referencePackHashes"] = jit.binary_manifest(refpack)
        manifest["nativeTools"] = {}
        for name in ("clang", "ld"):
            executable = shutil.which(name, path=env.get("PATH"))
            require(executable, "Required native tool missing: " + name)
            manifest["nativeTools"][name] = {"path": executable, "resolvedPath": str(Path(executable).resolve()), "sha256": sha(executable),
                "version": command([executable, "--version"], aroot, name + "-version.log")}
        # Pin the actual compiler/linker choice and retain their invocations in diagnostic logs.
        native_options = ["-p:CppCompilerAndLinker=" + manifest["nativeTools"]["clang"]["path"], "-p:LinkerFlavor=bfd"]
        linker = command([manifest["nativeTools"]["clang"]["path"], "-print-prog-name=ld.bfd"], aroot, "clang-linker-resolution.log").strip()
        if not Path(linker).is_absolute():
            linker = shutil.which(linker, path=env.get("PATH"))
        require(linker and Path(linker).is_file(), "Pinned clang could not resolve ld.bfd")
        manifest["nativeTools"]["selectedLinker"] = {"path": linker, "resolvedPath": str(Path(linker).resolve()),
            "sha256": sha(linker), "version": command([linker, "--version"], aroot, "selected-linker-version.log")}
        manifest["nativeToolOptions"] = native_options
        save()
        for variant, target in copies.items():
            project = target / relative / "Aot.csproj"
            build = command([args.dotnet, "build", project, *OPTIONS, *native_options, "--nologo", "-v:diag",
                "-bl:" + str(output / (variant + "-managed.binlog"))], target, variant + "-managed-build.log")
            compiler = jit.compiler_commands(build)
            require(compiler, "No actual managed compiler invocation")
            require(not any("ISSUE739_LOCAL" in line for line in compiler), "Local-only compiler symbol detected")
            (output / (variant + "-compiler-commands.log")).write_text("\n".join(compiler) + "\n")
            directory = project.parent / "bin/Release/net11.0" / RID
            obj = project.parent / "obj/Release/net11.0" / RID
            metadata_project = target / relative.parent / "Metadata/Metadata.csproj"
            command([args.dotnet, "build", metadata_project, "-c", "Release", "--nologo", "-v:diag"], target, variant + "-metadata-build.log")
            if variant == "B":
                saved = output / "independent-B-consumer"
                for location in ("bin", "obj"):
                    source_dir = directory if location == "bin" else obj
                    source_a = data["A"][location]
                    for name in ("SharpLink.Benchmarks.dll", "SharpLink.Benchmarks.pdb"):
                        destination = saved / location / name
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(source_dir / name, destination)
                        copy_fixed_consumer(source_a / name, source_dir / name)
                for name in ("SharpLink.Benchmarks.deps.json", "SharpLink.Benchmarks.runtimeconfig.json"):
                    require(sha(directory / name) == sha(data["A"]["bin"] / name), "Consumer binding configuration differs")
            expected = {name + ".dll": sha(directory / (name + ".dll")) for name in jit.PRODUCTION_PROJECTS + ["Fixture", "SharpLink.Benchmarks"]}
            require(sha(obj / "SharpLink.Benchmarks.dll") == expected["SharpLink.Benchmarks.dll"], "Consumer bin/obj drift")
            data[variant] = {"project": project, "root": target, "bin": directory, "obj": obj, "expected": expected}
            inspector = metadata_project.parent / "bin/Release/net11.0/Metadata.dll"
            command([args.dotnet, "exec", "--fx-version", jit.RUNTIME, inspector, output / (variant + "-metadata.json"),
                     *[directory / name for name in expected]], target, variant + "-metadata.log", 60)
            manifest.setdefault("packageGraphs", {})[variant] = jit.package_manifest(target)
            manifest.setdefault("aotPackages", {})[variant] = aot_packages(target)
            manifest.setdefault("generatedSourceHashes", {})[variant] = {str(path.relative_to(target)): sha(path)
                for path in (target / relative.parent / "Fixture/obj/generated").rglob("*.cs")}
            manifest.setdefault("generatorHashes", {})[variant] = sha(target / "src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll")
            manifest.setdefault("preAotHashes", {})[variant] = expected
            save()
        require(manifest["packageGraphs"]["A"] == manifest["packageGraphs"]["B"], "Resolved package graph differs")
        require(manifest["aotPackages"]["A"] == manifest["aotPackages"]["B"], "Native toolchain packages differ")
        require(manifest["generatorHashes"]["A"] == manifest["generatorHashes"]["B"], "Generator bytes differ")
        require(manifest["generatedSourceHashes"]["A"] and manifest["generatedSourceHashes"]["A"] == manifest["generatedSourceHashes"]["B"], "Generated source differs or absent")
        proof = jit.verify_metadata(*[json.loads((output / (variant + "-metadata.json")).read_text()) for variant in ("A", "B")])
        write_json(output / "lowering-proof.json", proof)
        manifest["status"] = "pre-aot-lowering-verified"
        save()
        for variant, item in data.items():
            native = output / "native" / variant
            publish = command([args.dotnet, "publish", item["project"], *OPTIONS, *native_options,
                "--no-build", "--no-restore", "-o", native, "--nologo", "-v:diag", "-bl:" + str(output / (variant + "-publish.binlog"))],
                item["root"], variant + "-publish.log")
            require(not jit.compiler_commands(publish), "Publish unexpectedly recompiled managed consumer/dependencies")
            # Source identity is checked independently from actual ILC response-file identity.
            require(item["expected"] == {name: sha(item["bin"] / name) for name in item["expected"]}, "Publish changed pre-AOT IL")
            require(sha(item["obj"] / "SharpLink.Benchmarks.dll") == proof["driverSha256"], "Publish changed fixed consumer input")
            manifest.setdefault("ilcInputs", {})[variant] = capture_ilc_inputs(item["project"], item["expected"])
            manifest.setdefault("selectedNativeTools", {})[variant] = selected_native_tools(publish,
                manifest["ilcInputs"][variant], manifest["aotPackages"][variant],
                manifest["nativeTools"]["clang"]["path"], manifest["nativeTools"]["selectedLinker"])
            rsp = manifest["ilcInputs"][variant]["responsePath"]
            require(any("ilc" in line.lower() and (str(Path(rsp).name) in line or str(Path(rsp)) in line)
                        for line in publish.splitlines()), "Actual ILC invocation not retained")
            item["native"] = native / "SharpLink.Benchmarks"
            manifest.setdefault("nativeImages", {})[variant] = elf_identity(item["native"])
            manifest.setdefault("nativeOutputHashes", {})[variant] = jit.binary_manifest(native)
            save()
        manifest["nativeImagesDiffer"] = sha(data["A"]["native"]) != sha(data["B"]["native"])
        manifest["nativeImageDifferencePolicy"] = "Expected, recorded, not required as proof; optimized native images need not reflect every IL difference"
        manifest["status"] = "aot-inputs-and-images-verified-before-sampling"
        save()
        for position, (group, variant) in enumerate(SEQUENCE, 1):
            for category, case in CASES if position % 2 else reversed(CASES):
                item = data[variant]
                operations, warmup = (131072, 16384) if category == "reader" else (8192, 2048)
                sample = f"{category}-{case}-{group}-{position}-{variant}"
                destination = output / (sample + ".sample.json")
                argv = [item["native"], *(case.split("-")[:2] if category == "tiny" else [case]),
                        str(operations), str(warmup), sample, destination, jit.ENVIRONMENT_VERSION]
                if category == "tiny":
                    argv.append("1")
                command(argv, item["root"], sample + ".log", 60)
                row = validate_native_sample(json.loads(destination.read_text()), category, case, sample, operations, warmup, item["native"])
                require(row["processId"] not in {prior["processId"] for prior in results}, "Sample PID reused")
                results.append(row | {"category": category, "case": case, "variant": variant, "group": group,
                                      "position": position, "sampleSha256": sha(destination)})
                write_json(output / "completed-samples.json", results)
                print(sample, f'{row["bytesPerOperation"]:.6f} B/op', flush=True)
        require(len(results) == 48, "Incomplete 48-record feasibility pilot")
        jit.assert_original(root)
        require(manifest["originalHashes"] == {name: sha(root / name) for name in names}, "Original source changed")
        for variant, item in data.items():
            require(jit.binary_manifest(item["native"].parent) == manifest["nativeOutputHashes"][variant], "Native outputs changed")
            require(capture_ilc_inputs(item["project"], item["expected"]) == manifest["ilcInputs"][variant], "ILC inputs changed")
        manifest["status"] = "complete-nativeaot-feasibility-only"
        summary = {"schemaVersion": 1, "status": manifest["status"], "samples": len(results), "stableAcceptance": False,
                   "sourceSha": jit.SOURCE, "interpretation": manifest["interpretation"],
                   "cases": [jit.summarize_cell([row for row in results if row["case"] == case], category, case, "pilot", SEQUENCE)
                             for category, case in CASES]}
        write_json(output / "summary.json", summary)
    except Exception as error:
        manifest["status"] = "failed-closed"
        manifest["failure"] = str(error)
        manifest["completedSamples"] = len(results)
        raise
    finally:
        try:
            export_evidence(output, copies, data, manifest, relative)
        except Exception as error:
            manifest["status"] = "failed-closed"
            manifest["evidenceExportFailure"] = str(error)
            raise
        finally:
            manifest["retainedEvidencePolicy"] = "proof/ retains actual consumed project IL plus pre-AOT inputs even on failure, snapshots, restore assets, overlays, generated sources and response files. Full logs/binlogs and raw samples retained at output root; native images retained separately in native/. work/ may be excluded from uploads."
            save()


if __name__ == "__main__":
    main()
