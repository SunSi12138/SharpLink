#!/usr/bin/env python3
"""The package SPI oracle accepts only its exact compiler obligation."""

import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("packaged_method_facts", Path(__file__).resolve().parents[1] / "verify-packaged-method-facts.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class PackagedSpiOracleTests(unittest.TestCase):
    def test_exact_resolver_error(self):
        gate.validate_diagnostics([dict(ruleId="CS0535", message=dict(text="IRpcStub.TryResolveMethod(long, out RpcMethodDescriptor)"))], [("CS0535", "IRpcStub.TryResolveMethod(")])

    def test_unrelated_build_failure_is_not_rejection(self):
        with self.assertRaises(ValueError):
            gate.validate_diagnostics([dict(ruleId="CS0246", message="MissingPackage")], [("CS0535", "TryResolveMethod")])

    def test_missing_or_duplicate_error_is_rejected(self):
        error = dict(ruleId="CS0535", message="IRpcStub.TryResolveMethod(long)")
        for errors in ([], [error, error]):
            with self.subTest(errors=errors), self.assertRaises(ValueError):
                gate.validate_diagnostics(errors, [("CS0535", "IRpcStub.TryResolveMethod(")])

    def test_positive_cannot_hide_an_error(self):
        with self.assertRaises(ValueError):
            gate.validate_diagnostics([dict(ruleId="CS1729", message="RpcLocalContract")], [])

    def test_cases_have_one_positive_and_ten_precise_negatives(self):
        cases = list(gate.cases())
        self.assertEqual(len(cases), 11)
        self.assertEqual(sum(not expected for _, _, expected in cases), 1)
        self.assertEqual(len({name for name, _, _ in cases}), 11)


if __name__ == "__main__":
    unittest.main()
