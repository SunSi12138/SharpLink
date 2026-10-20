#!/usr/bin/env python3
"""Read-only, patch-addressed candidate: semantic regressions and JIT, never timing acceptance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
from run import BENCHMARK, PROJECT, COMMON_INPUTS, OVERLAY, digest, execute, git, save
from client_send import overlay
from client_send_report import CASES, validate as validate_sender
import client_send_report
from persistent_regression import validate as validate_persistent

BASE = "454f51e913684853c1caa2221bc92c94e6ad1789"
PATCH_SHA = "17b337a676e7a1f690cf5f29e844532f34602d033d679bf480336543e8043b9e"
FILES = {f"src/SharpLink.Client/SharpLinkClient.{n}.cs" for n in
         ("CallOptions", "Interceptors", "Invokers", "Retry", "Telemetry")}


def default_source(text):
    lines, stack, active = [], [], True
    for line in text.splitlines():
        flag = line.strip()
        if flag == "#if SHARPLINK_STATIC_BINDING_PROTOTYPE":
            stack.append(active)
            active = False
        elif flag == "#else" and stack:
            active = stack[-1] and not active
        elif flag == "#endif" and stack:
            active = stack.pop()
        elif active:
            lines.append(line)
    if stack:
        raise ValueError("Unbalanced conditionals")
    tokens = re.findall(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\w+|[^\s]', "\n".join(lines))
    return " ".join(tokens)


def apply_candidate(root, driver, out):
    if git(root, "rev-parse", "HEAD") != BASE or git(root, "status", "--porcelain"):
        raise ValueError("Not the clean, fixed source")
    patch = driver / "eng/issue775/control-site.patch"
    if digest(patch) != PATCH_SHA:
        raise ValueError("Candidate patch changed")
    before = {p: (root/p).read_text() for p in FILES}
    subprocess.run(["git", "apply", "--check", str(patch)], cwd=root, check=True)
    subprocess.run(["git", "apply", "--index", str(patch)], cwd=root, check=True)
    changed = set(git(root, "diff", "--cached", "--name-only").splitlines())
    if changed != FILES:
        raise ValueError("Unexpected production change")
    for path, original in before.items():
        if default_source(original) != default_source((root/path).read_text()):
            raise ValueError("Default-build source changed: " + path)
    from control_site_layout import apply_layout
    layout = apply_layout(root, driver)
    if set(git(root, "diff", "--cached", "--name-only").splitlines()) != FILES | {layout["newFile"]}:
        raise ValueError("Unexpected change after method extraction")
    subprocess.run(["git", "diff", "--cached", "--check"], cwd=root, check=True)
    # Local deterministic commit identifies every production byte. It is never pushed by CI.
    env = os.environ | {"GIT_AUTHOR_NAME": "Issue775 experiment", "GIT_AUTHOR_EMAIL": "issue775@example.invalid",
                        "GIT_COMMITTER_NAME": "Issue775 experiment", "GIT_COMMITTER_EMAIL": "issue775@example.invalid",
                        "GIT_AUTHOR_DATE": "2026-10-19T00:00:00Z", "GIT_COMMITTER_DATE": "2026-10-19T00:00:00Z"}
    subprocess.run(["git", "-c", "commit.gpgsign=false", "commit", "-m", "experiment: explicit static binding site input"], cwd=root, env=env, check=True)
    identity = {"base": BASE, "patchSha256": PATCH_SHA, "localCandidateSha": git(root, "rev-parse", "HEAD"),
                "productionTree": git(root, "rev-parse", "HEAD^{tree}"), "publishedProductionCommit": False,
                "defaultSourceEquivalentIgnoringWhitespace": True, "changedFiles": sorted(FILES | {layout["newFile"]}), "layout": layout}
    save(out/"candidate-identity.json", identity)
    (out/"binding-site.patch").write_bytes(patch.read_bytes())
    (out/"production.patch").write_text(git(root, "diff", BASE, "HEAD", "--", "src") + "\n")
    identity["combinedPatchSha256"] = digest(out/"production.patch")
    save(out/"candidate-identity.json", identity)
    with (out/"candidate-source.tar").open("wb") as target:
        subprocess.run(["git", "archive", "HEAD"], cwd=root, stdout=target, check=True)
    return identity


def add_checks(root, driver, out):
    entries = {
        "test/SharpLink.Benchmarks/Issue775ControlSiteChecks.cs": driver/"eng/issue775/ControlSiteChecks.cs",
        "test/SharpLink.Benchmarks/Issue775PersistentChecks.cs": driver/"eng/issue775/PersistentBindingChecks.cs"}
    for rel, source in entries.items():
        if (root/rel).exists():
            raise ValueError("Unexpected overlay")
        (root/rel).write_bytes(source.read_bytes())
    program = root/"test/SharpLink.Benchmarks/Program.cs"
    text = program.read_text()
    anchor = "    public static async Task Main(string[] args)\n    {\n"
    if text.count(anchor) != 1:
        raise ValueError("Program anchor changed")
    text = text.replace(anchor, anchor + '#if SHARPLINK_STATIC_BINDING_PROTOTYPE\n'
        '        if (args.Length > 0 && args[0] == "--control-site-checks")\n'
        '        { await ControlSiteChecks.RunAsync(args[1]); return; }\n'
        '        if (args.Length > 0 && args[0] == "--control-site-persistent")\n'
        '        { await PersistentBindingChecks.RunAsync(args[1]); return; }\n#endif\n')
    program.write_text(text)
    entries["test/SharpLink.Benchmarks/Program.cs"] = program
    for rel in entries:
        copy = out/rel
        copy.parent.mkdir(parents=True, exist_ok=True)
        copy.write_bytes((root/rel).read_bytes())
    return {rel: digest(root/rel) for rel in entries}


def validate_checks(value, size):
    expected = {(t, i, a) for t in ("tcp", "sharedmemory") for i in (False, True) for a in (False, True)}
    rows = value["Cases"]
    if value["Magic"] != 183 or not value["Passed"] or value["ControlBytes"] != size:
        raise ValueError("Wrong dialect, control layout, or test result")
    if len(rows) != 8 or {(r["Transport"], r["Intercepted"], r["Telemetry"]) for r in rows} != expected:
        raise ValueError("Incomplete semantic matrix")
    if not all(r["Passed"] and ((r["Activities"] > 0) == r["Telemetry"]) for r in rows):
        raise ValueError("Telemetry or scenario failed")
    if value["Checks"] < 350:
        raise ValueError("Semantic checks missing")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("old", "candidate", "driver", "output"):
        parser.add_argument("--"+name, required=True, type=Path)
    parser.add_argument("--default-only", action="store_true")
    args = parser.parse_args()
    old, candidate, driver, out = (getattr(args, n).resolve() for n in ("old", "candidate", "driver", "output"))
    if out.exists() and any(out.iterdir()):
        raise ValueError("Refusing to overwrite evidence")
    out.mkdir(parents=True, exist_ok=True)
    identity = apply_candidate(candidate, driver, out)
    initial = os.environ.copy()
    if any(k.startswith(("DOTNET_", "COMPlus_", "CORECLR_", "COR_")) and
           any(s in k.lower() for s in ("jit", "tier", "profiler", "processor_count")) for k in initial):
        raise ValueError("Undeclared runtime knobs")
    if args.default_only:
        env = initial | {"TESTINGPLATFORM_TELEMETRY_OPTOUT": "1", "TUNIT_MAX_PARALLEL_TESTS": "8"}
        commands = [("build", ["dotnet", "build", "Sharplink.slnx", "-c", "Release", "-v", "minimal"]),
            ("units", ["dotnet", "test", "--project", "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj", "-c", "Release", "--no-build"]),
            ("generator", ["dotnet", "test", "--project", "test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj", "-c", "Release", "--no-build"]),
            ("format", ["dotnet", "format", "whitespace", "Sharplink.slnx", "--no-restore", "--verify-no-changes", "--verbosity", "minimal"]),
            ("policy-tests", ["python3", "eng/test-verify-maintainability.py"]),
            ("policy", ["bash", "eng/check-maintainability.sh"])]
        failures = [name for name, cmd in commands if not execute(cmd, candidate, out/name, env, 360)]
        save(out/"execution-failures.json", failures)
        if failures:
            raise RuntimeError(str(failures))
        return
    if git(old, "rev-parse", "HEAD") != BASE or git(old, "status", "--porcelain"):
        raise ValueError("Old source changed")
    for path in COMMON_INPUTS + OVERLAY:
        if (old/path).read_bytes() != (candidate/path).read_bytes():
            raise ValueError("Workload changed: " + path)
    cpus = sorted(os.sched_getaffinity(0))[:2]
    if len(cpus) != 2:
        raise ValueError("Two CPUs required")
    affinity = ["taskset", "-c", ",".join(map(str, cpus))]
    env = initial | {"SharpLinkStaticBindingPrototype": "true", "SHARPLINK_EXPECTED_MAGIC": "183",
                     "DOTNET_PROCESSOR_COUNT": "2", "DOTNET_TieredCompilation": "1", "DOTNET_TieredPGO": "1"}
    manifest = {"identity": identity, "driverSha": git(driver, "rev-parse", "HEAD"), "cpus": cpus,
                "stage2Authorized": False, "measurementSlots": [], "diagnosticOnly": True, "arms": {}}
    # Drop a former-layout-only assertion from the test shim in BOTH arms, not production.
    probe_text = (driver/"eng/issue775/client-send/ClientProbe.cs").read_text()
    old_block = '#if SHARPLINK_STATIC_BINDING_PROTOTYPE\n                control = control with { BindingSite = _site };\n                if (!ReferenceEquals(control.BindingSite, _site))\n                    throw new InvalidOperationException("Lost binding site.");\n#endif\n'
    if probe_text.count(old_block) != 1:
        raise ValueError("Probe anchor changed")
    failures = []
    for arm, root, sha, size in (("old", old, BASE, 88), ("candidate", candidate, identity["localCandidateSha"], 80)):
        aenv = env | {"SHARPLINK_BENCHMARK_SHA": sha}
        files = overlay(root, driver, "B", out/"overlay"/arm)
        rel = "src/SharpLink.Client/Issue775ClientProbe.cs"
        (root/rel).write_text(probe_text.replace(old_block, ""))
        (out/"overlay"/arm/rel).write_bytes((root/rel).read_bytes())
        files[rel] = digest(root/rel)
        files.update(add_checks(root, driver, out/"overlay"/arm))
        manifest["arms"][arm] = {"sha": sha, "overlay": files}
        save(out/"manifest.json", manifest)
        for name, command in (("env", ["dotnet", "--info"]), ("cpu", ["lscpu"]),
                ("build", ["dotnet", "build", PROJECT, "-c", "Release", "-v", "minimal"]),
                ("smoke", ["dotnet", BENCHMARK, "--static-binding-smoke"])):
            if not execute(command, root, out/arm/name, aenv, 300):
                raise RuntimeError(f"{arm}/{name} failed")
        for name, command, validator in (
            ("semantics", ["dotnet", BENCHMARK, "--control-site-checks", str(out/arm/"semantics.json")], lambda v: validate_checks(v, size)),
            ("persistent", ["dotnet", BENCHMARK, "--control-site-persistent", str(out/arm/"persistent.json")], lambda v: validate_persistent(v, False))):
            if not execute(command, root, out/arm/name, aenv, 180):
                failures.append(arm+"/"+name)
                continue
            try:
                validator(json.loads((out/arm/(name+".json")).read_text()))
            except Exception as error:
                failures.append(f"{arm}/{name}: {error}")
        if failures:
            save(out/"execution-failures.json", failures)
            raise RuntimeError(str(failures))
        if not execute(["dotnet", "build-server", "shutdown"], root, out/arm/"shutdown", initial, 60):
            raise RuntimeError("Build servers did not stop")
        client_send_report.COMMITS["B"] = sha  # Explicit identity for reuse of the strict raw-record reader.
        for case in CASES:
            directory = out/arm/"jit"/case
            directory.mkdir(parents=True, exist_ok=True)
            transport, c, p = case.split("-")
            jit = aenv | {"DOTNET_JitDisasm": "*SharpLinkClient*:* *StaticBindingRpcChannel*:* PublishEventAsync",
                          "DOTNET_JitStdOutFile": str(directory/"disassembly.txt")}
            save(directory/"jit-env.json", {k:v for k,v in jit.items() if k.startswith("DOTNET_")})
            argv = affinity + ["dotnet", BENCHMARK, "--client-send-diagnostic", transport, c[1:], p[1:], str(directory/"result.json")]
            if not execute(argv, root, directory, jit, 210):
                failures.append(arm+"/"+case)
                continue
            try:
                result = json.loads((directory/"result.json").read_text())
                validate_sender(result, "B", case)
                if result["ControlBytes"] != size:
                    raise ValueError("Wrong control size")
                headers = [line for line in (directory/"disassembly.txt").read_text().splitlines()
                           if line.startswith("; Assembly listing for method")]
                if not any("PublishEventAsync(" in line and "(Tier1)" in line for line in headers):
                    raise ValueError("Generated caller Tier1 missing")
                save(directory/"coverage.json", {"headers": headers, "mechanismConfirmed": False})
            except Exception as error:
                failures.append(f"{arm}/{case}: {error}")
        manifest["arms"][arm]["assemblies"] = {p.name:digest(p) for p in sorted((root/Path(BENCHMARK).parent).glob("*.dll"))}
    save(out/"manifest.json", manifest)
    save(out/"execution-failures.json", failures)
    if failures:
        raise RuntimeError(str(failures))


if __name__ == "__main__":
    main()
