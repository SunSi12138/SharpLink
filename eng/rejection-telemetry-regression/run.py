#!/usr/bin/env python3
"""Replay source-bound telemetry controls in a clean, disposable checkout."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

from fixture_overlay import create_overlay, omit_own_publication

FIXTURE = "test/SharpLink.UnitTests/Server/ConnectionAdmissionTests.cs"
RUN_LOOP = "src/SharpLink.Server/SharpLinkServer.RunLoop.cs"
PROJECT = "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj"
MODULE = "test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll"
SERVER_MODULE = "test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.Server.dll"
SRC_TREE = "c242f33f0041439b176c43460864a888237b79d9"
BASELINE_SHA256 = "65b659f64ec2e1bd3212b7dfa188cc369ea735da8ce5eb58b1781426c9db2cc6"
CANDIDATE_SHA256 = "af2c47a8a7bdfb6c3725ff55b11d35db27bae1ab740cb858a3b2b2492cdb0f8f"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--expected-head", required=True, help="Exact 40-character candidate checkout commit")
    parser.add_argument("--baseline-commit", required=True, help="Exact commit containing the baseline-identical fixture and c242 production")
    parser.add_argument("--expected-src-tree", required=True)
    parser.add_argument("--output", type=Path, required=True, help="New or empty evidence directory, outside the checkout")
    parser.add_argument("--build-dotnet", required=True)
    parser.add_argument("--runtime-dotnet", required=True, help="A host with Microsoft.NETCore.App 10.0.13 as its only .NET 10 runtime")
    args = parser.parse_args()
    repo = args.repo.resolve(strict=True)
    output = args.output.resolve()
    require(not output.is_relative_to(repo), "Evidence output must be outside the checkout")
    require(not output.exists() or (output.is_dir() and not any(output.iterdir())), "Output must be a new or empty directory")
    for value in (args.expected_head, args.baseline_commit, args.expected_src_tree):
        require(re.fullmatch(r"[0-9a-f]{40}", value), "Commit/tree identities must be exact lowercase 40-character hashes")

    def git(*arguments):
        return subprocess.check_output(["git", *arguments], cwd=repo).decode().strip()

    require(git("rev-parse", "HEAD") == args.expected_head, "Checkout HEAD does not match --expected-head")
    require(git("rev-parse", "--show-toplevel") == str(repo), "--repo must name the repository root")
    require(not git("status", "--porcelain", "--untracked-files=all"), "Refusing dirty tracked or untracked source/build inputs; use a fresh worktree")
    require(args.expected_src_tree == SRC_TREE, "This replay targets only the reviewed c242 production tree")
    require(git("rev-parse", "HEAD:src") == SRC_TREE, "Candidate production tree differs from the reviewed source")
    require(git("rev-parse", f"{args.baseline_commit}:src") == SRC_TREE, "Baseline must use the same c242 production, not a different historical runtime")
    baseline = subprocess.check_output(["git", "show", f"{args.baseline_commit}:{FIXTURE}"], cwd=repo)
    originals = {path: (repo / path).read_bytes() for path in (FIXTURE, RUN_LOOP)}
    require(sha(baseline) == BASELINE_SHA256, "Baseline fixture is not the reviewed baseline-identical source")
    require(sha(originals[FIXTURE]) == CANDIDATE_SHA256, "Checkout fixture is not the reviewed source-scoped correction")
    production_files = git("ls-files", "src").splitlines()
    source_hashes = {path: sha((repo / path).read_bytes()) for path in production_files}
    output.mkdir(parents=True, exist_ok=True)
    manifest = {
        "status": "running", "candidateCommit": args.expected_head,
        "candidateTree": git("rev-parse", "HEAD^{tree}"),
        "baselineCommit": args.baseline_commit, "productionTree": SRC_TREE,
        "baselineFixtureSha256": sha(baseline), "candidateFixtureSha256": sha(originals[FIXTURE]),
        "runnerSha256": sha(Path(__file__).read_bytes()),
        "overlaySha256": sha(Path(__file__).with_name("fixture_overlay.py").read_bytes()),
        "environment": {"DOTNET_PROCESSOR_COUNT": "1", "TUNIT_MAX_PARALLEL_TESTS": "8"},
        "originalProductionHashes": source_hashes, "runs": [],
    }
    env = os.environ.copy()
    env.update(manifest["environment"])
    env.update({"DOTNET_CLI_TELEMETRY_OPTOUT": "1", "TESTINGPLATFORM_TELEMETRY_OPTOUT": "1", "MSBUILDDISABLENODEREUSE": "1"})

    def save():
        (output / "result.json").write_text(json.dumps(manifest, indent=2) + "\n")

    def run(name, command, expected=0, noise="handshake", input_hashes=None):
        log = output / f"{name}.log"
        selected_env = env | {"SHARPLINK_REJECTION_PROOF_NOISE": noise}
        started = time.monotonic()
        record = {"name": name, "command": command, "expectedExitCode": expected, "noise": noise,
                  "sourceHashes": {path: sha((repo / path).read_bytes()) for path in originals}}
        if input_hashes is not None:
            record["inputHashes"] = input_hashes
        manifest["runs"].append(record)
        save()
        with log.open("w") as stream:
            result = subprocess.run(command, cwd=repo, env=selected_env, stdout=stream, stderr=subprocess.STDOUT)
        record.update({"exitCode": result.returncode, "elapsedSeconds": time.monotonic() - started, "logSha256": sha(log.read_bytes())})
        save()
        print(f"{name}: exit {result.returncode} (expected {expected})", flush=True)
        require(result.returncode == expected, f"{name}: unexpected exit; preserved output: {log}")
        return log.read_text()

    def build(name):
        run(name, [args.build_dotnet, "build", PROJECT, "-c", "Release", "-m:1", "-nr:false", "-p:UseSharedCompilation=false"])

    def test(name, *, expected=0, noise="handshake", focused=False):
        identity = {path: sha((repo / path).read_bytes()) for path in (FIXTURE, RUN_LOOP, MODULE, SERVER_MODULE)}
        count = 37 if focused else 1
        pattern = "/*/*/ConnectionAdmission*/*" if focused else "/*/*/ConnectionAdmissionTests/RejectionTelemetryRecordsReasonedRejections"
        text = run(name, [args.runtime_dotnet, MODULE, "--treenode-filter", pattern, "--progress", "off", "--output", "Detailed", "--results-directory", str(output / name)], expected, noise, identity)
        require(all(sha((repo / path).read_bytes()) == digest for path, digest in identity.items()), f"{name}: source or binaries changed during execution")
        record = manifest["runs"][-1]
        record["inputHashes"] = identity
        counters = {key: int(re.search(rf"^\s*{key}: (\d+)\s*$", text, re.MULTILINE).group(1)) for key in ("total", "failed", "succeeded", "skipped")}
        require(counters == {"total": count, "failed": 1 if expected else 0, "succeeded": count - (1 if expected else 0), "skipped": 0}, f"{name}: unexpected test inventory {counters}")
        record["counts"] = counters
        save()
        return text

    def check_control(text, *, noise, omitted=False):
        own = 0 if omitted else 3
        expected_global = 3 if omitted else 6
        same_reason = noise == "connection"
        cut = f"REJECTION_PROOF phase=cut ready=False observed=0 ownDisposals=0 unscoped=3 unscopedConnectionLimit={3 if same_reason else 0} reasons="
        end = f"REJECTION_PROOF phase=end observed={own} ownDisposals=3 unscoped={expected_global} unscopedConnectionLimit={expected_global if same_reason else 3} reasons="
        require(cut in text and end in text, "Diagnostic source-attribution or disposition invariant failed")
        if omitted:
            require("every rejected connection must be recorded by telemetry" in text, "Negative did not fail the intended readiness oracle")
        else:
            require(end + "connection_limit,connection_limit,connection_limit" in text, "Positive did not record all three own reason tags")

    try:
        run("build-dotnet-info", [args.build_dotnet, "--info"])
        runtimes = run("runtime-list", [args.runtime_dotnet, "--list-runtimes"])
        versions = re.findall(r"^Microsoft\.NETCore\.App (10\.\S+) ", runtimes, re.MULTILINE)
        require(versions == ["10.0.13"], f"Expected only .NET 10.0.13 on runtime host, found {versions}")
        # Preserve the exact authored overlays alongside output, not in shipped src.
        baseline_overlay = create_overlay(baseline.decode(), baseline=True).encode()
        candidate_overlay = create_overlay(originals[FIXTURE].decode(), baseline=False).encode()
        mutation = omit_own_publication(originals[RUN_LOOP].decode()).encode()
        for name, data in (("baseline-fixture.diagnostic.cs", baseline_overlay), ("candidate-fixture.diagnostic.cs", candidate_overlay), ("run-loop.omitted-publication.diagnostic.cs", mutation)):
            (output / name).write_bytes(data)
        try:
            (repo / FIXTURE).write_bytes(baseline_overlay)
            build("build-baseline")
            text = test("baseline-handshake-counterexample", expected=2)
            require("REJECTION_PROOF phase=cut ready=True observed=3 ownDisposals=0 unscoped=3 unscopedConnectionLimit=0 reasons=handshake_limit,handshake_limit,handshake_limit" in text, "Baseline did not expose the controlled early-readiness cut")
            require('Assert.That(reasons.Contains("connection_limit")).IsTrue()' in text, "Baseline did not fail its original reason assertion")
            (repo / FIXTURE).write_bytes(candidate_overlay)
            build("build-scoped-candidate")
            for noise in ("handshake", "connection"):
                check_control(test(f"candidate-{noise}-positive", noise=noise), noise=noise)
            try:
                (repo / RUN_LOOP).write_bytes(mutation)
                build("build-omitted-own-publication")
                check_control(test("missing-own-publication-negative", expected=2, noise="connection"), noise="connection", omitted=True)
            finally:
                (repo / RUN_LOOP).write_bytes(originals[RUN_LOOP])
                require(sha((repo / RUN_LOOP).read_bytes()) == source_hashes[RUN_LOOP], "Failed to restore production source")
            build("build-restored-production")
            check_control(test("restored-same-reason-positive", noise="connection"), noise="connection")
        finally:
            for path, data in originals.items():
                (repo / path).write_bytes(data)
            manifest["restoredInputHashes"] = {path: sha((repo / path).read_bytes()) for path in originals}
            require(all((repo / path).read_bytes() == data for path, data in originals.items()), "Failed to restore original fixture or production bytes")
            require({path: sha((repo / path).read_bytes()) for path in production_files} == source_hashes, "Production source hash map changed")
            require(not git("status", "--porcelain", "--untracked-files=all"), "Checkout was not clean after restoration")
            manifest["restorationVerified"] = True
            save()
        # Success requires rebuilt genuine source, not merely restoring text files.
        build("build-restored-deliverable")
        test("restored-deliverable-admission", focused=True)
        manifest["status"] = "passed"
    except BaseException as error:
        manifest["status"] = "failed"
        manifest["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        save()
    print(f"Replay passed; evidence preserved in {output}")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
