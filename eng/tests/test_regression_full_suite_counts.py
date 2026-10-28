#!/usr/bin/env python3
"""Keep the three fixed-old regression runners' current-suite census exact."""

import importlib.util
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
RUNNERS = (
    ("send-credit-lease-race", 2196, 8),
    ("generated-stream-snapshot-regression", 2188, 2),
    ("send-credit-terminal-regression", 2188, 1),
)


def load_runner(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "eng" / name / "run.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def summary(total, failed=0, skipped=0, succeeded=None):
    if succeeded is None:
        succeeded = total - failed - skipped
    return f"total: {total}\nfailed: {failed}\nsucceeded: {succeeded}\nskipped: {skipped}\n"


class ExactRegressionSuiteCensusTests(unittest.TestCase):
    def test_current_full_count_is_declared_and_exact(self):
        for name, count, _ in RUNNERS:
            with self.subTest(runner=name):
                runner = load_runner(name)
                self.assertEqual(runner.FULL_COUNT, count)
                self.assertEqual(runner.inspect_test_log(summary(count), False, False),
                                 dict(total=count, failed=0, succeeded=count, skipped=0))

    def test_missing_or_undeclared_extra_case_is_rejected(self):
        for name, count, _ in RUNNERS:
            for delta in (-1, 1):
                with self.subTest(runner=name, delta=delta):
                    with self.assertRaises(ValueError):
                        load_runner(name).inspect_test_log(summary(count + delta), False, False)

    def test_failure_skip_and_inconsistent_success_are_rejected(self):
        for name, count, _ in RUNNERS:
            for text in (summary(count, failed=1), summary(count, skipped=1),
                         summary(count, succeeded=count - 1)):
                with self.subTest(runner=name, text=text):
                    with self.assertRaises(ValueError):
                        load_runner(name).inspect_test_log(text, False, False)

    def test_focused_current_census_is_unchanged(self):
        for name, _, focused in RUNNERS:
            with self.subTest(runner=name):
                runner = load_runner(name)
                self.assertEqual(runner.FOCUSED_COUNT, focused)
                self.assertEqual(runner.inspect_test_log(summary(focused), True, False)["total"], focused)
                with self.assertRaises(ValueError):
                    runner.inspect_test_log(summary(focused - 1), True, False)

    def test_old_control_cannot_pass_or_fail_without_its_exact_marker(self):
        for name, _, focused in RUNNERS:
            with self.subTest(runner=name):
                runner = load_runner(name)
                with self.assertRaises(ValueError):
                    runner.inspect_test_log(summary(focused), True, True)
                failed = 2 if name == "generated-stream-snapshot-regression" else 1
                with self.assertRaises(ValueError):
                    runner.inspect_test_log(summary(focused, failed=failed), True, True)


if __name__ == "__main__":
    unittest.main()
