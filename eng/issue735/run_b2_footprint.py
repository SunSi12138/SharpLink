#!/usr/bin/env python3
"""Matched current-thread allocation appendix for the production B2 grant pilot.

This does not measure retained heap, native memory, CPU, RPC performance or
correctness acceptance. Run only after the separate correctness gate. Every
planned step and failed/partial output is retained; no retry-until-green logic.
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import hashlib
import io
import json
import math
import os
from pathlib import Path
import re
import shutil
import signal
import statistics
import subprocess
import sys
import time
import traceback

BASE = "c7ba1768f31ce7219dc438dc6009347de6806ed5"
INITIAL = "f8ba89121c87a6410a0139c44d7db30eab877f41"
FOOT = "test/SharpLink.FlowGrantFootprintEvidence"
RECIPE = "eng/issue735/run_b2_footprint.py"
PROJECT = FOOT + "/SharpLink.FlowGrantFootprintEvidence.csproj"
CONTROLLER = "src/SharpLink.Runtime/StreamFlowController.cs"
GRANTS = "src/SharpLink.Runtime/StreamFlowController.SendGrants.cs"
POPULATIONS = ("jit-pgo0", "jit-pgo1", "nativeaot")
MODES = {"A": "common-correctness-control", "B": "B2-grant-4096"}
ENV_KEYS = ("DOTNET_TieredPGO", "DOTNET_TieredCompilation", "DOTNET_ReadyToRun", "DOTNET_PROCESSOR_COUNT",
            "COMPlus_TieredPGO", "COMPlus_TieredCompilation", "COMPlus_ReadyToRun")
PROOF_FIELDS = ("ActivatedStreams", "DisposedStreams", "FirstProbeFallbacks", "LocalItems", "OwnerRefills",
                "ExactSlowItems", "RecalledGrantBytes", "RefundedItemBytes")
HEADER = ("phase", "round", "iterations", "total_allocated_bytes", "allocated_bytes_per_iteration")


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256(path: Path) -> str:
    return digest(path.read_bytes())


def content_digest(files: dict) -> str:
    return digest(json.dumps(files, sort_keys=True, separators=(",", ":")).encode())


def write_json(path: Path, value: object) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")
    temporary.replace(path)


def now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def git(work: Path, *arguments: str) -> bytes:
    return subprocess.check_output(["git", *arguments], cwd=work)


def revision(work: Path, value: str) -> str:
    return git(work, "rev-parse", value).decode().strip()


def entries(work: Path, ref: str, *paths: str) -> dict:
    result = {}
    for row in git(work, "ls-tree", "-r", "-z", ref, "--", *paths).split(b"\0"):
        if not row:
            continue
        metadata, filename = row.split(b"\t", 1)
        mode, kind, blob = metadata.decode().split()
        if kind != "blob" or mode not in ("100644", "100755"):
            raise ValueError("Unexpected non-file compiler input: " + filename.decode())
        result[filename.decode()] = blob
    return result


def blob_digest(data: bytes) -> str:
    return hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()


def committed_files(work: Path, ref: str, paths: tuple[str, ...]) -> dict:
    expected = entries(work, ref, *paths)
    files = {}
    for name, blob in expected.items():
        path = work / name
        if path.is_symlink() or blob_digest(path.read_bytes()) != blob:
            raise ValueError("Working bytes differ from the compiled subject commit: " + name)
        files[name] = sha256(path)
    return files


def source_identity(work: Path, ref: str) -> dict:
    files = committed_files(work, ref, ("src",))
    actual = {p.decode() for p in git(work, "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "src").split(b"\0") if p}
    if actual != set(files):
        raise ValueError("Unrecorded production source file set")
    return {"subjectSha": ref, "subjectCommitTreeSha": revision(work, ref + "^{tree}"),
            "runtimeSourceTreeSha": revision(work, ref + ":src"),
            "runtimeSourceContentSha256": content_digest(files), "fileSha256": files}


def build_input_paths(work: Path, ref: str) -> tuple[str, ...]:
    # Include parent build policy for both the new executable and its projects.
    # src/Directory.* is additionally covered by the complete production inventory.
    names = entries(work, ref)
    return tuple(sorted(p for p in names if ("/" not in p or p.startswith("test/Directory.")) and
                        (Path(p).name.startswith("Directory.") or Path(p).name in
                         {"global.json", "NuGet.Config", "NuGet.config", "nuget.config", ".editorconfig"})))


def measurement_files(work: Path) -> dict:
    files = {RECIPE: sha256(work / RECIPE)}
    for path in sorted((work / FOOT).rglob("*")):
        if not path.is_file() or "bin" in path.parts or "obj" in path.parts or path.name == "ModeIdentity.cs":
            continue
        files[path.relative_to(work).as_posix()] = sha256(path)
    return files


def validate_subject_pair(subjects: dict, policy: dict, repo: Path) -> dict:
    a, b = subjects["A"], subjects["B"]
    if policy["correctnessControlSha"] != BASE or policy["initialDevSha"] != INITIAL:
        raise ValueError("Wrong correctness control policy")
    if a["subjectCommitTreeSha"] != policy["correctnessControlCommitTreeSha"] or a["runtimeSourceTreeSha"] != policy["correctnessControlSrcTreeSha"]:
        raise ValueError("Fixed common control tree mismatch")
    old = entries(repo, INITIAL, "src")
    expected = dict(old)
    for name, value in policy["baselineChangedFilesFromInitialDev"].items():
        data = git(repo, "show", BASE + ":" + name)
        if digest(data) != value:
            raise ValueError("Reviewed baseline correctness source mismatch: " + name)
        expected[name] = blob_digest(data)
    if expected != entries(repo, BASE, "src") or len(expected) != 496:
        raise ValueError("Common control contains additional production changes")
    changes = policy["candidateChangedFilesFromCorrectnessControl"]
    allowed = {CONTROLLER, GRANTS, "src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs",
               "src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs"}
    if set(changes) != allowed:
        raise ValueError("Reviewed candidate scope is not the four-file producer grant pilot")
    expected_hashes = dict(a["fileSha256"])
    expected_hashes.update(changes)
    if b["fileSha256"] != expected_hashes or GRANTS in a["fileSha256"]:
        raise ValueError("Actual production bytes differ from the reviewed B2 source; D is not an allowed subject")
    reviewed = entries(repo, policy["reviewedCandidateSourceCommit"], "src")
    if reviewed != entries(repo, b["subjectSha"], "src"):
        raise ValueError("Candidate source tree differs from the separately reviewed source commit")
    pump = {p: value for p, value in a["fileSha256"].items() if Path(p).name.startswith("RpcSession.SendPump")}
    if not pump or any(b["fileSha256"].get(p) != value for p, value in pump.items()):
        raise ValueError("SendPump bytes were changed")
    return {"allBaselineProductionPaths": len(a["fileSha256"]), "allCandidateProductionPaths": len(b["fileSha256"]),
            "changedProductionFiles": changes, "unchangedSendPumpFileSha256": pump,
            "controlIncludesIndependentLeaseSnapshotTerminalFixes": True, "includesD": False}


def environment(population: str) -> dict:
    env = dict(os.environ)
    for key in ENV_KEYS:
        env.pop(key, None)
    env.update(DOTNET_PROCESSOR_COUNT="4", DOTNET_ReadyToRun="0")
    if population != "nativeaot":
        env.update(DOTNET_TieredCompilation="1", DOTNET_TieredPGO=population[-1])
    return env


def expected_proof(arm: str, spec: dict) -> dict:
    if arm == "A":
        return dict.fromkeys(PROOF_FIELDS, 0)
    n, p, s = spec["primaryIterations"], spec["poolControllerIterations"], spec["streamsPerPoolControllerIteration"]
    size, quantum = spec["itemCreditBytes"], spec["grantQuantumBytes"]
    values = (3 * n + 2 * p * s, 3 * n + 2 * p * s, 0, n, 2 * n, n,
              (2 * quantum - 3 * size) * n, 6 * size * n + 2 * p * s * size)
    return dict(zip(PROOF_FIELDS, values))


def parse_sample(path: Path, arm: str, population: str, identity: dict, binary: dict, env: dict, spec: dict) -> dict:
    metadata, table = {}, []
    for line in path.read_text().splitlines():
        if line.startswith("#"):
            parts = line[1:].split("\t")
            if len(parts) != 2 or parts[0] in metadata:
                raise ValueError("Malformed or duplicate footprint metadata")
            metadata[parts[0]] = parts[1]
        elif line:
            table.append(line)
    aot = population == "nativeaot"
    required = dict(identity)
    required.update(schema="sharplink-send-grant-footprint-v1", status="passed",
                    classification="current-thread-allocation-appendix-not-RPC-CPU-or-retained-heap",
                    mode=MODES[arm], baselineSha=BASE, runtimePopulation=population,
                    usesProductionSendCreditLease=str(arm == "B").lower(), builtForNativeAot=str(aot).lower(),
                    dynamicCodeSupported=str(not aot).lower(), dynamicCodeCompiled=str(not aot).lower(),
                    measuredBinarySha256=binary["measuredBinarySha256"], runtimeAssemblySha256=binary["runtimeAssemblySha256"],
                    architecture="X64", processArchitecture="X64",
                    iterations=str(spec["primaryIterations"]), rounds=str(spec["roundsPerProcess"]),
                    poolControllers=str(spec["poolControllerIterations"]), poolStreamsPerController=str(spec["streamsPerPoolControllerIteration"]),
                    encodedItemCreditBytes=str(spec["itemCreditBytes"]), candidateGrantQuantumBytes=str(spec["grantQuantumBytes"]),
                    streamWindowBytes=str(1 << 20), connectionWindowBytes=str(16 << 20),
                    maxFramePayloadBytes=str(4 << 20), maxConcurrentStreams="1024")
    required.update({key: env.get(key, "UNSET") for key in ENV_KEYS})
    for round_index in range(spec["roundsPerProcess"]):
        required.update({f"round.{round_index}.{key}": str(value) for key, value in expected_proof(arm, spec).items()})
    for key, value in required.items():
        if metadata.get(key) != value:
            raise ValueError(f"Footprint execution/source/path proof mismatch: {key}: {metadata.get(key)!r} != {value!r}")
    for key in ("runtime", "os", "counterScope", "nonClaims", "baselineNoOps", "warmup"):
        if not metadata.get(key):
            raise ValueError("Missing interpretation metadata: " + key)
    if not metadata["runtime"].startswith(".NET 10."):
        raise ValueError("Unexpected actual runtime version")
    reader = csv.DictReader(io.StringIO("\n".join(table)), delimiter="\t")
    if tuple(reader.fieldnames or ()) != HEADER:
        raise ValueError("Missing or changed footprint table header")
    samples, seen = [], set()
    expected_order = [(phase, round_index) for round_index in range(spec["roundsPerProcess"]) for phase in spec["phases"]]
    for row in reader:
        if None in row or any(value is None for value in row.values()):
            raise ValueError("Malformed footprint row")
        phase, round_index = row["phase"], int(row["round"])
        count, allocated = int(row["iterations"]), int(row["total_allocated_bytes"])
        per_iteration = float(row["allocated_bytes_per_iteration"])
        expected_count = spec["poolControllerIterations"] if phase.startswith("pool128-") else spec["primaryIterations"]
        if (phase, round_index) in seen or count != expected_count or allocated < 0 or not math.isfinite(per_iteration):
            raise ValueError("Duplicate or invalid footprint allocation row")
        if not math.isclose(per_iteration, allocated / count, rel_tol=1e-13, abs_tol=1e-12):
            raise ValueError("Derived bytes/iteration does not match original integer counter")
        seen.add((phase, round_index))
        samples.append(dict(phase=phase, round=round_index, iterations=count,
                            allocatedBytes=allocated, allocatedBytesPerIteration=per_iteration))
    if [(r["phase"], r["round"]) for r in samples] != expected_order:
        raise ValueError("Missing, extra, reordered or unplanned footprint samples")
    return {"metadata": metadata, "samples": samples, "rawFile": path.name, "rawSha256": sha256(path)}


def binary_identity(directory: Path, aot: bool) -> dict:
    names = ["SharpLink.Benchmarks"] if aot else ["SharpLink.Benchmarks.dll", "SharpLink.Runtime.dll",
                                                        "SharpLink.Benchmarks.deps.json", "SharpLink.Benchmarks.runtimeconfig.json"]
    for name in names:
        if not (directory / name).is_file():
            raise ValueError("Required measured binary is missing: " + name)
    files = {p.relative_to(directory).as_posix(): sha256(p) for p in sorted(directory.rglob("*")) if p.is_file()}
    return {"fileSha256": files, "measuredBinarySha256": files[names[0]],
            "runtimeAssemblySha256": "embedded-in-native-executable" if aot else files["SharpLink.Runtime.dll"]}


class Collector:
    def __init__(self, output: Path, rows: list, provenance: dict):
        self.output, self.rows, self.provenance = output, rows, provenance
        self.checkpoint("Running")

    def checkpoint(self, status: str) -> None:
        write_json(self.output / "exits.json", self.rows)
        write_json(self.output / "provenance.json", self.provenance)
        write_json(self.output / "result.json", {"status": status, "productionGo": False,
                   "classification": "allocation-appendix-only", "doesNotEstablishCorrectnessAcceptance": True,
                   "steps": {s: [r["name"] for r in self.rows if r["status"] == s] for s in ("NotRun", "Running", "Passed", "Failed")}})

    def run(self, row: dict, action=None) -> None:
        row.update(status="Running", startedUtc=now())
        started = time.monotonic()
        self.checkpoint("Running")
        try:
            if action is not None:
                row["details"] = action()
                row["exitCode"] = 0
            else:
                row["exitCode"] = self.command(row)
                if row["exitCode"] != 0:
                    raise ValueError("Command failed with exit " + str(row["exitCode"]))
            row["status"] = "Passed"
        except BaseException as error:
            row.update(status="Failed", error=f"{type(error).__name__}: {error}")
            (self.output / (row["name"] + ".validation-error.txt")).write_text(traceback.format_exc())
            raise
        finally:
            row.update(elapsedSeconds=time.monotonic() - started, finishedUtc=now())
            for suffix in ("stdout.log", "stderr.log"):
                path = self.output / (row["name"] + "." + suffix)
                if path.exists():
                    row[suffix] = {"file": path.name, "sha256": sha256(path)}
            self.checkpoint("Running")
            print(json.dumps({k: row[k] for k in ("name", "status", "exitCode")}), flush=True)

    def command(self, row: dict) -> int:
        with (self.output / (row["name"] + ".stdout.log")).open("w") as stdout, \
                (self.output / (row["name"] + ".stderr.log")).open("w") as stderr:
            process_env = environment(row["runtimePopulation"]) if "runtimePopulation" in row else None
            process = subprocess.Popen(row["command"], cwd=row["cwd"], env=process_env,
                                       stdout=stdout, stderr=stderr, start_new_session=True)
            row["pid"] = process.pid
            self.checkpoint("Running")
            try:
                return process.wait(timeout=row["timeoutSeconds"])
            except subprocess.TimeoutExpired:
                row["watchdogExpired"] = True
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
                return 124
            except BaseException:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
                raise


def main(args: argparse.Namespace) -> int:
    repo, out = args.root.resolve(), args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    populations = POPULATIONS if args.runtime == "all" else (args.runtime,)
    temp = Path(os.environ.get("RUNNER_TEMP", str(out.parent))).resolve()
    works = {arm: temp / ("flow-b2-footprint-" + out.name + "-" + arm) for arm in MODES}
    rows, actions, subjects, identities, binaries, parsed = [], {}, {}, {}, {}, []
    spec = json.loads((repo / FOOT / "phases.json").read_text())
    policy = json.loads((repo / FOOT / "SubjectPolicy.json").read_text())
    head = revision(repo, "HEAD")
    provenance = {"classification": "current-thread-allocation-appendix-not-retained-heap-CPU-or-RPC",
                  "baselineSha": BASE, "candidateSha": args.candidate_sha, "measurementRecipeSha": head,
                  "collectorSha256": sha256(Path(__file__)), "argv": sys.argv, "createdUtc": now(),
                  "runtimePopulations": populations, "orderPerRuntime": ["A", "B", "B", "A"],
                  "phasePlan": spec, "subjectPolicy": policy, "subjects": subjects, "binaries": binaries,
                  "runtimeSourceTreeDefinition": "Git <subject-sha>:src, separate from full commit tree",
                  "correctnessGate": "External prerequisite; this appendix never substitutes for the independent correctness workflow.",
                  "github": {k: os.environ.get(k) for k in ("GITHUB_SHA", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_REPOSITORY")}}

    def plan(name, command=None, work=repo, timeout=600, action=None, **extra):
        row = dict(name=name, command=command, cwd=str(work), timeoutSeconds=timeout,
                   status="NotRun", exitCode=None, **extra)
        rows.append(row)
        if action is not None:
            actions[name] = action
        return row

    def validate_recipe():
        if args.baseline_sha != BASE or not re.fullmatch(r"[0-9a-f]{40}", args.candidate_sha) or head != args.candidate_sha:
            raise ValueError("Explicit subject SHAs must match the fixed baseline and actual recipe/candidate HEAD")
        if os.environ.get("GITHUB_SHA") not in (None, head):
            raise ValueError("Actions source SHA differs from the recipe checkout")
        if revision(repo, "HEAD") != head or git(repo, "diff", "HEAD", "--", "src", FOOT, RECIPE):
            raise ValueError("Uncommitted source or recipe inputs")
        expected_measurement = entries(repo, head, FOOT, RECIPE)
        actual_measurement = measurement_files(repo)
        for name, value in actual_measurement.items():
            if name not in expected_measurement or blob_digest((repo / name).read_bytes()) != expected_measurement[name]:
                raise ValueError("Measurement file is not the committed recipe: " + name)
        if actual_measurement.get(RECIPE) != sha256(Path(__file__)):
            raise ValueError("The executing collector is not the committed recipe")
        if spec["phases"] != re.findall(r'Measure\("([^"]+)"', (repo / FOOT / "Program.Phases.cs").read_text()) or len(spec["phases"]) != 32:
            raise ValueError("Phase plan differs from the measurement program")
        if (spec["roundsPerProcess"], spec["primaryIterations"], spec["poolControllerIterations"], spec["streamsPerPoolControllerIteration"],
                spec["itemCreditBytes"], spec["grantQuantumBytes"]) != (5, 10000, 64, 128, 16, 4096):
            raise ValueError("Predeclared bounded workload was changed")
        provenance["measurementFiles"] = actual_measurement
        provenance["measurementSourceSha256"] = content_digest(actual_measurement)
        shutil.copytree(repo / FOOT, out / "measurement-source", ignore=shutil.ignore_patterns("bin", "obj"))
        shutil.copyfile(repo / RECIPE, out / "measurement-source" / "run_b2_footprint.py")
        return {"measurementSourceSha256": provenance["measurementSourceSha256"]}

    def prepare_subject(arm):
        work, ref = works[arm], BASE if arm == "A" else head
        target = work / FOOT
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(repo / FOOT, target, ignore=shutil.ignore_patterns("bin", "obj"))
        # Keep the identical collector in the source manifest without executing from a different revision.
        (work / RECIPE).parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(repo / RECIPE, work / RECIPE)
        subject = source_identity(work, ref)
        build_files = committed_files(work, ref, build_input_paths(work, ref))
        subject["buildPolicyFileSha256"] = build_files
        if measurement_files(work) != provenance["measurementFiles"]:
            raise ValueError("A/B measurement bytes differ")
        subjects[arm] = subject
        write_json(out / ("source-" + arm + ".json"), subject)
        return {"runtimeSourceContentSha256": subject["runtimeSourceContentSha256"]}

    def pair_and_stamp():
        record = validate_subject_pair(subjects, policy, repo)
        if subjects["A"]["buildPolicyFileSha256"] != subjects["B"]["buildPolicyFileSha256"]:
            raise ValueError("Central/test build or package policy differs across arms")
        for arm, subject in subjects.items():
            fields = {"Mode": MODES[arm], "BaselineSha": BASE, "SubjectSha": subject["subjectSha"],
                      "SubjectCommitTreeSha": subject["subjectCommitTreeSha"], "RuntimeSourceTreeSha": subject["runtimeSourceTreeSha"],
                      "RuntimeSourceContentSha256": subject["runtimeSourceContentSha256"], "MeasurementRecipeSha": head,
                      "MeasurementSourceSha256": provenance["measurementSourceSha256"],
                      "ControllerSourceSha256": subject["fileSha256"][CONTROLLER], "GrantSourceSha256": subject["fileSha256"].get(GRANTS, "ABSENT")}
            text = "namespace SharpLink.Benchmarks;\ninternal static class ModeIdentity\n{\n"
            text += "".join("    internal const string " + key + " = " + json.dumps(value) + ";\n" for key, value in fields.items()) + "}\n"
            (works[arm] / FOOT / "ModeIdentity.cs").write_text(text)
            (out / ("compiled-identity-" + arm + ".cs")).write_text(text)
            identities[arm] = {key[0].lower() + key[1:]: value for key, value in fields.items()}
            subjects[arm]["compiledIdentitySha256"] = digest(text.encode())
        return record

    def check_source(arm):
        current = source_identity(works[arm], subjects[arm]["subjectSha"])
        for key, value in current.items():
            if subjects[arm].get(key) != value:
                raise ValueError("Production inputs changed after source verification")
        if measurement_files(works[arm]) != provenance["measurementFiles"]:
            raise ValueError("Measurement inputs changed after stamping")
        if sha256(works[arm] / FOOT / "ModeIdentity.cs") != subjects[arm]["compiledIdentitySha256"]:
            raise ValueError("Compile-time identity file changed")
        if committed_files(works[arm], subjects[arm]["subjectSha"], build_input_paths(works[arm], subjects[arm]["subjectSha"])) != subjects[arm]["buildPolicyFileSha256"]:
            raise ValueError("Build policy changed after source verification")

    def record_binary(arm, kind):
        check_source(arm)
        directory = out / "binaries" / (arm + "-" + kind)
        if kind == "jit":
            shutil.copytree(works[arm] / FOOT / "bin/Release/net10.0", directory)
        value = binary_identity(directory, kind == "nativeaot")
        value["directory"] = str(directory.relative_to(out))
        binaries[arm + "-" + kind] = value
        assets = works[arm] / FOOT / "obj/project.assets.json"
        if assets.is_file():
            shutil.copyfile(assets, out / ("restore-" + arm + "-" + kind + ".assets.json"))
        return value

    def execute_sample(row, arm, population):
        kind = "nativeaot" if population == "nativeaot" else "jit"
        expected = binaries[arm + "-" + kind]
        directory = out / expected["directory"]
        before = binary_identity(directory, kind == "nativeaot")
        if before != {k: v for k, v in expected.items() if k != "directory"}:
            raise ValueError("Measured binary/dependency bytes changed before execution")
        check_source(arm)
        path = out / "samples" / (row["name"] + ".tsv")
        path.parent.mkdir(exist_ok=True)
        if path.exists():
            raise ValueError("Refusing stale sample file")
        binary = directory / ("SharpLink.Benchmarks" if kind == "nativeaot" else "SharpLink.Benchmarks.dll")
        row["command"] = ([str(binary)] if kind == "nativeaot" else ["dotnet", str(binary)]) + [MODES[arm], BASE,
            subjects[arm]["subjectSha"], provenance["measurementSourceSha256"], subjects[arm]["runtimeSourceTreeSha"], population, str(path)]
        run_env = environment(population)
        row["runtimeFlags"] = {key: run_env.get(key, "UNSET") for key in ENV_KEYS}
        collector.checkpoint("Running")
        row["exitCode"] = collector.command(row)
        if path.exists():
            row["rawSample"] = {"file": str(path.relative_to(out)), "sha256": sha256(path)}
        if binary_identity(directory, kind == "nativeaot") != before:
            raise ValueError("Measured binary/dependency bytes changed during execution")
        check_source(arm)
        if row["exitCode"] != 0:
            raise ValueError("Footprint process failed; partial samples retained")
        result = parse_sample(path, arm, population, identities[arm], expected, run_env, spec)
        result.update(arm=arm, population=population, process=row["name"])
        parsed.append(result)
        write_json(path.with_suffix(".validated.json"), result)
        return {"rawSamples": len(result["samples"]), "actualB2PathProof": arm == "B", "actualNativeAot": kind == "nativeaot"}

    def summarize():
        summary = []
        for population in populations:
            reports = [p for p in parsed if p["population"] == population]
            if len(reports) != 4 or any(len({p["metadata"][key] for p in reports}) != 1
                                        for key in ("runtime", "os", "architecture")):
                raise ValueError("A/B runtime/host metadata is not matched within the population")
            for phase in spec["phases"]:
                arms = {arm: [r["allocatedBytesPerIteration"] for report in parsed if report["arm"] == arm and report["population"] == population
                              for r in report["samples"] if r["phase"] == phase] for arm in MODES}
                if any(len(values) != 2 * spec["roundsPerProcess"] for values in arms.values()):
                    raise ValueError("Incomplete matched allocation population")
                row = {"runtime": population, "phase": phase, "samplesPerArm": 10,
                       "unit": "bytes per controller processing 128 streams" if phase.startswith("pool128-") else "bytes per phase iteration",
                       "A": {"mean": statistics.mean(arms["A"]), "median": statistics.median(arms["A"]), "samples": arms["A"]},
                       "B": {"mean": statistics.mean(arms["B"]), "median": statistics.median(arms["B"]), "samples": arms["B"]}}
                row["meanAdditionalAllocatedBytes"] = row["B"]["mean"] - row["A"]["mean"]
                row["baselineHasCorrespondingOperation"] = phase not in spec["baselineNoOpPhases"]
                if phase.startswith("pool128-"):
                    row["derivedAdditionalAllocatedBytesPerStream"] = row["meanAdditionalAllocatedBytes"] / spec["streamsPerPoolControllerIteration"]
                summary.append(row)
        write_json(out / "allocation-summary.json", {"classification": "allocation-appendix-not-retained-heap-or-RPC", "productionGo": False,
                   "rawProcesses": len(parsed), "rawRows": sum(len(p["samples"]) for p in parsed), "comparisons": summary})
        return {"matchedPhaseComparisons": len(summary)}

    plan("validate-recipe", action=validate_recipe)
    plan("dotnet-info", ["dotnet", "--info"], timeout=60)
    for name, ref in (("base", BASE), ("initial", INITIAL), ("reviewed", policy["reviewedCandidateSourceCommit"])):
        plan("fetch-" + name, ["git", "fetch", "--no-tags", "--depth=1", "origin", ref])
    for arm in MODES:
        plan("worktree-" + arm, ["git", "worktree", "add", "--detach", str(works[arm]), BASE if arm == "A" else head])
        plan("source-" + arm, action=lambda arm=arm: prepare_subject(arm))
    plan("pair-and-stamp", action=pair_and_stamp)
    kinds = [kind for kind in ("jit", "nativeaot") if any((p == "nativeaot") == (kind == "nativeaot") for p in populations)]
    for kind in kinds:
        for arm in MODES:
            command = ["dotnet", "publish" if kind == "nativeaot" else "build", PROJECT, "-c", "Release", "-v", "minimal",
                       "-p:PublishAot=" + str(kind == "nativeaot").lower(), "-p:FlowEvidenceB2=" + str(arm == "B").lower()]
            if kind == "nativeaot":
                command += ["-r", "linux-x64", "-o", str(out / "binaries" / (arm + "-" + kind))]
            plan("build-" + arm + "-" + kind, command, work=works[arm], timeout=900)
            plan("binary-" + arm + "-" + kind, action=lambda arm=arm, kind=kind: record_binary(arm, kind))
    for population in populations:
        for position, arm in enumerate(("A", "B", "B", "A")):
            row = plan(f"execute-{population}-p{position}-{arm}", work=repo, timeout=180, arm=arm, runtimePopulation=population)
            actions[row["name"]] = lambda row=row, arm=arm, population=population: execute_sample(row, arm, population)
    plan("summarize", action=summarize)
    write_json(out / "plan.json", {"provenance": provenance, "steps": rows})
    collector = Collector(out, rows, provenance)
    failure = None
    try:
        for row in rows:
            collector.run(row, actions.get(row["name"]))
    except BaseException as error:
        failure = f"{type(error).__name__}: {error}"
        (out / "collector-failure.txt").write_text(traceback.format_exc())
        for row in rows:
            if row["status"] == "NotRun":
                row["notRunReason"] = "Stopped at the first unexpected failure: " + failure
    finally:
        collector.checkpoint("Failed" if failure else "Passed")
        write_json(out / "artifact-manifest.json", {"algorithm": "sha256", "manifestDoesNotHashItself": True,
                   "files": {p.relative_to(out).as_posix(): sha256(p) for p in sorted(out.rglob("*"))
                             if p.is_file() and p.name != "artifact-manifest.json"}})
    return 1 if failure else 0


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--baseline-sha", default=BASE)
    parser.add_argument("--candidate-sha", required=True)
    parser.add_argument("--runtime", choices=("all", *POPULATIONS), default="all")
    return parser.parse_args()


if __name__ == "__main__":
    raise SystemExit(main(arguments()))
