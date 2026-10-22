"""Portable CI diagnostic. Mutates only a test fixture, then restores exact bytes and rebuilds.

Requires the validated child provenance fields. No production source or budget change.
Example: python run_allocation_counterfactual.py --repo . --output negative-arm64 \
    --build-dotnet dotnet --runtime-dotnet dotnet --expected-architecture Arm64
Run the ordinary full lifetime filter separately for semantic/registered-consumer coverage.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

p = argparse.ArgumentParser()
p.add_argument("--repo", required=True)
p.add_argument("--output", required=True)
p.add_argument("--build-dotnet", default="dotnet")
p.add_argument("--runtime-dotnet", default="dotnet")
p.add_argument("--expected-architecture", choices=["X64", "Arm64"], required=True)
p.add_argument("--expected-runtime")
a = p.parse_args()
if not __debug__:
    raise RuntimeError("Run this validation without Python -O or PYTHONOPTIMIZE")
repo = Path(a.repo).resolve()
output = Path(a.output).resolve()
output.mkdir(parents=True, exist_ok=True)
project = "test/SharpLink.UnitTests/SharpLink.UnitTests.csproj"
dll = repo / "test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll"
source = repo / "test/SharpLink.UnitTests/Runtime/ReadOwnershipPipeReaderLifetimeTests.cs"
case = "SuccessfulSuspensionsShouldNotAllocateAfterWarmup"
prefix = "READ_OWNERSHIP_ALLOCATION_VERIFIED|"
env = dict(os.environ, DOTNET_PROCESSOR_COUNT="1", TUNIT_MAX_PARALLEL_TESTS="8")

def run(command, log, timeout):
    with log.open("w", encoding="utf-8") as stream:
        completed = subprocess.run(command, cwd=repo, env=env, stdout=stream,
                                   stderr=subprocess.STDOUT, timeout=timeout)
    return completed.returncode

def build(stage):
    rc = run([a.build_dotnet, "build", project, "-c", "Release", "--no-restore",
              "-m:1", "-nr:false", "-p:UseSharedCompilation=false"],
             output / (stage + "-build.log"), 600)
    if rc:
        raise RuntimeError(f"{stage} build failed: exit {rc}; see retained build log")

def strings(node):
    if isinstance(node, str):
        yield node
    elif isinstance(node, dict):
        for value in node.values():
            yield from strings(value)
    elif isinstance(node, list):
        for value in node:
            yield from strings(value)

def test(stage, negative):
    directory = output / stage
    directory.mkdir()
    rc = run([a.runtime_dotnet, str(dll), "--treenode-filter",
              f"/*/*/ReadOwnershipPipeReaderLifetimeTests/{case}",
              "--progress", "off", "--output", "Detailed", "--results-directory", str(directory)],
             directory / "stdout.log", 180)
    reports = list(directory.glob("*.tunit-report.json"))
    if len(reports) != 1:
        raise RuntimeError(f"{stage}: expected exactly one retained TUnit JSON report")
    report = json.loads(reports[0].read_text(encoding="utf-8"))
    summary = report["summary"]
    tests = [t for group in report["groups"] for t in group["tests"]]
    assert summary["total"] == 1 and len(tests) == 1 and tests[0]["methodName"] == case
    assert summary["skipped"] == 0 and summary["cancelled"] == 0 and summary["timedOut"] == 0
    values = list(strings(report))
    text = "\n".join(values)
    if negative:
        assert rc != 0 and summary["failed"] == 1 and summary["passed"] == 0
        assert "timeout=False, exit=1" in tests[0]["exception"]["message"]
        assert "successful suspended read/release must allocate 0 bytes, got 280" in text
        assert prefix not in text
        print("EXPECTED_FAILURE: original assertion counted exactly 280 bytes; parent and child failed")
    else:
        assert rc == 0 and summary["passed"] == 1 and summary["failed"] == 0
        records = {}
        for value in values:
            for line in value.splitlines():
                if line.startswith(prefix):
                    record = json.loads(line[len(prefix):])
                    records[record["nonce"]] = record
        assert len(records) == 1, "The validated child provenance must be retained in ordinary test output"
        record = next(iter(records.values()))
        assert record["scenario"] == case and record["architecture"] == a.expected_architecture
        assert record["executed"] == 1 and record["warmup"] == 1000 and record["measured"] == 10000
        assert record["allocated"] == 0 and record["passed"] and record["cacheUnchanged"]
        if a.expected_runtime:
            assert record["runtime"] == a.expected_runtime
        print("VERIFIED_POSITIVE:", json.dumps(record, separators=(",", ":")))

original = source.read_bytes()
build("positive")
test("positive", False)
try:
    text = original.decode("utf-8")
    start = text.index("private static void MeasureSuccessfulSuspensions()")
    marker = "var before = GC.GetAllocatedBytesForCurrentThread();"
    insert = text.index(marker, start) + len(marker)
    mutated = text[:insert] + "\n        GC.KeepAlive(new byte[256]); // DIAGNOSTIC ONLY; NEVER PUBLISH." + text[insert:]
    source.write_bytes(mutated.encode("utf-8"))
    (output / "mutation-provenance.json").write_text(json.dumps({
        "originalSha256": hashlib.sha256(original).hexdigest(),
        "mutatedSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "mutation": "GC.KeepAlive(new byte[256]); immediately after the measured before-counter",
        "originalBudget": 0, "originalWarmup": 1000, "originalMeasured": 10000
    }, indent=2) + "\n", encoding="utf-8")
    build("negative")
    test("negative", True)
finally:
    source.write_bytes(original)
    assert source.read_bytes() == original
    # Never leave a mutant output behind for a later --no-build acceptance run.
    build("restored")
test("restored", False)
print("COUNTERFACTUAL_VALIDATED: source restored byte-for-byte; restored binary passed")
