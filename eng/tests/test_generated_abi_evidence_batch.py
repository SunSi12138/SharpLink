"""Validate generated ABI evidence directory ownership via the unchanged shell."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "run-generated-abi-performance-evidence.sh"
DOTNET = """#!/usr/bin/env python3
import os,pathlib,sys
args=sys.argv[1:]
with open(os.environ["EVIDENCE_EVENTS"],"a") as f:
    f.write(args[0]+"\\n")
if args[0]=="run":
    if "--summarize-generated-abi-streaming-evidence" in args:
        i=args.index("--summarize-generated-abi-streaming-evidence")
        pathlib.Path(args[i+2]).write_text("mock summary only")
        pathlib.Path(args[i+3]).write_text("mock JSONL only")
    else:
        p=pathlib.Path(args[-1])
        p.parent.mkdir(parents=True,exist_ok=True)
        p.write_text('{"mock":true,"not_a_benchmark":true}')
elif args[0]!="build":
    raise SystemExit(2)
"""
TASKSET = """#!/usr/bin/env python3
import os,sys
os.execvp(sys.argv[3], sys.argv[3:])
"""


class GeneratedAbiEvidenceBatchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        eng = self.root / "eng"
        eng.mkdir()
        self.script = eng / SCRIPT.name
        shutil.copyfile(SCRIPT, self.script)
        (self.root / "test/SharpLink.Benchmarks").mkdir(parents=True)
        fake = self.root / "fakebin"
        fake.mkdir()
        for name, code in (("dotnet", DOTNET), ("taskset", TASKSET)):
            target = fake / name
            target.write_text(code)
            target.chmod(0o755)
        self.events = self.root / "events.txt"
        self.env = dict(os.environ, PATH=str(fake)+":"+os.environ["PATH"],
            SHARPLINK_BENCHMARK_SHA="1a54993d", SHARPLINK_BENCHMARK_CPU_LIST="0",
            SHARPLINK_BENCHMARK_WARMUP="1", SHARPLINK_BENCHMARK_UNARY_WARMUP="1",
            SHARPLINK_BENCHMARK_SECONDS="1", EVIDENCE_EVENTS=str(self.events))
        self.default = self.root / "artifacts/p3-generated-abi"

    def run_batch(self, runs, directory=None):
        env = dict(self.env, SHARPLINK_BENCHMARK_RUNS=str(runs))
        if directory is not None:
            env["SHARPLINK_GENERATED_ABI_OUTPUT"] = str(directory)
        return subprocess.run(["bash",str(self.script)],env=env,capture_output=True,text=True)

    @staticmethod
    def fingerprints(path):
        return {str(item.relative_to(path)): hashlib.sha256(item.read_bytes()).hexdigest()
            for item in path.rglob("*") if item.is_file()}

    def test_existing_default_or_explicit_directory_is_rejected_before_build(self):
        for before, after in ((7, 3), (7, 7), (3, 7)):
            for explicit in (False, True):
                with self.subTest(before=before, after=after, explicit=explicit):
                    output = self.root / f"explicit-{before}-{after}" if explicit else self.default
                    if output.exists():
                        shutil.rmtree(output)
                    self.events.unlink(missing_ok=True)
                    first = self.run_batch(before, output if explicit else None)
                    self.assertEqual(first.returncode,0,first.stderr)
                    self.assertEqual(len(list((output/"raw/streaming").glob("*.json"))),7*before)
                    original = self.fingerprints(output)
                    count = len(self.events.read_text().splitlines())
                    second = self.run_batch(after, output if explicit else None)
                    self.assertEqual(second.returncode,2,second.stderr)
                    self.assertEqual(len(self.events.read_text().splitlines()),count,
                        "already owned evidence must be rejected before build/workload")
                    self.assertEqual(self.fingerprints(output),original,
                        "existing evidence and summaries must not be mutated")

    def test_fresh_three_run_batch_is_complete(self):
        output = self.root / "fresh-batch"
        result=self.run_batch(3,output)
        self.assertEqual(result.returncode,0,result.stderr)
        self.assertEqual(len(list((output/"raw/streaming").glob("*.json"))),21)
        self.assertEqual(len(list((output/"raw/unary").glob("*.json"))),3)
        self.assertTrue((output/"streaming-summary.md").is_file())


if __name__=="__main__":
    unittest.main()
