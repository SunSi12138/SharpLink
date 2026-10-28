"""Synthetic orchestration fixtures only: never execute dotnet or real load."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("scheduler", ROOT / "eng/performance-matrix-scheduler.py")
scheduler = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scheduler)
SHA = "a" * 40
TOOL = "b" * 40
BASELINE = "c" * 40


class SchedulerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.plans = {p: scheduler.matrix.make_plan(ROOT, p, SHA) for p in scheduler.PLATFORMS}

    def schedule(self, mode="selected-shards", shards="linux:tcp-balanced-jit-unary", confirmed=True):
        return scheduler.schedule(self.plans, mode, shards, confirmed, BASELINE, TOOL, "123", "1", 1)

    def test_real_three_platform_plan_preserves_all_378_shards_and_five_reps(self):
        full = self.schedule("full-structural", "")
        self.assertEqual(378, len(full["selected"]))
        self.assertEqual([135, 135, 108], [full["platformTotals"][p]["shards"] for p in scheduler.PLATFORMS])
        self.assertEqual([2835, 2835, 2205], [full["platformTotals"][p]["reports"] for p in scheduler.PLATFORMS])
        self.assertEqual([19170, 19170, 14910], [full["platformTotals"][p]["cells"] for p in scheduler.PLATFORMS])
        for plan in self.plans.values():
            self.assertTrue(all(s["environment"]["SHARPLINK_MATRIX_REPETITIONS"] == "5" for s in plan["shards"]))
            self.assertLess(max(s["minimumSeconds"] for s in plan["shards"]), 240 * 60)
        self.assertEqual("not-assessed", full["performanceDecision"])
        self.assertEqual("reference-only-not-executed-or-compared", full["baselineRole"])

    def test_plan_only_default_has_no_execution_and_baseline_is_optional(self):
        plan = scheduler.schedule(self.plans, "plan-only", "", False, "", TOOL, "123", "1", 1)
        self.assertEqual([], plan["selected"])
        self.assertIsNone(plan["baselineCommit"])
        with self.assertRaisesRegex(ValueError, "plan-only"):
            self.schedule("plan-only")

    def test_execution_needs_confirmation_and_bounded_exact_selection(self):
        with self.assertRaisesRegex(ValueError, "confirmation"):
            self.schedule(confirmed=False)
        for value in ("", "linux:unknown", "windows:uds-balanced-jit-unary",
                      "linux:tcp-balanced-jit-unary,linux:tcp-balanced-jit-unary",
                      ",".join("linux:" + s["id"] for s in self.plans["linux"]["shards"][:7]),
                      "linux:../../escape"):
            with self.subTest(selection=value), self.assertRaises(ValueError):
                self.schedule(shards=value)
        with self.assertRaisesRegex(ValueError, "shorten"):
            self.schedule("full-structural")
        self.assertEqual(6, len(self.schedule(shards=",".join(
            "linux:" + s["id"] for s in self.plans["linux"]["shards"][:6]))["selected"]))

    def test_nonimmutable_refs_run_ids_and_parallelism_are_rejected(self):
        for bad in ("main", "abc123", "A" * 40, SHA + "\n"):
            with self.assertRaises(ValueError):
                scheduler.sha(bad)
        for run_id, attempt, parallel in (("../1", "1", 1), ("123", "0", 1), ("123", "1", 5), ("123", "1", True)):
            with self.assertRaises(ValueError):
                scheduler.schedule(self.plans, "plan-only", "", False, "", TOOL, run_id, attempt, parallel)

    def test_tool_candidate_core_drift_fails_closed_without_overlay(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "eng").mkdir()
            for name in scheduler.CORE_FILES:
                (root / name).write_bytes((ROOT / name).read_bytes())
            scheduler.compatible(root)
            (root / scheduler.CORE_FILES[0]).write_text("# mismatching capture hook")
            with self.assertRaisesRegex(ValueError, "contract differs"):
                scheduler.compatible(root)

    def test_checked_manifest_rejects_cross_attempt_tool_and_metadata_tamper(self):
        with tempfile.TemporaryDirectory() as temp:
            args = SimpleNamespace(campaign=Path(temp), root=ROOT, tool_sha=TOOL, run_id="123", run_attempt="1")
            manifest = self.schedule()
            path = args.campaign / "campaign.json"
            path.write_text(json.dumps(manifest))
            with patch.object(scheduler, "identity"), patch.object(scheduler, "compatible"), \
                    patch.object(scheduler.matrix, "checked_plan", side_effect=lambda p, r: self.plans[p.stem]):
                scheduler.checked(args)
                for field, value in (("runAttempt", "2"), ("toolCommit", SHA), ("timeoutMinutes", 360),
                                     ("performanceDecision", "equivalent")):
                    altered = dict(manifest, **{field: value})
                    path.write_text(json.dumps(altered))
                    with self.subTest(field=field), self.assertRaises(ValueError):
                        scheduler.checked(args)

    def fixture(self, directory, full=False):
        manifest = self.schedule("full-structural", "") if full else self.schedule()
        args = SimpleNamespace(output=directory, root=ROOT, campaign=directory / "unused",
            summary=directory / "summary.json", tool_sha=TOOL, run_id="123", run_attempt="1")
        for item in manifest["selected"]:
            target, shard = item.split(":")
            (directory / "evidence" / target / shard).mkdir(parents=True)
            identity = scheduler.job_identity(manifest, target, shard)
            scheduler.matrix.write_new(directory / "jobs" / target / f"{shard}.start.json",
                {"identity": identity, "startedAtUtc": "synthetic-start"})
            scheduler.matrix.write_new(directory / "jobs" / target / f"{shard}.finish.json",
                {"identity": identity, "endedAtUtc": "synthetic-finish", "runExitCode": 0,
                 "coverageAndCorrectnessPassed": True, "error": None})
        return args, manifest

    def aggregate_fixture(self, args, manifest):
        # The real raw-report validator is covered by test_performance_matrix_plan.
        # This mock isolates orchestration, receipts and scope, not acceptance.
        with patch.object(scheduler, "checked", return_value=(manifest, self.plans)), \
                patch.object(scheduler.matrix, "validate", return_value={"coverageAndCorrectnessPassed": True,
                    "performanceDecision": "not-assessed", "syntheticFixture": True}) as validate, \
                patch("builtins.print"):
            code = scheduler.aggregate(args)
            return code, scheduler.matrix.read_json(args.summary), validate.call_count

    def test_selected_pass_never_claims_full_or_equivalence(self):
        with tempfile.TemporaryDirectory() as temp:
            args, manifest = self.fixture(Path(temp))
            code, result, count = self.aggregate_fixture(args, manifest)
            self.assertEqual((0, 1), (code, count))
            self.assertTrue(result["selectedCoverageAndCorrectnessPassed"])
            self.assertFalse(result["fullThreePlatformCoverageAndCorrectnessPassed"])
            self.assertEqual("not-assessed", result["performanceDecision"])
            with self.assertRaises(FileExistsError):
                self.aggregate_fixture(args, manifest)

    def test_full_pass_requires_all_three_raw_platform_validations(self):
        with tempfile.TemporaryDirectory() as temp:
            args, manifest = self.fixture(Path(temp), full=True)
            code, result, count = self.aggregate_fixture(args, manifest)
            self.assertEqual((0, 3, True), (code, count, result["fullThreePlatformCoverageAndCorrectnessPassed"]))

    def test_failed_missing_cross_attempt_receipts_and_extra_artifacts_cannot_pass(self):
        for kind in ("missing", "failed", "cross-attempt", "extra"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temp:
                args, manifest = self.fixture(Path(temp))
                path = args.output / "jobs/linux/tcp-balanced-jit-unary.finish.json"
                if kind == "missing":
                    path.unlink()
                elif kind == "extra":
                    (args.output / "evidence/linux/unselected-shard").mkdir()
                else:
                    data = scheduler.matrix.read_json(path)
                    if kind == "failed":
                        data["runExitCode"] = 1
                    else:
                        data["identity"]["runAttempt"] = "2"
                    path.write_text(json.dumps(data))
                code, result, _ = self.aggregate_fixture(args, manifest)
                self.assertEqual(1, code)
                self.assertFalse(result["selectedCoverageAndCorrectnessPassed"])
                self.assertTrue(result["failures"])

    def test_raw_failure_overrides_successful_receipts(self):
        with tempfile.TemporaryDirectory() as temp:
            args, manifest = self.fixture(Path(temp))
            with patch.object(scheduler, "checked", return_value=(manifest, self.plans)), \
                    patch.object(scheduler.matrix, "validate", return_value={"coverageAndCorrectnessPassed": False}), \
                    patch("builtins.print"):
                self.assertEqual(1, scheduler.aggregate(args))

    def test_prepare_receipt_is_immutable_and_platform_bound(self):
        with tempfile.TemporaryDirectory() as temp:
            args = SimpleNamespace(output=Path(temp), platform="linux", shard="tcp-balanced-jit-unary")
            with patch.object(scheduler, "checked", return_value=(self.schedule(), self.plans)), \
                    patch.object(scheduler.matrix, "host_platform", return_value="windows"):
                with self.assertRaisesRegex(ValueError, "platform"):
                    scheduler.prepare(args)
            with patch.object(scheduler, "checked", return_value=(self.schedule(), self.plans)), \
                    patch.object(scheduler.matrix, "host_platform", return_value="linux"):
                scheduler.prepare(args)
                with self.assertRaises(FileExistsError):
                    scheduler.prepare(args)

    def test_run_failure_still_validates_and_preserves_failure_receipt(self):
        with tempfile.TemporaryDirectory() as temp:
            args, manifest = self.fixture(Path(temp))
            args.platform, args.shard = "linux", "tcp-balanced-jit-unary"
            scheduler.receipt_path(args, "finish").unlink()
            with patch.object(scheduler, "checked", return_value=(manifest, self.plans)), \
                    patch.object(scheduler.matrix, "run_shard", return_value=23) as run, \
                    patch.object(scheduler.matrix, "validate", return_value={"coverageAndCorrectnessPassed": False}) as validate:
                self.assertEqual(1, scheduler.execute(args))
                self.assertEqual(1, validate.call_count)
                self.assertEqual(23, scheduler.matrix.read_json(scheduler.receipt_path(args, "finish"))["runExitCode"])
                with self.assertRaisesRegex(ValueError, "already finished"):
                    scheduler.execute(args)
                self.assertEqual(1, run.call_count)

    def test_workflow_is_manual_default_safe_and_three_separate_fail_fast_false_groups(self):
        workflow = (ROOT / ".github/workflows/full-performance-matrix.yml").read_text()
        shard = (ROOT / ".github/workflows/full-performance-shard.yml").read_text()
        self.assertIn("default: plan-only", workflow)
        self.assertIn("default: false", workflow)
        self.assertNotIn("pull_request:", workflow)
        self.assertNotIn("push:", workflow)
        self.assertEqual(3, workflow.count("fail-fast: false"))
        for p in scheduler.PLATFORMS:
            self.assertIn("matrix: ${{ fromJSON(needs.plan.outputs." + p + ") }}", workflow)
        self.assertIn("timeout-minutes: 240", shard)
        self.assertIn("if: always()", shard)
        self.assertIn("${{ github.run_attempt }}", shard)
        self.assertNotIn("overwrite: true", shard)
        self.assertNotIn("secrets: inherit", workflow)


if __name__ == "__main__":
    unittest.main()
