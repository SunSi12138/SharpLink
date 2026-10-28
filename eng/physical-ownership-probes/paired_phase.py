#!/usr/bin/env python3
"""Phase full-product entry: unchanged R1 schedule; isolated rewrite comparison.

Not a performance acceptance test. Legacy paired_931 defaults are unchanged.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import sys

from phase_common import (BASELINE, CANDIDATE, CANDIDATE_ORIGINAL, CANDIDATE_TREE, SDK, RUNTIME, PACKAGES, capture, run, write,
                        verify_source, validate_runtime, verify_dependencies, pin_snapshot, verify_process)
from phase_full_validation import validate_measurements, validate_summary

DECISION = "Diagnostic only: no outcome alone lifts #931 NoGo or authorizes product merge. Separate predeclared acceptance contract and candidate gates required."


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--build-dotnet", type=Path, required=True)
    parser.add_argument("--runtime-dotnet", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source = Path(__file__).resolve().parent
    tools_root = source.parent.parent
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    roots = {"baseline": args.baseline.resolve(strict=True), "candidate": args.candidate.resolve(strict=True)}
    build_dotnet = args.build_dotnet.resolve(strict=True)
    runtime_dotnet = args.runtime_dotnet.resolve(strict=True)
    for variant, expected in (("baseline", BASELINE), ("candidate", CANDIDATE)):
        verify_source(roots[variant], expected)
    if capture(["git", "rev-parse", "HEAD^{tree}"], roots["candidate"]) != CANDIDATE_TREE:
        raise ValueError("Published candidate tree differs from reviewed local 607")
    tool_sha = capture(["git", "rev-parse", "HEAD"], tools_root)
    verify_source(tools_root, tool_sha)
    actual_sdk = capture([str(build_dotnet), "--version"], tools_root)
    if actual_sdk != SDK:
        raise ValueError(f"Expected SDK {SDK}; found {actual_sdk}")
    runtimes = validate_runtime(runtime_dotnet)
    available = sorted(os.sched_getaffinity(0))
    if len(available) < 2:
        raise ValueError(f"Two allowed CPUs required; available: {available}")
    affinity = ",".join(map(str, available[:2]))
    write(output / "experiment.json", {
        "baselineRevision": BASELINE, "candidateRevision": CANDIDATE, "toolRevision": tool_sha,
        "candidateOriginalRevision": CANDIDATE_ORIGINAL, "candidateTree": CANDIDATE_TREE,
        "decisionBoundary": DECISION,
        "sdk": actual_sdk, "runtimes": runtimes, "availableCpus": available, "affinity": affinity,
        "runner": {k: os.environ.get(k) for k in ("RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT")},
        "scope": "Phase full-product diagnostic; unchanged four-scene 48-process contract. Not performance equivalence or final 3.0 acceptance.",
        "interpretation": "All samples retained; no added performance threshold. Zero allocation and noisy null results do not establish performance equivalence.",
    })
    for name, command in (("build-dotnet-info", [build_dotnet, "--info"]),
                          ("runtime-dotnet-info", [runtime_dotnet, "--info"]),
                          ("uname", ["uname", "-a"]), ("cpu", ["lscpu"]),
                          ("cpu-mapping", ["lscpu", "-e=CPU,CORE,SOCKET,NODE,ONLINE"])):
        run(command, output / f"{name}.txt", tools_root)
    # Each source arm is independently built and fresh-packed before snapshots.
    for variant, root in roots.items():
        logs = output / variant
        logs.mkdir()
        common = ["-c", "Release", "-m:1", "-nr:false", "-p:UseSharedCompilation=false"]
        run([build_dotnet, "build", "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj", *common, f"-bl:{logs / 'build.binlog'}"], logs / "build.log", root)
        packages = logs / "packages"
        for package in PACKAGES:
            project = f"src/SharpLink.{package}/SharpLink.{package}.csproj"
            run([build_dotnet, "pack", project, *common, "-o", packages, f"-bl:{logs / ('pack-' + package + '.binlog')}"], logs / f"pack-{package}.log", root)
        run(["bash", "eng/verify-packages.sh", packages, "3.0.0"], logs / "package-verification.log", root)
        verify_source(root, BASELINE if variant == "baseline" else CANDIDATE)
    libraries = {v: r / "test/SharpLink.UnitTests/bin/Release/net10.0" for v, r in roots.items()}
    shared_args = ["--baseline-libraries", libraries["baseline"], "--candidate-libraries", libraries["candidate"], "--dotnet", build_dotnet]
    tcp = output / "tcp-prepared"
    scalar = output / "scalar-prepared"
    run([sys.executable, source / "tcp/prepare.py", *shared_args,
         "--baseline-generator", roots["baseline"] / "src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll",
         "--probe-source", source / "tcp/r1-full-probe", "--output", tcp], output / "tcp-prepare.log", tools_root)
    revisions = ["--baseline-revision", BASELINE, "--candidate-revision", CANDIDATE]
    run([sys.executable, source / "phase-scalar/run.py", *shared_args, *revisions,
         "--prepare-only", "--output", scalar], output / "scalar-prepare.log", tools_root)
    snapshots = {"tcp": (tcp / "baseline", tcp / "candidate", ("PhysicalTcpProbe.dll", "ScalarStreamModule.dll")),
                 "scalar": (scalar / "baseline/app", scalar / "candidate/app", ("SharpLink.Benchmarks.dll",))}
    integrity = {}
    for name, (baseline, candidate, identical) in snapshots.items():
        integrity[name] = verify_dependencies(baseline, candidate, identical)
        pin_snapshot(baseline)
        pin_snapshot(candidate)
    write(output / "snapshot-integrity.json", integrity)
    # This runs only on this job's disposable hosted runner, never on a developer host.
    run([build_dotnet, "build-server", "shutdown"], output / "build-server-shutdown.log", tools_root)
    environment = dict(os.environ, DOTNET_ROOT=str(runtime_dotnet.parent), DOTNET_MULTILEVEL_LOOKUP="0")
    write(output / "measurement-start.json", {"timestampUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                                               "policy": "No build or fresh-pack after this marker"})
    tcp_results = output / "tcp"
    execution_error = None
    try:
        run([sys.executable, source / "tcp/run_matrix.py", "--prepared", tcp, "--output", tcp_results,
             *revisions, "--dotnet", runtime_dotnet, "--rounds", "6", "--warmup", "3", "--seconds", "6",
             "--workers", "1", "--items", "1,100", "--modes", "static-intercepted,dynamic-intercepted",
             "--affinity", affinity, "--export-ticks"], output / "tcp-run.log", tools_root, environment)
    except Exception as error:
        execution_error = error
    validation = validate_measurements(tcp_results, tcp, integrity["tcp"], runtime_dotnet)
    run([sys.executable, source / "tcp/summarize.py", "--input", tcp_results,
         "--validation-manifest", tcp_results / "sample-validation.json"],
        output / "tcp-summary.log", tools_root,
        check=execution_error is None and validation["status"] == "Passed")
    if execution_error is not None:
        raise execution_error
    if validation["status"] != "Passed":
        raise ValueError("Phase full-product input validation failed; retain all failures")
    validate_summary(tcp_results)
    scalar_results = output / "scalar"
    run([sys.executable, source / "phase-scalar/run.py", "--prepared", scalar, "--output", scalar_results,
         "--runtime-dotnet", runtime_dotnet, "--allocation-repeats", "5", "--affinity", affinity],
        output / "scalar-run.log", tools_root, environment)
    scalar_environments = list(scalar_results.glob("*/*/run-*/environment.json"))
    if len(scalar_environments) != 14:
        raise ValueError(f"Expected 14 scalar process records; found {len(scalar_environments)}")
    for path in scalar_environments:
        verify_process(json.loads(path.read_text()), runtime_dotnet.parent)
    write(output / "complete.json", {"tcpSamples": 48, "scalarFullOptsProcesses": 10,
                                      "scalarTieredDiagnosticProcesses": 4, "toolRevision": tool_sha,
                                      "decisionBoundary": DECISION, "performanceAcceptance": "Not evaluated; #931 remains NoGo"})


if __name__ == "__main__":
    main()
