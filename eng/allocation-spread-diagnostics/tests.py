import contextlib
import hashlib
import importlib.util
import io
import json
import os
import tarfile
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("diagnostic", Path(__file__).with_name("run.py"))
diagnostic = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnostic)
package_spec = importlib.util.spec_from_file_location("packager", Path(__file__).with_name("package.py"))
packager = importlib.util.module_from_spec(package_spec)
package_spec.loader.exec_module(packager)


def fixture(injected=0):
    cases = []
    for name, (concurrency, operations, maximum, spread) in diagnostic.CASES.items():
        value = maximum + 100 if injected else maximum - 100
        samples = []
        for index in range(5):
            before = dict(timestamp=100, timestampUtc="2026-10-09T00:00:00Z", gen0Collections=1,
                          gen1Collections=1, gen2Collections=1, completedWorkItems=100,
                          threadCount=4, pendingWorkItems=0)
            after = dict(before, timestamp=200, timestampUtc="2026-10-09T00:00:01Z", completedWorkItems=200)
            samples.append(dict(index=index, completedOperations=operations, allocatedBytes=value * operations,
                                bytesPerOperation=value, elapsedMilliseconds=1000,
                                diagnosticsBefore=before, diagnosticsAfter=after))
        cases.append(dict(name=name, concurrency=concurrency, warmupOperations=512, operationsPerSample=operations,
                          sampleCount=5, maxBytesPerOperation=maximum, maxSpreadBytesPerOperation=spread,
                          medianBytesPerOperation=value, spreadBytesPerOperation=0, minBytesPerOperation=value,
                          maxBytesPerOperationObserved=value, passed=not injected, samples=samples))
    return dict(runtimeVersion=diagnostic.RUNTIME, runtimeMajor=10, mode="gate", configuration="Release",
                injectedBytesPerOperation=injected, filter=",".join(diagnostic.CASES), cases=cases, passed=not injected)


class ValidationTests(unittest.TestCase):
    def test_valid_baseline_and_negative_control(self):
        self.assertTrue(diagnostic.validate_gate(fixture(), 0, 0))
        self.assertFalse(diagnostic.validate_gate(fixture(512), 1, 512))

    def test_fail_closed_mutations(self):
        mutations = [lambda x: x.update(runtimeVersion="10.0.2"),
                     lambda x: x.update(configuration="Debug"),
                     lambda x: x.update(injectedBytesPerOperation=512),
                     lambda x: x.update(filter="rpc-add-sharedmemory-c1"),
                     lambda x: x["cases"].pop(),
                     lambda x: x["cases"][0].update(sampleCount=4),
                     lambda x: x["cases"][0].update(warmupOperations=4096),
                     lambda x: x["cases"][0].update(maxSpreadBytesPerOperation=999),
                     lambda x: x["cases"][0].update(medianBytesPerOperation=0),
                     lambda x: x["cases"][0]["samples"][0].update(completedOperations=3999),
                     lambda x: x["cases"][0]["samples"][0].update(bytesPerOperation=float("nan")),
                     lambda x: x["cases"][0]["samples"][0].update(elapsedMilliseconds=0),
                     lambda x: x["cases"][0]["samples"][0]["diagnosticsAfter"].update(timestamp=1)]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                report = fixture()
                mutation(report)
                with self.assertRaises((ValueError, KeyError)):
                    diagnostic.validate_gate(report, 0, 0)

    def test_spread_outlier_must_remain_failure(self):
        report = fixture()
        case = report["cases"][0]
        sample = case["samples"][-1]
        sample["bytesPerOperation"] += 51
        sample["allocatedBytes"] = sample["bytesPerOperation"] * sample["completedOperations"]
        case.update(maxBytesPerOperationObserved=sample["bytesPerOperation"], spreadBytesPerOperation=51, passed=False)
        report["passed"] = False
        self.assertFalse(diagnostic.validate_gate(report, 1, 0))
        with self.assertRaises(ValueError):
            diagnostic.validate_gate(report, 0, 0)
        case["passed"] = True
        with self.assertRaises(ValueError):
            diagnostic.validate_gate(report, 1, 0)

    def test_negative_control_cannot_pass_due_to_setup_or_spread_only(self):
        report = fixture()
        report["injectedBytesPerOperation"] = 512
        with self.assertRaises(ValueError):
            diagnostic.validate_gate(report, 1, 512)
        report = fixture(512)
        report["cases"] = []
        with self.assertRaises(ValueError):
            diagnostic.validate_gate(report, 1, 512)
        with self.assertRaises(ValueError):
            diagnostic.validate_gate(fixture(512), 124, 512)


class OrchestrationTests(unittest.TestCase):
    def test_traced_pass_does_not_replace_failed_untraced_samples(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "evidence"
            labels = []

            def fake_run(command, directory, label, executions, timeout=300):
                labels.append(label)
                (directory / (label + ".log")).write_text("Synthetic orchestration fixture only")
                if label == "self-test":
                    diagnostic.write(directory / "self-test.json", {"passed": True})
                if label in ("untraced", "traced", "negative-control"):
                    report = fixture(512 if label == "negative-control" else 0)
                    if label == "untraced":
                        case = report["cases"][0]
                        sample = case["samples"][-1]
                        sample["bytesPerOperation"] += 51
                        sample["allocatedBytes"] = sample["bytesPerOperation"] * sample["completedOperations"]
                        case.update(maxBytesPerOperationObserved=sample["bytesPerOperation"],
                                    spreadBytesPerOperation=51, passed=False)
                        report["passed"] = False
                    diagnostic.write(directory / (label + ".json"), report)
                    return 0 if report["passed"] else 1
                return 0

            def fake_git(command, text):
                return diagnostic.SOURCE + "\n" if command[1] == "rev-parse" else ""

            with patch.object(diagnostic, "run", side_effect=fake_run), \
                    patch.object(diagnostic.subprocess, "check_output", side_effect=fake_git), \
                    patch.object(diagnostic.subprocess, "run"), \
                    patch.object(diagnostic.platform, "platform", return_value="synthetic test platform"), \
                    patch.object(diagnostic.sys, "argv", ["run.py", "--trace", "unused", "--output", str(output)]), \
                    contextlib.redirect_stdout(io.StringIO()):
                status = diagnostic.main()
            self.assertEqual(status, 1)
            summary = json.loads((output / "summary.json").read_text())
            self.assertTrue(summary["complete"], summary)
            self.assertFalse(summary["untracedGatePassed"])
            self.assertTrue(summary["traced"]["passed"])
            self.assertEqual(labels, ["dotnet-info", "trace-version", "cpu", "self-test", "untraced",
                                      "negative-control", "calibration", "parse-calibration", "traced", "parse-traced"])
            original = json.loads((output / "untraced.json").read_text())
            self.assertEqual(sum(len(case["samples"]) for case in original["cases"]), 10)
            self.assertEqual(original["cases"][0]["spreadBytesPerOperation"], 51)


class WorkflowTests(unittest.TestCase):
    def test_runner_directory_is_resolved_only_in_setup_step(self):
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/allocation-spread-diagnostics.yml").read_text()
        before_steps = workflow.split("    steps:", 1)[0]
        self.assertNotIn("runner.", before_steps)
        setup = workflow.split("      - name: Install pinned build SDK and diagnostic runtime", 1)[1].split("      - name:", 1)[0]
        self.assertIn("        env:\n          DOTNET_INSTALL_DIR: ${{ runner.temp }}/allocation-dotnet", setup)
        self.assertIn("test \"$(cat artifacts/allocation-spread-setup/sdk-version.txt)\" = '10.0.102'", workflow)


class PackagingTests(unittest.TestCase):
    def test_roundtrip_hashes_and_part_bounds(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            source.mkdir()
            payload = os.urandom(2048)
            (source / "raw.nettrace").write_bytes(payload)
            (source / "summary.json").write_text('{"diagnosticOnly": true}')
            with patch.object(packager, "PART_BYTES", 512), patch.object(packager, "UPLOAD_SLOTS", 20):
                index = packager.package(source, root / "parts")
            blocks = [(root / "parts" / part["file"]).read_bytes() for part in index["parts"]]
            self.assertTrue(all(len(block) <= 512 for block in blocks))
            for block, part in zip(blocks, index["parts"]):
                self.assertEqual(hashlib.sha256(block).hexdigest(), part["sha256"])
            archive = b"".join(blocks)
            self.assertEqual(hashlib.sha256(archive).hexdigest(), index["archiveSha256"])
            corrupted = bytearray(archive)
            corrupted[0] ^= 1
            self.assertNotEqual(hashlib.sha256(corrupted).hexdigest(), index["archiveSha256"])
            with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as tar:
                self.assertEqual(tar.extractfile("raw.nettrace").read(), payload)
            with self.assertRaises(FileExistsError):
                packager.package(source, root / "parts")

    def test_overflow_retains_entire_evidence_and_marks_transfer_incomplete(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            source.mkdir()
            payload = os.urandom(2048)
            (source / "raw.nettrace").write_bytes(payload)
            with patch.object(packager, "PART_BYTES", 128), patch.object(packager, "UPLOAD_SLOTS", 1):
                with self.assertRaises(ValueError):
                    packager.package(source, root / "parts")
            index = json.loads((root / "parts/index.json").read_text())
            self.assertFalse(index["completeTransfer"])
            self.assertTrue((root / "parts/overflow.txt").is_file())
            with tarfile.open(root / "parts/evidence.tar.gz", "r:gz") as tar:
                self.assertEqual(tar.extractfile("raw.nettrace").read(), payload)
            self.assertEqual((source / "raw.nettrace").read_bytes(), payload)


if __name__ == "__main__":
    unittest.main()
