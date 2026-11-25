#!/usr/bin/env python3
"""Run one frozen D6 shard serially on its registered host."""

import argparse
import datetime
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import time
from types import SimpleNamespace

from d6_paired_steady import aa_decision, ab_decision, evaluate_aa, matrix, read_sample, require_report_population
from d6_paired_steady import sample_names, sample_plan
from d6_paired_short_stream import OPERATIONS, read_short_stream, stream_plan
from d6_paired_cold import cold_plan, read_cold
from d6_paired_dynamic_stream import dynamic_stream_plan, read_dynamic_stream
from d6_paired_replacement import TRANSPORT as REPLACE_TRANSPORT, invocation as replacement_invocation
from d6_paired_replacement import read_replacement, replace_plan, verify_dynamic_preflight, verify_no_tracing


PROJECT = "test/SharpLink.LoadTest/SharpLink.LoadTest.csproj"
BINARY = "test/SharpLink.LoadTest/bin/Release/net10.0/SharpLink.LoadTest.dll"
MAX_SECONDS = 55 * 60  # leave time for raw evidence upload within the 60-minute job
TEST_ONLY_PREFIXES = ("test/SharpLink.LoadTest/", "test/SharpLink.LoadTest.Tests/",
                      "test/SharpLink.StreamLoadTest/", "test/SharpLink.StreamLoadTest.Tests/",
                      "eng/native-version-negotiation-study/")
WORKFLOWS = {".github/workflows/999-paired-steady.yml", ".github/workflows/999-paired-short-streams.yml",
             ".github/workflows/999-paired-cold.yml", ".github/workflows/999-paired-replacement.yml",
             ".github/workflows/999-paired-dynamic-short-streams.yml", ".github/workflows/999-paired-windows-steady.yml",
             ".github/workflows/999-paired-controlled-steady.yml", ".github/workflows/999-paired-route-repair.yml"}
WORKLOAD_FIXTURES = {
    "test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.QualifiedReplacement.cs",
    "test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.QualifiedReplacementArm.cs",
    "test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.QualifiedReplacementTiming.cs",
    "test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.QualifiedDynamicShortStreams.cs",
    "test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.QualifiedDynamicShortStreamTiming.cs",
    "test/SharpLink.DynamicServices/DynamicPluginService.cs",
}
REPLACE_ENV = ("SHARPLINK_999_REPLACE_REPORT", "SHARPLINK_999_REPLACE_SOURCE", "SHARPLINK_999_REPLACE_CADENCE")
REPLACE_INTERCEPTOR_ENV = "SHARPLINK_999_REPLACE_INTERCEPTOR"
DYNAMIC_ENV = tuple("SHARPLINK_999_DYNAMIC_" + name for name in ("REPORT", "SOURCE", "FAMILY", "ITEMS"))


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def verified_checkout(root, expected, product):
    matrix.require(matrix.git(root, "rev-parse", "HEAD") == expected, "unexpected overlay commit")
    matrix.require(not matrix.git(root, "status", "--porcelain"), "dirty performance checkout")
    subprocess.run(["git", "-C", str(root), "merge-base", "--is-ancestor", product, expected], check=True)
    files = matrix.git(root, "diff", "--name-only", f"{product}..{expected}").splitlines()
    matrix.require(bool(files) and all(path in WORKFLOWS or path in WORKLOAD_FIXTURES or
                                       path.startswith(TEST_ONLY_PREFIXES) for path in files),
                   "the measured overlay contains non-study product changes")


def save(root, filename, value):
    matrix.write_new(root / filename, value)


def linux_scheduler_sample():
    def read(path, *, first_line=False):
        try:
            with Path(path).open() as source:
                return (source.readline() if first_line else source.read()).strip()
        except OSError:
            return None

    # cpu.stat is scoped to the runner's cgroup when v2 exposes its membership.
    membership = read("/proc/self/cgroup") or ""
    group = next((line.split("::", 1)[1] for line in membership.splitlines()
                  if line.startswith("0::")), None)
    cpu_stat = "/sys/fs/cgroup/" + group.lstrip("/") + "/cpu.stat" if group is not None else None
    return dict(aggregate_cpu=read("/proc/stat", first_line=True), cgroup=group,
                cgroup_cpu=read(cpu_stat) if cpu_stat is not None else None,
                cpu_pressure=read("/proc/pressure/cpu"))


def execute(command, cwd, env, logfile, timeout):
    record_host = sys.platform == "linux" and env.get("SHARPLINK_D6_ISOLATED_LINUX") == "1"
    before = linux_scheduler_sample() if record_host else None
    started = time.monotonic_ns()
    with logfile.open("x") as log:
        try:
            proc = subprocess.run(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
            exit_code = proc.returncode
        except subprocess.TimeoutExpired:
            exit_code = 124
    receipt = dict(command=command, exit_code=exit_code, timeout_s=timeout, started_ns=started,
                   finished_ns=time.monotonic_ns(), finished_utc=utc_now(), log=str(logfile))
    if record_host:
        receipt["linux_scheduler"] = dict(before=before, after=linux_scheduler_sample())
    return receipt


def host_snapshot(runner_os):
    if runner_os == "Linux":
        boot = Path("/proc/sys/kernel/random/boot_id").read_text().strip()
        affinity = sorted(os.sched_getaffinity(0))
        matrix.require(boot and affinity, "missing Linux host boot/affinity evidence")
        return dict(boot_id=boot, affinity=affinity)

    matrix.require(runner_os == "Windows", "unexpected runner platform")
    # Read the actual controller process affinity, not the helper process defaults.
    command = (
        "$ErrorActionPreference = 'Stop';"
        "$boot = (Get-CimInstance -ClassName Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks;"
        f"$mask = (Get-Process -Id {os.getpid()}).ProcessorAffinity.ToInt64();"
        "[Console]::WriteLine($boot); [Console]::WriteLine($mask);"
    )
    boot, affinity = map(int, subprocess.check_output(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
        text=True, timeout=15).splitlines())
    matrix.require(boot > 0 and affinity != 0, "missing Windows host boot/affinity evidence")
    return dict(boot_id=f"windows-boot-ticks:{boot}", affinity=affinity)


def require_local_hyperv_shutdown(receipt):
    recorded = matrix.read_json(receipt)
    matrix.require(recorded["status"] == "quiescent", "the local guest shutdown has not completed")
    running = subprocess.check_output(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
         "[Console]::WriteLine(@(Get-Process -Name vmwp -ErrorAction SilentlyContinue).Count)"],
        text=True, timeout=15).strip()
    matrix.require(running == "0", "a Hyper-V worker is still active on the local benchmark host")
    return recorded


def workload_commands(root, output, workload, project, binary, native_aot, *, runner_os="Linux"):
    if native_aot:
        rid, extension = {"Linux": ("linux-x64", ""), "Windows": ("win-x64", ".exe")}[runner_os]
        publish = output / "publish"
        executable = publish / (workload + extension)
        # Use the OS-specific release publish, not the ordinary JIT apphost.
        return (["dotnet", "publish", project, "-c", "Release", "-r", rid,
                 "/p:PublishAot=true", "-o", str(publish), "-v", "minimal"], executable, [str(executable)])
    executable = root / binary
    return ["dotnet", "build", project, "-c", "Release", "-v", "minimal"], executable, ["dotnet", str(executable)]


def verify_steady_preflight(log, runtime, transport, exit_code):
    verified = [line for line in log.splitlines() if line.startswith("QUALIFIED32_VERIFIED ")]
    name = {"tcp": "Tcp", "sharedmemory": "SharedMemory"}[transport]
    expected = f"QUALIFIED32_VERIFIED runtime={runtime} transport={name} unary=16 oneway-remote=16"
    matrix.require(exit_code == 0 and verified == [expected],
                   "wrong runtime or fixed-count generated Unary/OneWay workload failed")


def verify_stream_preflight(log, runtime, exit_code):
    verified = [line for line in log.splitlines() if line.startswith("SHORT_STREAM_VERIFIED ")]
    expected = [f"SHORT_STREAM_VERIFIED {shape} items={items} runtime={runtime}"
                for shape in OPERATIONS for items in (32, 256)]
    matrix.require(exit_code == 0 and verified == expected,
                   "wrong runtime or fixed-count generated short-stream workload failed")


def run_phases(root, cells, sources, launch, *, reader=read_sample):
    hosts, baseline, statuses = set(), {}, []

    def read_pair(cell, cell_dir, phase, index, pair):
        samples = []
        for arm, filename in pair:
            path = cell_dir / filename
            launch(cell, phase, index, arm, path)
            samples.append(reader(path, arm, sources[arm], cell, hosts))
        return samples

    # Every cell must complete its planned A/A before any candidate runs.
    for cell in cells:
        key = cell["operation"]
        cell_dir = root / key
        cell_dir.mkdir(exist_ok=True)
        planned = sample_names("aa", cell["aa"])
        measured = []
        for index, pair in enumerate(planned, 1):
            sample = read_pair(cell, cell_dir, "aa", index, pair)
            measured.append(sample)
            if evaluate_aa([sample], cell["aa_maximum_deviation"])["status"] != "AA_PASS":
                break
        require_report_population(cell_dir, "aa", planned[:len(measured)])
        finding = aa_decision(cell, measured)
        save(cell_dir, "decision-aa.json", finding)
        if finding["aa"]["status"] != "AA_PASS":
            require_report_population(cell_dir, "ab", ())
            return "INCONCLUSIVE_AA"
        baseline[key] = finding

    for cell in cells:
        key = cell["operation"]
        cell_dir = root / key
        planned = sample_names("ab", cell["ab"])
        measured = [read_pair(cell, cell_dir, "ab", index, pair) for index, pair in enumerate(planned, 1)]
        require_report_population(cell_dir, "ab", planned)
        finding = ab_decision(baseline[key], measured)
        save(cell_dir, "decision-ab.json", finding)
        statuses.append(finding["ab"]["status"])
    if "REGRESSION" in statuses:
        return "REGRESSION"
    if "INCONCLUSIVE" in statuses:
        return "INCONCLUSIVE_AB"
    return "SUPPORTS_NEXT_STAGE"


def run(args):
    runner_os = platform.system()
    local_receipt = getattr(args, "local_hyperv_receipt", None)
    if local_receipt is not None:
        matrix.require(runner_os == "Windows" and os.environ.get("GITHUB_ACTIONS") != "true",
                       "a local guest shutdown receipt applies only to the Windows desktop")
        require_local_hyperv_shutdown(local_receipt)
    else:
        matrix.require(runner_os in ("Linux", "Windows") and os.environ.get("GITHUB_ACTIONS") == "true"
                       and os.environ.get("RUNNER_OS") == runner_os,
                       "D6 timing requires the registered GitHub runner or a confirmed local guest shutdown")
    # Start only after the exact candidate has qualified for performance admission.
    stop_at = time.monotonic() + MAX_SECONDS
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    paths = {"P": args.baseline_dir.resolve(), "N": args.candidate_dir.resolve()}
    sources = {"P": args.baseline_overlay, "N": args.candidate_overlay}
    products = {"P": args.baseline_product, "N": args.candidate_product}
    short = args.stream_operation is not None
    matrix.require(short == (args.stream_items is not None), "stream operation and item count are both required")
    cold = args.cold_first_rpc
    replacement = args.replace_cadence is not None
    dynamic = args.dynamic_short_stream
    runtime = args.runtime
    aot = runtime == "aot"
    steady = not (short or cold or replacement)
    matrix.require(args.steady_operation is None or steady, "select a registered steady cell")
    matrix.require(local_receipt is None or (steady and not aot), "the initial local lane is static JIT steady")
    matrix.require(not aot or (not replacement and not dynamic and platform.machine().lower() in ("x86_64", "amd64")),
                   "the paired NativeAOT lanes cover generated static contracts on x64")
    matrix.require(runner_os == "Linux" or not (replacement or dynamic),
                   "dynamic JIT lanes are registered on Linux; Windows uses generated static contracts")
    matrix.require(not replacement or (not cold and not short and args.concurrency == 1),
                   "replacement independently exercises two calls on one physical connection")
    matrix.require(not dynamic or (short and not cold and not replacement and args.concurrency == 1),
                   "dynamic streams require an exact JIT short-stream shape at c1")
    matrix.require(replacement or dynamic or args.replace_interceptor == "none",
                   "select a registered dynamic cell for interceptor evidence")
    matrix.require(not cold or (not short and args.concurrency == 1), "cold first RPC uses one client, not the stream runner")
    if replacement or dynamic:
        workload = "SharpLink.IntegrationTests"
        project = f"test/{workload}/{workload}.csproj"
        binary = f"test/{workload}/bin/Release/net10.0/{workload}.dll"
        if replacement:
            cells = [replace_plan(args.transport, args.replace_cadence, args.replace_interceptor)]
            reader = read_replacement
            scope = "same-Session A update and B collateral latency; public timing, not wire phase evidence"
        else:
            cells = [dynamic_stream_plan(args.transport, args.stream_operation, args.stream_items, args.replace_interceptor)]
            reader = read_dynamic_stream
            scope = "prepared JIT dynamic short streams: application first progress, business completion and owner exit"
    elif cold:
        cells = [cold_plan(args.transport)]
        workload, project, binary, reader = "SharpLink.LoadTest", PROJECT, BINARY, read_cold
        scope = f"cold first RPC ({runtime}) on a fresh Session; first sample in each process recorded separately"
    elif short:
        cells = [stream_plan(args.transport, args.concurrency, args.stream_operation, args.stream_items)]
        workload = "SharpLink.StreamLoadTest"
        project = f"test/{workload}/{workload}.csproj"
        binary = f"test/{workload}/bin/Release/net10.0/{workload}.dll"
        reader = read_short_stream
        scope = f"short-stream ({runtime}) application progress/business/owner-exit, not release acceptance"
    else:
        if args.steady_operation is None:
            operations = ("unary32", "oneway32")
            rate = 0  # Keep the original combined raw study available for historical evidence.
        else:
            operations = (args.steady_operation,)
            rate = 80_000 if args.steady_operation == "oneway32" else 0
        cells = [sample_plan(args.transport, args.concurrency, op, oneway_rate=rate) for op in operations]
        workload, project, binary, reader = "SharpLink.LoadTest", PROJECT, BINARY, read_sample
        scope = f"steady {runtime}, not release acceptance"
    matrix.require("SHARPLINK_COMMIT" not in os.environ, "do not pre-set the measured source globally")
    matrix.require(not any(key in os.environ for key in (*REPLACE_ENV, *DYNAMIC_ENV, REPLACE_INTERCEPTOR_ENV)),
                   "do not globally set dynamic workload options")
    environment = os.environ.copy()
    # TUnit's HTML reporter samples the RPC activity sources. Match the intended
    # consumer configuration rather than comparing calls under the test's trace.
    replacement_environment = {
        REPLACE_INTERCEPTOR_ENV: args.replace_interceptor,
        "TUNIT_DISABLE_HTML_REPORTER": "true",
        "SHARPLINK_999_REPLACE_REQUIRE_NO_TRACING": "true",
    }
    snapshot = host_snapshot(runner_os)
    location = (dict(runner="windows-local", run=output.name, attempt=1, job="steady",
                     shutdown_receipt=str(local_receipt.resolve())) if local_receipt is not None else
                dict(runner=os.environ["RUNNER_NAME"], run=os.environ["GITHUB_RUN_ID"],
                     attempt=os.environ["GITHUB_RUN_ATTEMPT"], job=os.environ["GITHUB_JOB"]))
    host = dict(node=platform.node(), runner_os=runner_os, **snapshot, **location,
                sdk=subprocess.check_output(["dotnet", "--version"], text=True, cwd=paths["N"]).strip(),
                jit_gc={key: os.environ.get(key) for key in ("DOTNET_TieredPGO", "DOTNET_TieredCompilation", "DOTNET_gcServer",
                                                            "COMPlus_TieredPGO", "COMPlus_gcServer")})
    save(output, "frozen-shard.json", dict(scope=scope, cells=cells, host=host, runtime=runtime,
                                           aa_stop="first complete pair exceeding a frozen limit; all planned pairs required to advance",
                                           overlays=sources, product_heads=products, registered_utc=utc_now()))

    def remaining(maximum):
        timeout = min(maximum, stop_at - time.monotonic())
        matrix.require(timeout > 0, "timed shard exhausted its pre-registered job budget")
        return timeout

    try:
        entrypoints = {}
        for arm, root in paths.items():
            verified_checkout(root, sources[arm], products[arm])
            matrix.require(subprocess.check_output(["dotnet", "--version"], text=True, cwd=root).strip() == host["sdk"],
                           "arms resolved different SDKs")
            build, executable, entrypoints[arm] = workload_commands(
                root, output / arm, workload, project, binary, aot, runner_os=runner_os)
            if runner_os == "Linux" and environment.get("SHARPLINK_D6_ISOLATED_LINUX") == "1":
                # A fresh hosted study should not leave compilation workers during timing.
                build.append("--disable-build-servers")
            entry = execute(build, root,
                            environment, output / f"build-{arm}.log", remaining(480))
            save(output, f"build-{arm}.json", entry)
            matrix.require(entry["exit_code"] == 0, f"{arm}: restore or Release build failed")
            if aot:
                matrix.require(executable.is_file() and (runner_os == "Windows" or os.access(executable, os.X_OK)),
                               f"{arm}: NativeAOT publish did not produce an executable")
            matrix.capture(SimpleNamespace(root=root, output=output / arm / "provenance.json",
                                           binary=executable, runtime=runtime, project=workload))
            if steady or cold:
                logfile = output / f"fixed-count-{arm}.log"
                cmd = [*entrypoints[arm], "--mode", "local", "--bind-ip", "127.0.0.1", "--transport", args.transport,
                       "--min-connections", "1", "--max-connections", "1", "--metrics-port", "0",
                       "--verify-qualified32"]
                dry = execute(cmd, root, environment, logfile, remaining(30))
                save(output, f"fixed-count-{arm}.json", dry)
                verify_steady_preflight(logfile.read_text(), runtime, args.transport, dry["exit_code"])
            if short and not dynamic:
                logfile = output / f"fixed-count-{arm}.log"
                dry = execute([*entrypoints[arm], "--mode", "local", "--bind-ip", "127.0.0.1",
                               "--transport", args.transport, "--verify-short-streams"], root, environment, logfile, remaining(45))
                save(output, f"fixed-count-{arm}.json", dry)
                verify_stream_preflight(logfile.read_text(), runtime, dry["exit_code"])
            dry_env = environment | replacement_environment
            if replacement:
                logfile = output / f"fixed-count-{arm}.log"
                cmd = [*entrypoints[arm], *replacement_invocation(args.transport, measured=False),
                       "--results-directory", str(output / "test-reports" / f"fixed-count-{arm}")]
                dry = execute(cmd, root, dry_env, logfile, remaining(45))
                save(output, f"fixed-count-{arm}.json", dry)
                transport = REPLACE_TRANSPORT[args.transport][0]
                expected = [f"REPLACE_WORKLOAD_VERIFIED transport={transport} replacements={n} b-same-session=true "
                            f"interceptor={args.replace_interceptor}" for n in (1, 2)]
                verified = [line.strip() for line in logfile.read_text().splitlines() if "REPLACE_WORKLOAD_VERIFIED " in line]
                matrix.require(dry["exit_code"] == 0 and verified == expected, f"{arm}: fixed replacement validation failed")
                verify_no_tracing(logfile.read_text())
            if dynamic or args.replace_cadence == 0:
                stream_log = output / f"dynamic-short-preflight-{arm}.log"
                cmd = [*entrypoints[arm], *replacement_invocation(args.transport, measured=False, dynamic_streams=True),
                       "--results-directory", str(output / "test-reports" / f"dynamic-short-{arm}")]
                result = execute(cmd, root, dry_env, stream_log, remaining(45))
                save(output, f"dynamic-short-preflight-{arm}.json", result)
                verify_dynamic_preflight(stream_log.read_text(), args.transport, args.replace_interceptor, result["exit_code"])
                verify_no_tracing(stream_log.read_text())

        def launch(cell, phase, index, arm, path):
            env = dict(environment, SHARPLINK_COMMIT=sources[arm])
            if replacement:
                argv = [*replacement_invocation(args.transport, measured=True),
                        "--results-directory", str(output / "test-reports" / path.stem)]
                env.update(zip(REPLACE_ENV, (str(path), sources[arm], str(cell["cadence"]))))
                env.update(replacement_environment)
                limit = 125
            elif dynamic:
                argv = [*replacement_invocation(args.transport, measured=True, dynamic_streams=True),
                        "--results-directory", str(output / "test-reports" / path.stem)]
                env.update(replacement_environment)
                env.update(zip(DYNAMIC_ENV, (str(path), sources[arm], cell["family"], str(cell["items"]))))
                limit = 135
            else:
                argv = [item for name, value in cell["arguments"].items() for item in (f"--{name}", str(value))]
                argv += ["--json-output", str(path)]
                limit = 115 if cold else cell["arguments"]["duration"] + 41 if short else 100
            child = execute([*entrypoints[arm], *argv],
                            paths[arm], env, path.with_suffix(".log"), remaining(limit))
            save(path.parent / "invocations", path.name, dict(**child, arm=arm, phase=phase, pair=index))
            matrix.require(child["exit_code"] == 0, f"{path.name}: failed execution; not retried")

        if local_receipt is not None:
            require_local_hyperv_shutdown(local_receipt)
        status = run_phases(output, cells, sources, launch, reader=reader)
        if local_receipt is not None:
            require_local_hyperv_shutdown(local_receipt)
        for arm, root in paths.items():
            verified_checkout(root, sources[arm], products[arm])
        matrix.require(host_snapshot(runner_os) == snapshot, "runner boot identity or affinity changed during the shard")
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        save(output, "outcome.json", dict(status="INCONCLUSIVE_EXECUTION", error=str(error), hosted=host, at=utc_now()))
        raise
    save(output, "outcome.json", dict(status=status, hosted=host, at=utc_now()))
    print(json.dumps(dict(status=status, scope=scope, output=str(output))))
    return 0 if status == "SUPPORTS_NEXT_STAGE" else 3


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("transport", choices=("tcp", "sharedmemory"))
    parser.add_argument("concurrency", type=int, choices=(1, 8))
    parser.add_argument("--steady-operation", choices=("unary32", "oneway32"))
    parser.add_argument("--stream-operation", choices=OPERATIONS)
    parser.add_argument("--stream-items", type=int, choices=(32, 256))
    parser.add_argument("--cold-first-rpc", action="store_true")
    parser.add_argument("--dynamic-short-stream", action="store_true")
    parser.add_argument("--runtime", choices=("jit", "aot"), default="jit")
    parser.add_argument("--replace-cadence", type=int, choices=(0, 100, 10000))
    parser.add_argument("--replace-interceptor", choices=("none", "forward"), default="none")
    for arm in ("baseline", "candidate"):
        parser.add_argument(f"--{arm}-dir", type=Path, required=True)
        parser.add_argument(f"--{arm}-overlay", required=True)
        parser.add_argument(f"--{arm}-product", required=True)
    parser.add_argument("--local-hyperv-receipt", type=Path,
                        help="Windows-only JIT steady lane after normal Hyper-V shutdown has completed")
    parser.add_argument("--output", type=Path, required=True)
    return run(parser.parse_args())


if __name__ == "__main__":
    sys.exit(main())
