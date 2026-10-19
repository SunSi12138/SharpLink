#!/usr/bin/env python3
"""Frozen no-stream bridge candidate; semantics and JIT precede any timing."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
from run import BENCHMARK, PROJECT, COMMON_INPUTS, OVERLAY, digest, execute, git, save
from control_site import default_source, add_checks, validate_checks
from client_send import overlay
import client_send_report
from persistent_regression import validate as validate_persistent

BASE = "6cfb09e7dd6b68695f92d28f726340bc8eb39e1d"
PATCH_SHA = "14f925061666cd272abf37bffdbbf54e319910f2cbe040dcaccaf8e5cce514bc"
CHANNEL = "src/SharpLink.Client/StaticBindingRpcChannel.cs"
ENTRY = "src/SharpLink.Client/SharpLinkClient.StaticPlainOneWay.cs"
INVOKERS = "src/SharpLink.Client/SharpLinkClient.Invokers.cs"


def check_entry(root):
    # Reject drift in the copied experiment entry: the original body with the exact
    # empty writer substituted is the entire new method body, including its catches.
    original = (root/INVOKERS).read_text()
    a = original.index("        ArgumentNullException.ThrowIfNull(requestCodec);", original.index("internal ValueTask InvokeStaticOneWayAsync"))
    b = original.index("\n    public ValueTask<TResponse> InvokeClientStreamingAsync", a)
    body = original[a:b].replace("#if SHARPLINK_STATIC_BINDING_PROTOTYPE\n", "").replace("#endif\n", "")
    body = body.replace("requestCodec, streams,", "requestCodec, default(RpcNoClientStreams),")
    actual = (root/ENTRY).read_text()
    actual = actual[actual.index("        ArgumentNullException.ThrowIfNull(requestCodec);"):]
    if actual != body + "}\n#endif\n":
        raise ValueError("Specialized entry differs from original no-stream semantics")


def apply_candidate(root, driver, out):
    if git(root, "rev-parse", "HEAD") != BASE or git(root, "status", "--porcelain"):
        raise ValueError("Wrong or dirty candidate base")
    patch = driver/"eng/issue775/bridge.patch"
    if digest(patch) != PATCH_SHA:
        raise ValueError("Frozen patch changed")
    before = (root/CHANNEL).read_text()
    subprocess.run(["git", "apply", "--check", str(patch)], cwd=root, check=True)
    subprocess.run(["git", "apply", "--index", str(patch)], cwd=root, check=True)
    if set(git(root,"diff","--cached","--name-only").splitlines()) != {CHANNEL, ENTRY}:
        raise ValueError("Unexpected source modification")
    if default_source(before) != default_source((root/CHANNEL).read_text()) or default_source((root/ENTRY).read_text()):
        raise ValueError("Default compile changed")
    check_entry(root)
    subprocess.run(["git","diff","--cached","--check"],cwd=root,check=True)
    env = os.environ | {"GIT_AUTHOR_NAME":"Issue775 experiment","GIT_AUTHOR_EMAIL":"issue775@example.invalid",
        "GIT_COMMITTER_NAME":"Issue775 experiment","GIT_COMMITTER_EMAIL":"issue775@example.invalid",
        "GIT_AUTHOR_DATE":"2026-10-20T00:00:00Z","GIT_COMMITTER_DATE":"2026-10-20T00:00:00Z"}
    subprocess.run(["git","-c","commit.gpgsign=false","commit","-m","experiment: exact no-stream bridge specialization"],cwd=root,env=env,check=True)
    identity = {"base":BASE,"patchSha256":PATCH_SHA,"localCandidateSha":git(root,"rev-parse","HEAD"),
        "tree":git(root,"rev-parse","HEAD^{tree}"),"published":False,"stage2Authorized":False,
        "changedFiles":[CHANNEL,ENTRY],"defaultSourceUnchanged":True,"entryBodyEquivalenceChecked":True}
    save(out/"candidate-identity.json",identity)
    (out/"production.patch").write_text(git(root,"diff",BASE,"HEAD","--","src")+"\n")
    with (out/"candidate-source.tar").open("wb") as target:
        subprocess.run(["git","archive","HEAD"],cwd=root,stdout=target,check=True)
    return identity


def validate_bridge(value):
    expected = {"Passed":True,"Checks":46,"TransportCount":2,"WriterTypes":2,
                "GeneratedReceived":2048,"StreamWrites":0,"Magic":183}
    if value != expected:
        raise ValueError("Bridge semantics incomplete or incorrect")


def install_checks(root, driver, out):
    files = overlay(root,driver,"B",out)
    files.update(add_checks(root,driver,out))
    rel = "test/SharpLink.Benchmarks/Issue775BridgeChecks.cs"
    if (root/rel).exists():
        raise ValueError("Unexpected existing bridge shim")
    (root/rel).write_bytes((driver/"eng/issue775/BridgeChecks.cs").read_bytes())
    program = root/"test/SharpLink.Benchmarks/Program.cs"
    anchor = "    public static async Task Main(string[] args)\n    {\n"
    text = program.read_text()
    if text.count(anchor) != 1:
        raise ValueError("Entrypoint anchor changed")
    program.write_text(text.replace(anchor,anchor+'#if SHARPLINK_STATIC_BINDING_PROTOTYPE\n        if (args.Length > 0 && args[0] == "--bridge-checks")\n        { await BridgeChecks.RunAsync(args[1]); return; }\n#endif\n'))
    for path in (rel,"test/SharpLink.Benchmarks/Program.cs"):
        dest = out/path
        dest.parent.mkdir(parents=True,exist_ok=True)
        dest.write_bytes((root/path).read_bytes())
        files[path] = digest(root/path)
    return files


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for n in ("old","candidate","driver","output"):
        parser.add_argument("--"+n,type=Path,required=True)
    parser.add_argument("--default-only",action="store_true")
    args = parser.parse_args()
    old,candidate,driver,out = (getattr(args,n).resolve() for n in ("old","candidate","driver","output"))
    if out.exists() and any(out.iterdir()):
        raise ValueError("Do not overwrite or merge evidence")
    out.mkdir(parents=True,exist_ok=True)
    identity = apply_candidate(candidate,driver,out)
    initial = os.environ.copy()
    if any(k.startswith(("DOTNET_","COMPlus_","CORECLR_","COR_")) and any(s in k.lower() for s in ("jit","tier","profiler","processor_count","perfmap")) for k in initial):
        raise ValueError("Inherited diagnostic settings")
    failures = []
    if args.default_only:
        env = initial | {"TESTINGPLATFORM_TELEMETRY_OPTOUT":"1","TUNIT_MAX_PARALLEL_TESTS":"8"}
        commands = [("build",["dotnet","build","Sharplink.slnx","-c","Release","-v","minimal"]),
            ("units",["dotnet","test","--project","test/SharpLink.UnitTests/SharpLink.UnitTests.csproj","-c","Release","--no-build"]),
            ("generator",["dotnet","test","--project","test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj","-c","Release","--no-build"]),
            ("format",["dotnet","format","whitespace","Sharplink.slnx","--no-restore","--verify-no-changes","--verbosity","minimal"]),
            ("policy-tests",["python3","eng/test-verify-maintainability.py"]),
            ("policy",["bash","eng/check-maintainability.sh"])]
        failures = [n for n,c in commands if not execute(c,candidate,out/n,env,360)]
    else:
        if git(old,"rev-parse","HEAD") != BASE or git(old,"status","--porcelain"):
            raise ValueError("Old source changed")
        for path in COMMON_INPUTS + OVERLAY:
            if (old/path).read_bytes() != (candidate/path).read_bytes():
                raise ValueError("Workload or policy changed: "+path)
        cpus = sorted(os.sched_getaffinity(0))[:2]
        if len(cpus) != 2:
            raise ValueError("Two CPUs required")
        affinity = ["taskset","-c",",".join(map(str,cpus))]
        env = initial | {"SharpLinkStaticBindingPrototype":"true","SHARPLINK_EXPECTED_MAGIC":"183",
                         "DOTNET_PROCESSOR_COUNT":"2","DOTNET_TieredCompilation":"1","DOTNET_TieredPGO":"1"}
        manifest = {"identity":identity,"driverSha":git(driver,"rev-parse","HEAD"),"cpus":cpus,
                    "diagnosticOnly":True,"measurementSlots":[],"arms":{}}
        for arm,root,sha in (("old",old,BASE),("candidate",candidate,identity["localCandidateSha"])):
            aenv = env | {"SHARPLINK_BENCHMARK_SHA":sha}
            manifest["arms"][arm] = {"sha":sha,"overlay":install_checks(root,driver,out/"overlay"/arm)}
            save(out/"manifest.json",manifest)
            for name,command in (("env",["dotnet","--info"]),("cpu",["lscpu"]),
                ("build",["dotnet","build",PROJECT,"-c","Release","-v","minimal"]),
                ("smoke",["dotnet",BENCHMARK,"--static-binding-smoke"])):
                if not execute(command,root,out/arm/name,aenv,300):
                    raise RuntimeError(arm+"/"+name+" failed")
            for name,flag,validator in (("semantics","--control-site-checks",lambda v:validate_checks(v,88)),
                ("persistent","--control-site-persistent",lambda v:validate_persistent(v,False)),
                ("bridge","--bridge-checks",validate_bridge)):
                result = out/arm/(name+".json")
                if not execute(["dotnet",BENCHMARK,flag,str(result)],root,out/arm/name,aenv,180):
                    failures.append(arm+"/"+name)
                else:
                    try: validator(json.loads(result.read_text()))
                    except Exception as error: failures.append(f"{arm}/{name}: {error}")
            if failures:
                break
            if not execute(["dotnet","build-server","shutdown"],root,out/arm/"shutdown",initial,60):
                raise RuntimeError("Build server shutdown failed")
            client_send_report.COMMITS["B"] = sha
            for case in client_send_report.CASES:
                dest = out/arm/"jit"/case
                dest.mkdir(parents=True,exist_ok=True)
                t,c,p = case.split("-")
                jit = aenv | {"DOTNET_JitDisasm":"*SharpLinkClient*:* *StaticBindingRpcChannel*:* PublishEventAsync",
                              "DOTNET_JitStdOutFile":str(dest/"disassembly.txt")}
                save(dest/"jit-env.json",{k:v for k,v in jit.items() if k.startswith("DOTNET_")})
                if not execute(affinity+["dotnet",BENCHMARK,"--client-send-diagnostic",t,c[1:],p[1:],str(dest/"result.json")],root,dest,jit,210):
                    failures.append(arm+"/"+case)
                    continue
                try:
                    client_send_report.validate(json.loads((dest/"result.json").read_text()),"B",case)
                    headers = [s for s in (dest/"disassembly.txt").read_text().splitlines() if s.startswith("; Assembly listing for method")]
                    if not any("PublishEventAsync(" in h and "(Tier1)" in h for h in headers):
                        raise ValueError("Generated caller Tier1 missing")
                    save(dest/"coverage.json",{"headers":headers,"tailcallConfirmed":False,"manualReviewRequired":True})
                except Exception as error: failures.append(f"{arm}/{case}: {error}")
            manifest["arms"][arm]["assemblies"] = {p.name:digest(p) for p in sorted((root/Path(BENCHMARK).parent).glob("*.dll"))}
        save(out/"manifest.json",manifest)
    save(out/"execution-failures.json",failures)
    if failures:
        raise RuntimeError(str(failures))


if __name__ == "__main__":
    main()
