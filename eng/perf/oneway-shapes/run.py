#!/usr/bin/env python3
"""Investigation-only exact-baseline A/C/B full-RPC evidence; never edits production HEAD."""
import argparse
import hashlib
import itertools
import json
import math
import os
from pathlib import Path
import random
import shutil
import statistics
import subprocess
import time

BASELINE = "553854f56c33a6e02ef225c24f558e5e92037aa1"
ARMS = {"A": None, "C": "internal-split.patch", "B": "static-entries.patch"}
PROJECT = Path("test/SharpLink.OneWayEvidence/SharpLink.OneWayEvidence.csproj")


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n")


def run(command, log, *, cwd=None, env=None, timeout=600):
    log.parent.mkdir(parents=True, exist_ok=True)
    start = time.perf_counter()
    status = {"command": list(map(str, command)), "cwd": str(cwd), "startedUtc": time.time()}
    with log.open("w") as stream:
        try:
            completed = subprocess.run(command, cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT,
                                       timeout=timeout, check=False)
            status["exitCode"] = completed.returncode
        except subprocess.TimeoutExpired:
            status.update(exitCode=124, error="timeout; sample retained")
        except OSError as error:
            status.update(exitCode=127, error=str(error))
    status["elapsedSeconds"] = time.perf_counter() - start
    write(log.with_suffix(".status.json"), status)
    return status


def hash_files(root):
    return {str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(root.rglob("*")) if path.is_file() and not {"bin", "obj"}.intersection(path.parts)}


def checked(command, log, **kwargs):
    status = run(command, log, **kwargs)
    if status["exitCode"]:
        raise RuntimeError(f"Command failed; complete evidence in {log}")
    return status


def prepare(args, root, output, dotnet):
    source = Path(__file__).resolve().parents[3]
    manifest = {"baselineSha": BASELINE, "harnessSha": subprocess.check_output(
        ["git", "rev-parse", "HEAD"], cwd=source, text=True).strip(),
        "harnessFiles": hash_files(source / PROJECT.parent), "arms": {}, "arguments": vars(args)}
    write(output / "provenance.json", manifest)
    for arm, patch in ARMS.items():
        checkout = root / arm
        checked(["git", "clone", "--no-hardlinks", "--no-checkout", str(source), str(checkout)], output / f"build/{arm}-clone.log")
        checked(["git", "checkout", "--detach", BASELINE], output / f"build/{arm}-checkout.log", cwd=checkout)
        if patch:
            patch_path = source / "eng/perf/oneway-shapes" / patch
            checked(["git", "apply", "--check", str(patch_path)], output / f"build/{arm}-patch-check.log", cwd=checkout)
            checked(["git", "apply", str(patch_path)], output / f"build/{arm}-patch.log", cwd=checkout)
        shutil.copytree(source / PROJECT.parent, checkout / PROJECT.parent,
                        ignore=shutil.ignore_patterns("bin", "obj"))
        arm_manifest = {"baselineSha": BASELINE, "patch": patch,
                        "patchSha256": hashlib.sha256(patch_path.read_bytes()).hexdigest() if patch else None,
                        "harnessFiles": hash_files(checkout / PROJECT.parent), "builds": {}}
        assert arm_manifest["harnessFiles"] == manifest["harnessFiles"]
        manifest["arms"][arm] = arm_manifest
        write(output / "provenance.json", manifest)
        diff = subprocess.check_output(["git", "diff", "--", "src"], cwd=checkout)
        (output / f"build/{arm}-production.patch").write_bytes(diff)
        for runtime in (["jit", "aot"] if args.aot else ["jit"]):
            publish = output / "images" / arm / runtime
            command = [dotnet, "publish", str(PROJECT), "-c", "Release", "-r", "linux-x64", "-o", str(publish), "-v", "minimal"]
            command += ["--self-contained", "true", "-p:PublishAot=true"] if runtime == "aot" else ["--self-contained", "false", "-p:PublishAot=false"]
            if args.offline:
                command += ["-p:NuGetAudit=false", "-p:RestoreIgnoreFailedSources=true", "-p:BuildInParallel=false", "-p:RestoreDisableParallel=true", "-m:1"]
            status = run(command, output / f"build/{arm}-{runtime}.log", cwd=checkout, timeout=1200)
            status["files"] = {str(p.relative_to(publish)): p.stat().st_size for p in publish.rglob("*") if p.is_file()}
            status["imageBytesExcludingDebug"] = sum(size for name, size in status["files"].items() if not name.endswith((".dbg", ".pdb")))
            status["executableBytes"] = status["files"].get("SharpLink.OneWayEvidence")
            arm_manifest["builds"][runtime] = status
            write(output / "provenance.json", manifest)
            if status["exitCode"]:
                continue
            generated = checkout / PROJECT.parent / "obj/generated"
            shutil.copytree(generated, output / "codegen" / arm / runtime / "generated", dirs_exist_ok=True)
            if runtime == "aot":
                executable = publish / "SharpLink.OneWayEvidence"
                run(["size", "-A", str(executable)], output / f"codegen/{arm}/aot/sections.log")
                debug = publish / "SharpLink.OneWayEvidence.dbg"
                run(["nm", "-S", "-C", "--defined-only", str(debug if debug.exists() else executable)], output / f"codegen/{arm}/aot/symbols.log")
    return manifest


def scenarios(args):
    cases = [(transport, streams, batch, "none") for transport in ("tcp", "shm")
             for streams in (0, 1, 2) for batch in (1, 8)]
    if args.features:
        cases += [(transport, streams, 1, feature) for transport in ("tcp", "shm") for streams in (0, 1, 2)
                  for feature in ("deadline", "token", "interceptor", "telemetry", "endpoint", "endpoint,admission",
                                  "deadline,token,interceptor,telemetry,endpoint,admission")]
    return cases


def samples(args, output, manifest, dotnet):
    rng = random.Random(args.seed)
    orders = list(itertools.permutations(ARMS))
    rng.shuffle(orders)
    schedule = []
    # Every six rounds covers all positions/pairs equally; five rounds is near-balanced.
    for runtime in (["jit", "aot"] if args.aot else ["jit"]):
        rounds = args.rounds if runtime == "jit" else args.aot_rounds
        for round_index in range(rounds):
            cases = scenarios(args)
            rng.shuffle(cases)
            for case_index, (transport, streams, batch, features) in enumerate(cases):
                for position, arm in enumerate(orders[(round_index + case_index) % len(orders)]):
                    key = f"{runtime}-{transport}-s{streams}-b{batch}-{features}"
                    schedule.append(dict(runtime=runtime, round=round_index, position=position, arm=arm, key=key,
                                         transport=transport, streams=streams, batch=batch, features=features))
    write(output / "schedule.json", schedule)
    records = []
    for sample in schedule:
        stem = f"{sample['key']}-r{sample['round']}-{sample['arm']}"
        result_path = output / "samples" / f"{stem}.json"
        build = manifest["arms"][sample["arm"]]["builds"][sample["runtime"]]
        if build["exitCode"]:
            records.append(dict(sample, status="build-failed", exitCode=build["exitCode"]))
            continue
        image = output / "images" / sample["arm"] / sample["runtime"] / "SharpLink.OneWayEvidence"
        command = [str(image)] if sample["runtime"] == "aot" else [dotnet, str(image) + ".dll"]
        command += [sample["transport"], str(sample["streams"]), str(sample["batch"]), str(args.seconds),
                    str(args.warmup), "4", sample["features"], str(result_path)]
        environment = dict(os.environ, ONEWAY_ARM=sample["arm"], ONEWAY_SOURCE_SHA=BASELINE,
                           DOTNET_TieredCompilation="0", DOTNET_ReadyToRun="0")
        status = run(command, output / "logs" / f"{stem}.log", env=environment,
                     timeout=args.seconds + args.warmup + 90)
        result = json.loads(result_path.read_text()) if result_path.exists() else {}
        records.append(dict(sample, status=result.get("status", "failed"), exitCode=status["exitCode"], result=result))
        write(output / "records.json", records)
    # Diagnostic disassembly is deliberately outside measured samples.
    for arm in ARMS:
        if manifest["arms"][arm]["builds"]["jit"]["exitCode"]:
            continue
        for streams in (0, 1, 2):
            env = dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_ReadyToRun="0", COMPlus_JitDisasm="*OneWay*:MoveNext *InvokeOneWay*")
            run([dotnet, str(output / "images" / arm / "jit/SharpLink.OneWayEvidence.dll"), "shm", str(streams), "8", "0.1", "0.1", "4", "none",
                 str(output / f"codegen/{arm}/jit/s{streams}.json")], output / f"codegen/{arm}/jit/s{streams}.log", env=env, timeout=90)
    return records


def summarize(records, output):
    rows = []
    for key in sorted({r["key"] for r in records}):
        for numerator, denominator in (("C", "A"), ("B", "A"), ("B", "C")):
            relevant = [r for r in records if r["key"] == key]
            paired = []
            for round_index in sorted({r["round"] for r in relevant}):
                pair = {r["arm"]: r for r in relevant if r["round"] == round_index}
                if all(arm in pair and pair[arm]["status"] == "passed" and pair[arm]["exitCode"] == 0 for arm in (numerator, denominator)):
                    paired.append((pair[numerator]["result"], pair[denominator]["result"]))
            row = dict(scenario=key, comparison=f"{numerator}/{denominator}", validPairs=len(paired), metrics={})
            for metric in ("nsPerCall", "cpuNsPerCall", "allocatedBytesPerCall"):
                ratios = [a[metric] / b[metric] for a, b in paired if b[metric] > 0 and a[metric] > 0]
                if not ratios:
                    continue
                logs = [math.log(r) for r in ratios]
                mean = statistics.mean(logs)
                # Student t 95% interval on paired log ratios; five rounds uses df=4.
                t95 = {1: 12.706, 2: 4.303, 3: 3.182, 4: 2.776, 5: 2.571, 6: 2.447,
                       7: 2.365, 8: 2.306, 9: 2.262, 10: 2.228}.get(len(logs) - 1, 2.228)
                margin = t95 * statistics.stdev(logs) / math.sqrt(len(logs)) if len(logs) > 1 else None
                row["metrics"][metric] = dict(pairedRatios=ratios, geometricMeanRatio=math.exp(mean),
                    minRatio=min(ratios), maxRatio=max(ratios),
                    confidence95=[math.exp(mean - margin), math.exp(mean + margin)] if margin is not None else None)
            rows.append(row)
    summary = dict(purpose="Full-RPC correctness/regression sanity; no full-RPC gain is required to justify a local optimization.",
                   failedSamples=sum(r["status"] != "passed" or r.get("exitCode", 1) != 0 for r in records),
                   totalSamples=len(records), comparisons=rows)
    write(output / "summary.json", summary)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--work-root", required=True)
    parser.add_argument("--rounds", type=int, default=1)
    parser.add_argument("--seconds", type=float, default=2)
    parser.add_argument("--warmup", type=float, default=1)
    parser.add_argument("--seed", type=int, default=729)
    parser.add_argument("--aot", action="store_true")
    parser.add_argument("--aot-rounds", type=int, default=1)
    parser.add_argument("--features", action="store_true", help="Add independent feature and all-enabled smoke cases; no full factorial.")
    parser.add_argument("--offline", action="store_true", help="Local cached-package smoke only; disables NuGet audit explicitly in provenance.")
    args = parser.parse_args()
    if min(args.rounds, args.aot_rounds) < 1 or args.seconds <= 0 or args.warmup < 0:
        parser.error("rounds and seconds must be positive; warmup must be nonnegative")
    output, root = Path(args.output).resolve(), Path(args.work_root).resolve()
    output.mkdir(parents=True, exist_ok=False)
    root.mkdir(parents=True, exist_ok=False)
    dotnet = os.environ.get("DOTNET", "dotnet")
    checked([dotnet, "--info"], output / "environment/dotnet.log")
    run(["uname", "-a"], output / "environment/uname.log")
    run(["lscpu"], output / "environment/lscpu.log")
    manifest = prepare(args, root, output, dotnet)
    summary = summarize(samples(args, output, manifest, dotnet), output)
    print(json.dumps({k: v for k, v in summary.items() if k != "comparisons"}, indent=2))
    return 1 if summary["failedSamples"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
