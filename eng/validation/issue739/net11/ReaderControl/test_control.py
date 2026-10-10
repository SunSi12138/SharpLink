#!/usr/bin/env python3
"""Socket-free correctness checks. Local net10 results are not net11 performance evidence."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import resource
import subprocess
import tempfile
import unittest

SOURCE = "eb99fe887cf2129d9b88441245ca0a4a6406b6c2"
CASES = (
    "reader-wrapped-sync", "reader-direct-sync",
    "reader-wrapped-incomplete", "reader-direct-incomplete",
    "reader-wrapped-incomplete-burst32", "reader-direct-incomplete-burst32",
)
LIFECYCLE_FIELDS = ("checks", "completed", "reads", "resets", "sourceCompletions", "sourceGetResults", "advances")


class ReaderControlTests(unittest.TestCase):
    def invoke(self, kind, operations=131072, warmup=16384, expected_runtime=None, source=SOURCE):
        with tempfile.TemporaryDirectory(prefix="issue739-reader-control-") as directory:
            output = Path(directory) / "sample.json"
            env = dict(os.environ, ISSUE739_SOURCE_SHA=source)
            result = subprocess.run(
                [ARGS.dotnet, ARGS.assembly, kind, str(operations), str(warmup), "correctness", str(output),
                 expected_runtime or ARGS.expected_runtime],
                env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
            return result, json.loads(output.read_text()) if output.exists() else None

    def test_all_shapes_and_serial_token_wrap(self):
        for kind in CASES:
            with self.subTest(kind=kind):
                result, sample = self.invoke(kind)
                self.assertEqual(result.returncode, 0, result.stdout)
                self.assertIsNotNone(sample)
                width = 32 if kind.endswith("-burst32") else 1
                incomplete = "incomplete" in kind
                wrapped = "wrapped" in kind
                self.assertEqual(sample["kind"], kind)
                self.assertEqual(sample["sourceSha"], SOURCE)
                self.assertEqual(sample["runtime"], ARGS.expected_runtime)
                self.assertEqual(sample["burstWidth"], width)
                self.assertEqual(sample["bursts"], 131072 // width)
                self.assertEqual(sample["threadStart"], sample["threadEnd"])
                self.assertEqual(sample["executableSha256"], hashlib.sha256(Path(ARGS.assembly).read_bytes()).hexdigest())
                self.assertEqual(sample["runtimeAssemblySha256"], hashlib.sha256(Path(ARGS.assembly).with_name("SharpLink.Runtime.dll").read_bytes()).hexdigest())
                self.assertTrue(sample["precise"])
                self.assertTrue(sample["driverIncluded"])
                self.assertFalse(sample["subtractionApplied"])
                self.assertTrue(sample["sameThreadVerified"])
                self.assertTrue(sample["immediateCompletionVerified"])
                for counts, expected in ((sample, 131072), (sample["warmupCounts"], 16384)):
                    for field in LIFECYCLE_FIELDS:
                        self.assertEqual(counts[field], expected, field)
                    self.assertEqual(counts["startedIncomplete"], expected if incomplete else 0)
                    self.assertEqual(counts["continuationRegistrations"], expected if incomplete and wrapped else 0)
                self.assertEqual(sample["expectedContinuationRegistrations"], 131072 if incomplete and wrapped else 0)
                self.assertEqual(sample["tokenWraps"], 2 if width == 1 else 0)
                self.assertEqual(sample["warmupCounts"]["tokenWraps"], 0)
                for field, normalized in (("bytes", "bytesPerOperation"), ("currentThreadBytes", "currentThreadBytesPerOperation")):
                    self.assertGreaterEqual(sample[field], 0)
                    self.assertEqual(sample[normalized], sample[field] / 131072)

    def test_rejects_invalid_inputs_before_sample(self):
        invalid = (
            dict(kind="unknown"),
            dict(kind="reader-wrapped-sync", operations=0),
            dict(kind="reader-wrapped-sync", warmup=0),
            dict(kind="reader-wrapped-sync", operations=1048577),
            dict(kind="reader-wrapped-incomplete-burst32", operations=33),
            dict(kind="reader-direct-incomplete-burst32", warmup=33),
            dict(kind="reader-wrapped-sync", expected_runtime="0.0.0"),
            dict(kind="reader-wrapped-sync", source="unverified"),
        )
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                result, sample = self.invoke(**arguments)
                self.assertNotEqual(result.returncode, 0)
                self.assertIsNone(sample)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--assembly", required=True)
    parser.add_argument("--expected-runtime", required=True)
    ARGS = parser.parse_args()
    # Invalid-input checks intentionally fail fast; avoid leaving crash dumps behind.
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    unittest.main(argv=[__file__], verbosity=2)
