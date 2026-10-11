#!/usr/bin/env python3
"""Allocation appendix; no RPC timing or production Go is inferred."""
import hashlib, json, os, shutil, subprocess
from pathlib import Path
BASE = "f8ba89121c87a6410a0139c44d7db30eab877f41"
ROOT = Path.cwd()
OUT = ROOT / "artifacts/issue735/footprint"
HARNESS = "test/SharpLink.FlowArchitectureEvidence"
FOOT = "test/SharpLink.FlowControllerFootprintEvidence"
PROJECT = FOOT + "/SharpLink.FlowControllerFootprintEvidence.csproj"
OUT.mkdir(parents=True, exist_ok=True)
rows = []
def run(command, name, cwd=ROOT, env=None, timeout=600):
    with (OUT / (name + ".log")).open("w") as log:
        try:
            code = subprocess.run(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=timeout).returncode
        except subprocess.TimeoutExpired:
            code = 124
    rows.append(dict(name=name, command=command, exitCode=code))
    (OUT / "exits.json").write_text(json.dumps(rows, indent=2))
    print(json.dumps(rows[-1]), flush=True)
    if code:
        raise RuntimeError("Footprint appendix failed: " + name)
def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()
head = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
run(["dotnet", "--info"], "dotnet-info")
run(["git", "fetch", "--no-tags", "--depth=1", "origin", BASE], "fetch-base")
manifest = dict(classification="allocation-appendix-not-RPC-or-retained-heap", measurementRecipeSha=head, subjects={})
for arm, revision, mode in [("A", BASE, "baseline"), ("B", head, "D-split-direction-gates")]:
    work = Path(os.environ["RUNNER_TEMP"]) / ("issue735-footprint-" + arm)
    run(["git", "worktree", "add", "--detach", str(work), revision], "worktree-" + arm)
    for source in [HARNESS, FOOT]:
        target = work / source
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(ROOT / source, target)
    tree = subprocess.check_output(["git", "rev-parse", revision + "^{tree}"], cwd=work, text=True).strip()
    verified = OUT / ("runtime-identity-" + arm + ".json")
    run(["python3", str(work / HARNESS / "stamp_identity.py"), "--root", str(work),
         "--baseline-sha", BASE, "--candidate-sha", revision, "--runtime-source-tree-sha", tree,
         "--mode", mode, "--record", str(verified)], "verify-runtime-" + arm)
    info = json.loads(verified.read_text())
    files = {p.name: digest(p) for p in sorted((work / FOOT).iterdir())
             if p.is_file() and p.suffix in (".cs", ".csproj") and p.name != "ModeIdentity.cs"}
    info["MeasurementSourceSha"] = hashlib.sha256(json.dumps(files, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    fields = ["Mode", "BaselineSha", "CandidateSha", "RuntimeSourceTreeSha", "MeasurementSourceSha", "ControllerSourceSha256"]
    generated = "namespace SharpLink.Benchmarks;\ninternal static class ModeIdentity\n{\n"
    generated += "".join("    internal const string " + k + " = " + json.dumps(info[k]) + ";\n" for k in fields)
    generated += "}\n"
    (work / FOOT / "ModeIdentity.cs").write_text(generated)
    (OUT / ("compiled-identity-" + arm + ".cs")).write_text(generated)
    manifest["subjects"][arm] = dict(identity=info, measurementFiles=files, binaries={})
    for kind in ["jit", "nativeaot"]:
        binary_dir = OUT / "binaries" / (arm + "-" + kind)
        if kind == "nativeaot":
            command = ["dotnet", "publish", PROJECT, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "-o", str(binary_dir), "-v", "minimal"]
        else:
            command = ["dotnet", "build", PROJECT, "-c", "Release", "-p:PublishAot=false", "-v", "minimal"]
        run(command, "build-" + arm + "-" + kind, work, timeout=900)
        if kind == "jit":
            shutil.copytree(work / FOOT / "bin/Release/net10.0", binary_dir)
        binary = binary_dir / ("SharpLink.Benchmarks" if kind == "nativeaot" else "SharpLink.Benchmarks.dll")
        binary_hash = digest(binary)
        manifest["subjects"][arm]["binaries"][kind] = dict(sha256=binary_hash, file=str(binary.relative_to(OUT)))
        (OUT / "provenance.json").write_text(json.dumps(manifest, indent=2))
        for population in (["nativeaot"] if kind == "nativeaot" else ["jit-pgo0", "jit-pgo1"]):
            result = OUT / ("sample-" + arm + "-" + population + ".tsv")
            env = dict(os.environ, DOTNET_PROCESSOR_COUNT="4", DOTNET_ReadyToRun="0")
            if kind == "jit":
                env.update(DOTNET_TieredCompilation="1", DOTNET_TieredPGO=population[-1])
            else:
                env.pop("DOTNET_TieredPGO", None)
                env.pop("DOTNET_TieredCompilation", None)
            command = ([str(binary)] if kind == "nativeaot" else ["dotnet", str(binary)]) + [mode, revision, info["ControllerSourceSha256"], str(result)]
            run(command, "execute-" + arm + "-" + population, env=env, timeout=180)
            lines = result.read_text().splitlines()
            metadata = dict(line[1:].split("\t", 1) for line in lines if line.startswith("#"))
            samples = [line for line in lines if not line.startswith("#")][1:]
            if len(samples) != 65 or metadata.get("status") != "passed" or metadata.get("measuredBinarySha256") != binary_hash:
                raise RuntimeError("Footprint sample validation failed")
            if metadata.get("measurementSourceSha") != info["MeasurementSourceSha"] or metadata.get("builtForNativeAot") != str(kind == "nativeaot").lower():
                raise RuntimeError("Footprint execution identity mismatch")
if manifest["subjects"]["A"]["identity"]["MeasurementSourceSha"] != manifest["subjects"]["B"]["identity"]["MeasurementSourceSha"]:
    raise RuntimeError("Unmatched footprint measurement source")
(OUT / "result.json").write_text(json.dumps(dict(status="passed", rawSampleRows=390, productionGo=False), indent=2))
