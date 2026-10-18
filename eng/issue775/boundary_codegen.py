#!/usr/bin/env python3
"""Fixed-source scalar resolution codegen; no profiling/diagnostic timing enters acceptance."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess

from run import BENCHMARK, COMMON_INPUTS, OVERLAY, PROJECT, digest, execute, git, save

OLD = "a473f5d3a50d3b23874f8b1bb72f46e65ff939d1"
PREFIX = "src/SharpLink.Server/SharpLinkServer."
PRODUCTION = {PREFIX + name + ".cs" for name in ("StaticBinding", "InvocationDispatch", "AdmissionDispatch")}
FILTER = "*SharpLinkServer:DispatchRpcAsync *SharpLinkServer:DispatchOneWayRpc *SharpLinkServer:ResolveStaticRequest"
CASES = [(transport, shape) for transport in ("tcp", "sharedmemory") for shape in ("unary", "oneway")]


def verify_source(old, candidate):
    changed = set(git(candidate, "diff", "--name-only", OLD, "HEAD", "--", "src").splitlines())
    if changed != PRODUCTION:
        raise ValueError(f"Unexpected production scope: {changed}")
    # Exact allowed transformation, including reattaching Unary method facts after decode.
    # Registration gates, resources, deadlines and error branches otherwise stay unchanged.
    for relative in PRODUCTION:
        text = (old / relative).read_text()
        if relative.endswith("StaticBinding.cs"):
            text = text.replace("ProtocolV2FrameFlags flags, ref ServerRequestEnvelope request, out ServiceRegistration registration)",
                "ProtocolV2FrameFlags flags, ulong bindingId, long methodId,\n        out ServiceRegistration registration, out StaticBindingMethod method)")
            text = text.replace("connection.StaticBindings.Resolve(unchecked((ulong)request.InterfaceHash),\n            request.MethodHash, flags, out registration, out var method)",
                "connection.StaticBindings.Resolve(bindingId, methodId, flags, out registration, out method)")
            text = text.replace("        if (failure == StaticBindingFailure.None) request = request with { BoundMethod = method };\n", "")
        else:
            text = text.replace("ResolveStaticRequest(connection, flags, ref request, out var serviceInfo)",
                "ResolveStaticRequest(connection, flags, unchecked((ulong)request.InterfaceHash),\n                request.MethodHash, out var serviceInfo, out var boundMethod)")
            if relative.endswith("InvocationDispatch.cs"):
                text = text.replace("            if (!serviceInfo.AcceptsCalls)",
                    "#if SHARPLINK_STATIC_BINDING_PROTOTYPE\n            request = request with { BoundMethod = boundMethod };\n#endif\n            if (!serviceInfo.AcceptsCalls)", 1)
            else:
                text = text.replace("            var descriptor = request.BoundMethod!.Descriptor;",
                    "            var descriptor = boundMethod.Descriptor;", 1)
                text = text.replace("request.BoundMethod!", "boundMethod")
        if relative.endswith("InvocationDispatch.cs"):
            anchor = "                        request = ReadRequestEnvelope(\n                            session, payload, flags, request.RpcDeadline);"
            text = text.replace(anchor, anchor + "\n#if SHARPLINK_STATIC_BINDING_PROTOTYPE\n                        request = request with { BoundMethod = boundMethod };\n#endif")
        if text != (candidate / relative).read_text():
            raise ValueError(f"Unexpected semantic edits: {relative}")
    hashes = {}
    for relative in COMMON_INPUTS + OVERLAY + ("src/SharpLink.Server/ServerRequestEnvelopeReader.cs",):
        if (old / relative).read_bytes() != (candidate / relative).read_bytes():
            raise ValueError(f"Changed original harness/policy/reader: {relative}")
        hashes[relative] = digest(old / relative)
    return hashes


def overlay(root, driver, output):
    relative = "test/SharpLink.Benchmarks/Program.cs"
    program = root / relative
    anchor = "    public static async Task Main(string[] args)\n    {\n"
    text = program.read_text()
    if text.count(anchor) != 1:
        raise ValueError("Program overlay anchor not unique")
    hook = ("#if SHARPLINK_STATIC_BINDING_PROTOTYPE\n"
            "        if (args.Length > 0 && args[0] == \"--boundary-codegen\")\n"
            "        {\n            await BoundaryDiagnostics.RunAsync(args[1..]);\n            return;\n        }\n#endif\n")
    program.write_text(text.replace(anchor, anchor + hook))
    target = root / "test/SharpLink.Benchmarks/BoundaryDiagnostics.cs"
    shutil.copyfile(driver / "eng/issue775/BoundaryDiagnostics.cs", target)
    for source in (program, target):
        destination = output / source.name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
    (output / "diagnostic-only.patch").write_text(git(root, "diff") + "\n")
    return {p.name: digest(p) for p in (program, target)}


def validate_result(value, transport, shape):
    one_way = shape == "oneway"
    expected = {"Schema": 1, "Passed": True, "Magic": 183, "Transport": transport, "Shape": shape,
                "Pool": 1 if one_way else 4, "Concurrency": 1 if one_way else 8,
                "Batches": 512 if one_way else 8, "BatchSize": 2048 if one_way else 65536,
                "Sent": 1048576 if one_way else 524288, "Received": 1048576 if one_way else 524288,
                "BoundaryUnaryCalls": 512 if one_way else 8, "BindCount": 1 if one_way else 4,
                "PendingBinds": 0, "ProcessorCount": 2, "PerformanceEvidence": False}
    if any(value.get(k) != v for k, v in expected.items()):
        raise ValueError("Diagnostic completion/count/dialect mismatch")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--old", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--expected-candidate", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    roots = {"old": args.old.resolve(), "candidate": args.candidate.resolve()}
    commits = {"old": OLD, "candidate": args.expected_candidate}
    output = args.output.resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError("Refusing to replace prior evidence")
    output.mkdir(parents=True, exist_ok=True)
    for arm, root in roots.items():
        if not re.fullmatch(r"[0-9a-f]{40}", commits[arm]) or git(root, "rev-parse", "HEAD") != commits[arm] or git(root, "status", "--porcelain"):
            raise ValueError("Wrong or dirty input")
    common = verify_source(roots["old"], roots["candidate"])
    (output / "production.patch").write_text(git(roots["candidate"], "diff", OLD, "HEAD", "--", "src") + "\n")
    env = os.environ.copy()
    if any(k.startswith(("DOTNET_", "COMPlus_")) and any(x in k.lower() for x in ("jit", "tier", "processor_count", "profiler")) for k in env):
        raise ValueError("Inherited runtime tuning")
    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Two available CPUs required")
    env |= {"SharpLinkStaticBindingPrototype": "true", "SHARPLINK_EXPECTED_MAGIC": "183",
            "DOTNET_PROCESSOR_COUNT": "2", "DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1"}
    affinity = ["taskset", "-c", ",".join(map(str, cpus))]
    manifest = {"commits": commits, "commonInputSha256": common, "cpuAffinity": cpus,
                "cases": CASES, "jitFilter": FILTER, "sdk": {}, "assemblies": {}, "overlay": {},
                "purpose": "Actual caller Tier1 mechanism review. NOT timing evidence.", "stage2Authorized": False}
    failures, runtimes = [], set()
    for arm, root in roots.items():
        manifest["sdk"][arm] = subprocess.check_output(["dotnet", "--version"], cwd=root, text=True).strip()
        manifest["overlay"][arm] = overlay(root, roots["candidate"], output / arm / "overlay")
        arm_env = env | {"SHARPLINK_BENCHMARK_SHA": commits[arm]}
        save(output / "manifest.json", manifest)
        for name, command in (("environment", ["dotnet", "--info"]), ("cpu", ["lscpu"]),
                              ("build", ["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal"]),
                              ("smoke", ["dotnet", BENCHMARK, "--static-binding-smoke"]),
                              ("shutdown", ["dotnet", "build-server", "shutdown"])):
            if not execute(command, root, output / arm / name, arm_env, 300):
                raise RuntimeError(f"{arm}/{name} failed")
        manifest["assemblies"][arm] = {p.name: digest(p) for p in sorted((root / Path(BENCHMARK).parent).glob("*.dll"))}
        for transport, shape in CASES:
            directory = output / arm / f"{transport}-{shape}"
            directory.mkdir(parents=True, exist_ok=True)
            jit_env = arm_env | {"DOTNET_JitDisasm": FILTER, "DOTNET_JitStdOutFile": str(directory / "disassembly.txt")}
            save(directory / "diagnostic-environment.json", {k: v for k, v in jit_env.items() if k.startswith("DOTNET_")})
            command = affinity + ["dotnet", BENCHMARK, "--boundary-codegen", transport, shape, str(directory / "result.json")]
            if not execute(command, root, directory, jit_env, 180):
                failures.append(f"{arm}/{transport}-{shape}: process failed")
                continue
            try:
                value = json.loads((directory / "result.json").read_text())
                validate_result(value, transport, shape)
                runtimes.add(value["Runtime"])
                caller = "DispatchOneWayRpc" if shape == "oneway" else "DispatchRpcAsync"
                text = (directory / "disassembly.txt").read_text()
                headers = [s for s in text.splitlines() if s.startswith("; Assembly listing for method") and caller + "(" in s and "(Tier1)" in s]
                if not headers:
                    raise ValueError(f"Missing actual {caller} Tier1")
                save(directory / "coverage.json", {"caller": caller, "headers": headers,
                    "copyEliminationConfirmed": False, "note": "Review complete actual-caller listing, not code size or helper Tier0."})
            except Exception as error:
                failures.append(f"{arm}/{transport}-{shape}: {error}")
    if len(set(manifest["sdk"].values())) != 1 or len(runtimes) != 1:
        failures.append("SDK/runtime mismatch or missing results")
    if manifest["overlay"].get("old") != manifest["overlay"].get("candidate"):
        failures.append("Diagnostic overlay differs across arms")
    save(output / "manifest.json", manifest)
    save(output / "execution-failures.json", failures)
    if failures:
        raise RuntimeError("; ".join(failures))


if __name__ == "__main__":
    main()
