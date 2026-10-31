"""Offline full plan and real-schema negative controls; never runs a benchmark."""
import copy
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("matrix", ROOT / "eng/performance-matrix.py")
matrix = importlib.util.module_from_spec(spec)
spec.loader.exec_module(matrix)
SHA = "a" * 40


def fixture(expected, plan, provenance):
    options = dict(zip(expected["argv"][::2], expected["argv"][1::2]))
    config = {field: value if key == "--operation" else int(value)
              for key, field in matrix.CONFIG.items() if (value := options.get(key)) is not None}
    config.update(Mode=0, Transport=matrix.TRANSPORTS[options["--transport"]],
                  PerformanceProfile=matrix.PROFILES[options["--profile"]],
                  ConcurrencyConfig=list(map(int, options["--concurrency"].split(","))), RecordingMode=1)
    result = dict(Success=100, Failure=0, OperationsStartedDuringMeasurement=100, OperationsCompleted=100,
                  SampleCount=100, MaximumSampleCapacity=300000000, WorkerCount=1,
                  StopwatchFrequency=10000000, MeasurementDurationSeconds=20, WarmupDurationSeconds=5,
                  DrainDurationSeconds=0, RecorderMode="formal", RecorderVersion=plan["recorderVersion"],
                  FormalComparable=True, P50Us=1.0, P95Us=2.0, P99Us=3.0, P999Us=4.0)
    report_index = [r["file"] for shard in plan["shards"] for r in shard["reports"]].index(expected["file"])
    timestamp = (datetime.datetime(2026, 10, 28, tzinfo=datetime.timezone.utc) + datetime.timedelta(seconds=report_index)).isoformat()
    return dict(SchemaVersion=plan["reportSchemaVersion"], SourceCommit=SHA,
                Workload=expected["project"], Runtime=".NET 10.0.11", OperatingSystem="Linux fixture",
                OsArchitecture="X64", ProcessArchitecture="X64", TimestampUtc=timestamp,
                ProcessorCount=2, Configuration=config,
                Results=[dict(result, Operation=op, Concurrency=c, WorkerCount=c) for op, c in expected["cells"]])


class FullMatrixPlanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.plan = matrix.make_plan(ROOT, "linux", SHA)
        cls.windows = matrix.make_plan(ROOT, "windows", SHA)

    def setUp(self):
        self.shard = self.plan["shards"][0]
        self.expected = self.shard["reports"][0]
        self.provenance = dict(schemaVersion=1, sourceCommit=SHA, sourceDirty=False, platform="linux",
            architecture="X64", cpu="fixture cpu", os="Linux", sdkVersion="10.0.102",
            toolchain="fixture SDK", runtimeInventory="Microsoft.NETCore.App 10.0.11 [/fixture]",
            capturedAtUtc="2026-10-28T00:00:00Z", environment=self.shard["environment"], binaries=[])

    def test_full_plan_counts_and_unique_cells_are_from_real_runner(self):
        self.assertEqual((135, 2835, 19170, 479250),
            (len(self.plan["shards"]), self.plan["reportCount"], self.plan["cellCount"], self.plan["minimumSeconds"]))
        keys = [(r["file"], *cell) for s in self.plan["shards"] for r in s["reports"] for cell in r["cells"]]
        self.assertEqual(len(keys), len(set(keys)))
        self.assertEqual(7750, max(s["minimumSeconds"] for s in self.plan["shards"]))
        self.assertFalse(any("aot-oneway-backpressure" in s["id"] for s in self.plan["shards"]))

    def test_windows_plan_excludes_only_uds_and_preserves_five_rounds(self):
        self.assertEqual((108, 2205, 14910, 372750),
            (len(self.windows["shards"]), self.windows["reportCount"], self.windows["cellCount"], self.windows["minimumSeconds"]))
        self.assertFalse(any(s["id"].startswith("uds-") for s in self.windows["shards"]))
        self.assertTrue(all(s["environment"]["SHARPLINK_MATRIX_REPETITIONS"] == "5" for s in self.windows["shards"]))

    def test_shard_invocations_exactly_partition_full_runner(self):
        # Direct subset output must equal the saved whole-run partition, including
        # every CLI option. Exercise unary and all four stream operations/AOT.
        for shard in [self.shard, next(s for s in self.plan["shards"] if s["id"] == "anonymous-throughput-aot-streams")]:
            records = matrix.runner_records(ROOT, "linux", shard["environment"])
            actual = [matrix.invocation(r, matrix.source_constants(ROOT)[0])[1] for r in records]
            self.assertEqual(shard["reports"], actual)

    def test_plan_mode_never_calls_dotnet_or_creates_report_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            binary = Path(temp) / "dotnet"
            binary.write_text("#!/bin/sh\necho forbidden >&2\nexit 97\n")
            binary.chmod(0o755)
            with patch.dict(os.environ, {"PATH": temp + os.pathsep + os.environ["PATH"]}):
                records = matrix.runner_records(ROOT, "linux", self.shard["environment"])
            self.assertEqual(60, len(records))
            self.assertEqual(["dotnet"], sorted(p.name for p in Path(temp).iterdir()))

    def test_valid_real_schema_and_partial_result_negative_controls(self):
        data = fixture(self.expected, self.plan, self.provenance)
        matrix.validate_report(data, self.expected, self.plan, self.provenance)
        for change in (lambda d: d["Results"].pop(), lambda d: d["Results"].append(copy.deepcopy(d["Results"][0])),
                       lambda d: d["Results"][0].update(Concurrency=999)):
            altered = copy.deepcopy(data)
            change(altered)
            with self.assertRaisesRegex(ValueError, "result cell"):
                matrix.validate_report(altered, self.expected, self.plan, self.provenance)

    def test_wrong_source_runtime_configuration_and_binary_kind_fail(self):
        cases = [(("SourceCommit",), "b" * 40, "source mismatch"),
                 (("Runtime",), ".NET 9.0.1", "runtime absent"),
                 (("ProcessArchitecture",), "Arm64", "architecture mismatch"),
                 (("Configuration", "WarmupSeconds"), 1, "WarmupSeconds"),
                 (("Configuration", "RecordingMode"), 0, "formal recording")]
        for path, value, error in cases:
            with self.subTest(path=path):
                data = fixture(self.expected, self.plan, self.provenance)
                target = data
                for key in path[:-1]:
                    target = target[key]
                target[path[-1]] = value
                with self.assertRaisesRegex(ValueError, error):
                    matrix.validate_report(data, self.expected, self.plan, self.provenance)

    def test_partial_duration_and_wrong_capacity_cannot_claim_full_cell(self):
        for field, value, message in [("MeasurementDurationSeconds", 0.1, "partial measurement"),
                                      ("WarmupDurationSeconds", 0.1, "partial measurement"),
                                      ("MaximumSampleCapacity", 1000, "capacity differs"),
                                      ("WorkerCount", 99, "worker/concurrency")]:
            with self.subTest(field=field):
                data = fixture(self.expected, self.plan, self.provenance)
                data["Results"][0][field] = value
                with self.assertRaisesRegex(ValueError, message):
                    matrix.validate_report(data, self.expected, self.plan, self.provenance)

    def test_sample_loss_failure_and_nonfinite_percentile_fail(self):
        data = fixture(self.expected, self.plan, self.provenance)
        for field, value, error in [("SampleCount", 99, "sample counts"),
                ("OperationsCompleted", 99, "sample counts"), ("MaximumSampleCapacity", 99, "capacity"),
                ("Failure", 1, "nonzero"), ("ValidationFailure", 1, "nonzero"),
                ("Cancelled", 1, "nonzero"), ("TailObserverFailure", 1, "nonzero"),
                ("P999Us", float("nan"), "invalid"), ("RecorderVersion", "old", "recorder")]:
            with self.subTest(field=field):
                row = dict(data["Results"][0], **{field: value})
                with self.assertRaisesRegex(ValueError, error):
                    matrix.validate_result(row, self.plan)

    def test_failure_is_mandatory_strict_integer_zero_but_optional_counts_may_be_absent(self):
        data = fixture(self.expected, self.plan, self.provenance)
        # Real LoadTest records do not have ValidationFailure/Cancelled. The
        # original optional-field semantics must remain valid.
        for result in data["Results"]:
            for field in ("ValidationFailure", "Cancelled", "TailObserverFailure"):
                result.pop(field, None)
        matrix.validate_report(data, self.expected, self.plan, self.provenance)
        for result in data["Results"]:
            del result["Failure"]
        with self.assertRaisesRegex(ValueError, "missing mandatory Failure"):
            matrix.validate_report(data, self.expected, self.plan, self.provenance)
        for value in (False, 0.0, "0", None):
            with self.subTest(value=value):
                row = fixture(self.expected, self.plan, self.provenance)["Results"][0]
                row["Failure"] = value
                with self.assertRaisesRegex(ValueError, "invalid Failure"):
                    matrix.validate_result(row, self.plan)

    def evidence(self, directory):
        shard_dir = directory / self.shard["id"]
        binary = shard_dir / "binaries" / "SharpLink.LoadTest.dll"
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b"explicit fixture binary, not performance evidence")
        prov = copy.deepcopy(self.provenance)
        prov["binaries"] = [dict(runtime="jit", project="SharpLink.LoadTest", artifacts=[dict(
            path="binaries/SharpLink.LoadTest.dll", sha256=hashlib.sha256(binary.read_bytes()).hexdigest(),
            entryPoint=True, originalPath="/fixture/SharpLink.LoadTest.dll")])]
        (shard_dir / "provenance.json").write_text(json.dumps(prov))
        execution = dict(schemaVersion=1, sourceCommit=SHA, finalSourceCommit=SHA, sourceDirtyAfter=False,
                         exitCode=0, shardId=self.shard["id"], environment=self.shard["environment"],
                         command=["bash", "eng/run-performance-matrix.sh"],
                         startedAtUtc="2026-10-28T00:00:00Z", endedAtUtc="2026-10-28T01:00:00Z")
        (shard_dir / "execution.json").write_text(json.dumps(execution))
        for expected in self.shard["reports"]:
            (shard_dir / expected["file"]).write_text(json.dumps(fixture(expected, self.plan, prov)))
        return shard_dir

    def test_offline_shard_validation_missing_report_and_binary_tamper(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(matrix, "git", return_value=SHA), \
                patch.object(matrix, "make_plan", return_value=self.plan):
            directory = Path(temp)
            shard_dir = self.evidence(directory)
            result = matrix.validate(self.plan, directory, ROOT, self.shard["id"])
            self.assertTrue(result["coverageAndCorrectnessPassed"], result)
            self.assertEqual(("single-shard", "not-assessed", 60, 310),
                (result["scope"], result["performanceDecision"], result["acceptedReports"], result["acceptedCells"]))
            report = shard_dir / self.expected["file"]
            content = report.read_text()
            report.unlink()
            self.assertIn("report inventory differs", matrix.validate(self.plan, directory, ROOT, self.shard["id"])["failures"][0])
            report.write_text(content)
            (shard_dir / "binaries/SharpLink.LoadTest.dll").write_bytes(b"different bytes")
            self.assertIn("binary integrity mismatch", matrix.validate(self.plan, directory, ROOT, self.shard["id"])["failures"][0])

    def test_failed_execution_extra_report_and_missing_shard_are_not_full_success(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(matrix, "git", return_value=SHA), \
                patch.object(matrix, "make_plan", return_value=self.plan):
            directory = Path(temp)
            shard_dir = self.evidence(directory)
            execution_file = shard_dir / "execution.json"
            execution = matrix.read_json(execution_file)
            execution["exitCode"] = 1
            execution_file.write_text(json.dumps(execution))
            failed = matrix.validate(self.plan, directory, ROOT, self.shard["id"])
            self.assertIn("execution failed", failed["failures"][0])
            execution["exitCode"] = 0
            execution_file.write_text(json.dumps(execution))
            (shard_dir / "duplicate-report.json").write_text("{}")
            self.assertIn("report inventory differs", matrix.validate(self.plan, directory, ROOT, self.shard["id"])["failures"][0])
            (shard_dir / "duplicate-report.json").unlink()
            full = matrix.validate(self.plan, directory, ROOT)
            self.assertFalse(full["coverageAndCorrectnessPassed"])
            self.assertEqual("full", full["scope"])
            self.assertEqual(134, len(full["failures"]))

    def test_copying_one_round_to_another_is_rejected_even_with_all_filenames(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(matrix, "git", return_value=SHA), \
                patch.object(matrix, "make_plan", return_value=self.plan):
            directory = Path(temp)
            shard_dir = self.evidence(directory)
            first = shard_dir / "jit-r1-tcp-balanced-p1-1-unary-0.json"
            second = shard_dir / "jit-r2-tcp-balanced-p1-1-unary-0.json"
            self.assertNotEqual(matrix.read_json(first)["TimestampUtc"], matrix.read_json(second)["TimestampUtc"])
            # Different whitespace/key order must not evade duplicate detection.
            copied = matrix.read_json(first)
            second.write_text(json.dumps(dict(reversed(list(copied.items()))), separators=(",", ":")))
            result = matrix.validate(self.plan, directory, ROOT, self.shard["id"])
            self.assertFalse(result["coverageAndCorrectnessPassed"])
            self.assertEqual(1, len(result["failures"]))
            self.assertIn("duplicate report content", result["failures"][0])
            self.assertIn(first.name, result["failures"][0])
            self.assertIn(second.name, result["failures"][0])

    def test_missing_provenance_and_runtime_binary_substitution_fail(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = self.evidence(Path(temp))
            for field in ("sdkVersion", "runtimeInventory", "cpu", "architecture", "toolchain"):
                prov = matrix.read_json(directory / "provenance.json")
                prov[field] = ""
                with self.assertRaisesRegex(ValueError, "missing provenance"):
                    matrix.validate_provenance(prov, self.shard, self.plan, directory)
            prov = matrix.read_json(directory / "provenance.json")
            prov["binaries"][0]["runtime"] = "aot"
            with self.assertRaisesRegex(ValueError, "runtime/project"):
                matrix.validate_provenance(prov, self.shard, self.plan, directory)

    def test_shrunken_plan_rejected_before_reading_artifacts(self):
        plan = copy.deepcopy(self.plan)
        plan["shards"].pop()
        with patch.object(matrix, "make_plan", return_value=self.plan):
            with self.assertRaisesRegex(ValueError, "actual full runner"):
                matrix.validate(plan, Path("/not-read"), ROOT)

    def test_run_shard_records_real_exit_and_removes_inherited_overrides(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(matrix, "checked_plan", return_value=self.plan), \
                patch.object(matrix, "host_platform", return_value="linux"), \
                patch.object(matrix, "git", side_effect=lambda root, *args: SHA if args[0] == "rev-parse" else ""), \
                patch.object(matrix.subprocess, "run", return_value=SimpleNamespace(returncode=23)) as run, \
                patch.dict(os.environ, {"SHARPLINK_MATRIX_DURATION": "1", "SHARPLINK_MATRIX_PLAN_ONLY": "1"}):
            args = SimpleNamespace(root=ROOT, plan="unused", evidence=temp, shard=self.shard["id"])
            self.assertEqual(23, matrix.run_shard(args))
            kwargs = run.call_args.kwargs
            self.assertNotIn("SHARPLINK_MATRIX_DURATION", kwargs["env"])
            self.assertNotIn("SHARPLINK_MATRIX_PLAN_ONLY", kwargs["env"])
            self.assertEqual("full", kwargs["env"]["SHARPLINK_MATRIX_TIER"])
            receipt = matrix.read_json(Path(temp) / self.shard["id"] / "execution.json")
            self.assertEqual((23, SHA, False), (receipt["exitCode"], receipt["finalSourceCommit"], receipt["sourceDirtyAfter"]))
            with self.assertRaises(FileExistsError):
                matrix.run_shard(args)
            self.assertEqual(1, run.call_count, "a retry must not overwrite the old attempt")

    def test_capture_copies_actual_binary_bytes_once_and_requires_clean_source(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            binary = root / "build/SharpLink.LoadTest.dll"
            binary.parent.mkdir()
            binary.write_bytes(b"explicit synthetic entry binary")
            (binary.parent / "SharpLink.Runtime.dll").write_bytes(b"explicit synthetic runtime")
            args = SimpleNamespace(root=root, output=root / "evidence/provenance.json", binary=binary,
                                   runtime="jit", project="SharpLink.LoadTest")
            clean_git = lambda root, *args: SHA if args[0] == "rev-parse" else ""
            with patch.object(matrix, "git", side_effect=clean_git), \
                    patch.object(matrix.subprocess, "check_output", return_value="fixture toolchain and cpu"), \
                    patch.object(matrix.platform, "platform", return_value="fixture OS"), \
                    patch.dict(os.environ, {"SHARPLINK_COMMIT": SHA}):
                matrix.capture(args)
            captured = matrix.read_json(args.output)
            self.assertEqual(SHA, captured["sourceCommit"])
            artifacts = captured["binaries"][0]["artifacts"]
            self.assertEqual(2, len(artifacts))
            entry = next(a for a in artifacts if a["entryPoint"])
            archived = Path(args.output).parent / entry["path"]
            self.assertEqual(binary.read_bytes(), archived.read_bytes())
            self.assertEqual(hashlib.sha256(binary.read_bytes()).hexdigest(), entry["sha256"])
            binary.write_bytes(b"later modification")
            self.assertEqual(b"explicit synthetic entry binary", archived.read_bytes())
            with patch.object(matrix, "git", side_effect=lambda root, *args: SHA if args[0] == "rev-parse" else " M changed"):
                with self.assertRaisesRegex(ValueError, "clean source"):
                    matrix.capture(args)

    def test_duplicate_json_keys_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "duplicate.json"
            path.write_text('{"SourceCommit":"old","SourceCommit":"new"}')
            with self.assertRaisesRegex(ValueError, "duplicate JSON key"):
                matrix.read_json(path)


if __name__ == "__main__":
    unittest.main()
