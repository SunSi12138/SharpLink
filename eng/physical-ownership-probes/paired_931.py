#!/usr/bin/env python3
"""Frozen PR931 same-runner experiment. All builds finish before any sampling."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

BASELINE = "cc7c458b559295e0fc61ec0062bb220faf880a76"
CANDIDATE = "ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c"
SDK = "10.0.102"
RUNTIME = "10.0.13"
PACKAGES = ("Abstractions", "Client", "Compression.Zstd", "GenerationControl", "Hosting",
            "Runtime", "Sdk", "Serializer.SharpPack", "Server")


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def capture(command, cwd=None):
    return subprocess.check_output(command, cwd=cwd, text=True).strip()


def run(command, log, cwd=None, environment=None, check=True):
    print("RUN", " ".join(map(str, command)), flush=True)
    with log.open("w") as stream:
        subprocess.run(list(map(str, command)), cwd=cwd, env=environment,
                       stdout=stream, stderr=subprocess.STDOUT, check=check)


def verify_source(root, expected):
    actual = capture(["git", "rev-parse", "HEAD"], root)
    if actual != expected or capture(["git", "status", "--porcelain"], root):
        raise ValueError(f"Expected clean {expected} checkout: {root}; found {actual}")


def validate_runtime(dotnet):
    listing = capture([str(dotnet), "--list-runtimes"])
    expected = {("Microsoft.NETCore.App", RUNTIME), ("Microsoft.AspNetCore.App", RUNTIME)}
    actual = {tuple(line.split()[:2]) for line in listing.splitlines()}
    if actual != expected or capture([str(dotnet), "--list-sdks"]):
        raise ValueError(f"Expected runtime-only private .NETCore + AspNet {RUNTIME}; found {listing}")
    for line in listing.splitlines():
        location = Path(line.split("[", 1)[1].rstrip("]")).resolve()
        if not location.is_relative_to(dotnet.parent):
            raise ValueError(f"Runtime escaped private installation: {line}")
    return listing


def pin_snapshot(root):
    """Framework versions are exact; no roll-forward to any installed servicing patch."""
    configs = list(root.glob("*.runtimeconfig.json"))
    if len(configs) != 1:
        raise ValueError(f"Expected one runtimeconfig: {root}")
    data = json.loads(configs[0].read_text())
    options = data["runtimeOptions"]
    frameworks = options.get("frameworks", [options.get("framework")])
    if {f["name"] for f in frameworks if f} != {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"}:
        raise ValueError(f"Unexpected frameworks: {configs[0]}")
    for framework in frameworks:
        framework["version"] = RUNTIME
    options["rollForward"] = "Disable"
    write(configs[0], data)


def verify_dependencies(baseline, candidate, identical):
    b = {p.name: p for p in baseline.glob("*.dll")}
    c = {p.name: p for p in candidate.glob("*.dll")}
    if b.keys() != c.keys():
        raise ValueError("Dependency inventories differ")
    for name in b:
        if (not name.startswith("SharpLink.") or name in identical) and b[name].read_bytes() != c[name].read_bytes():
            raise ValueError(f"Non-product binary mismatch: {name}")
    return {variant: {name: hashlib.sha256(p.read_bytes()).hexdigest() for name, p in files.items()}
            for variant, files in (("baseline", b), ("candidate", c))}


def verify_process(data, private_root):
    if data["runtimeVersion"] != RUNTIME:
        raise ValueError(f"Wrong executing runtime: {data['runtimeVersion']}")
    if {Path(p).parent.parent.name for p in data["frameworkLocations"]} != {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"}:
        raise ValueError("Both executing framework locations are required")
    for path in data["frameworkLocations"]:
        if not Path(path).is_relative_to(private_root / "shared") or RUNTIME not in Path(path).parts:
            raise ValueError(f"Wrong executing framework path: {path}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--build-dotnet", type=Path, required=True)
    parser.add_argument("--runtime-dotnet", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--experiment", choices=("full", "client-only"), default="full")
    parser.add_argument("--client-runtime-profile", choices=("fullopts", "default-pgo"), default="fullopts")
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
        "sdk": actual_sdk, "runtimes": runtimes, "availableCpus": available, "affinity": affinity,
        "experiment": args.experiment, "clientRuntimeProfile": args.client_runtime_profile,
        "runner": {k: os.environ.get(k) for k in ("RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT")},
        "scope": ("Client-only binary ablation; common candidate runtime/server; not product acceptance."
                  if args.experiment == "client-only" else
                  "Successful scalar streams: two-logical-CPU loopback TCP and short-circuit allocation/JIT diagnostics. Not all RPC, AOT, or the final 3.0 matrix."),
        "interpretation": "All samples retained; no added performance threshold. Zero allocation and noisy null results do not establish performance equivalence.",
    })
    for name, command in (("build-dotnet-info", [build_dotnet, "--info"]),
                          ("runtime-dotnet-info", [runtime_dotnet, "--info"]),
                          ("uname", ["uname", "-a"]), ("cpu", ["lscpu"])):
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
    probe_args = ["--probe-source", source / "tcp/ablation-probe"] if args.experiment == "client-only" else []
    run([sys.executable, source / "tcp/prepare.py", *shared_args, *probe_args,
         "--baseline-generator", roots["baseline"] / "src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll",
         "--output", tcp], output / "tcp-prepare.log", tools_root)
    if args.experiment == "client-only":
        from client_ablation import run_client_ablation
        run_client_ablation(tcp, output, runtime_dotnet, build_dotnet, affinity, tool_sha, args.client_runtime_profile)
        return
    revisions = ["--baseline-revision", BASELINE, "--candidate-revision", CANDIDATE]
    run([sys.executable, source / "scalar/run.py", *shared_args, *revisions,
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
    try:
        run([sys.executable, source / "tcp/run_matrix.py", "--prepared", tcp, "--output", tcp_results,
             *revisions, "--dotnet", runtime_dotnet, "--rounds", "6", "--warmup", "3", "--seconds", "6",
             "--workers", "1", "--items", "1,100", "--modes", "static-intercepted,dynamic-intercepted",
             "--affinity", affinity], output / "tcp-run.log", tools_root, environment)
    except Exception:
        if (tcp_results / "runs").exists():
            run([sys.executable, source / "tcp/summarize.py", "--input", tcp_results],
                output / "tcp-summary.log", tools_root, check=False)
        raise
    else:
        run([sys.executable, source / "tcp/summarize.py", "--input", tcp_results],
            output / "tcp-summary.log", tools_root)
    raw = [p for p in (tcp_results / "runs").glob("*.json") if not p.name.endswith(".host.json")]
    if len(raw) != 48:
        raise ValueError(f"Expected 48 TCP samples; found {len(raw)}")
    for path in raw:
        verify_process(json.loads(path.read_text()), runtime_dotnet.parent)
    scalar_results = output / "scalar"
    run([sys.executable, source / "scalar/run.py", "--prepared", scalar, "--output", scalar_results,
         "--runtime-dotnet", runtime_dotnet, "--allocation-repeats", "5", "--affinity", affinity],
        output / "scalar-run.log", tools_root, environment)
    scalar_environments = list(scalar_results.glob("*/*/run-*/environment.json"))
    if len(scalar_environments) != 14:
        raise ValueError(f"Expected 14 scalar process records; found {len(scalar_environments)}")
    for path in scalar_environments:
        verify_process(json.loads(path.read_text()), runtime_dotnet.parent)
    write(output / "complete.json", {"tcpSamples": 48, "scalarFullOptsProcesses": 10,
                                      "scalarTieredDiagnosticProcesses": 4, "toolRevision": tool_sha})


if __name__ == "__main__":
    main()
