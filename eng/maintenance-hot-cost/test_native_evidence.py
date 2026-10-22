import unittest

from native_evidence import validate_native_evidence


def row(tier, size=1):
    return {"Tier": [tier], "NativeBytes": size}


class NativeEvidenceTests(unittest.TestCase):
    def test_each_requested_mode_has_real_evidence(self):
        for mode, tiers in {
            "fullopts": ["FullOpts"],
            "tier0": ["Tier0"],
            "tier1": ["Tier1"],
            "pgo": ["Instrumented Tier0", "Tier1"],
        }.items():
            with self.subTest(mode=mode):
                self.assertEqual(sorted(tiers), validate_native_evidence(mode, list(map(row, tiers))))

    def test_empty_listing_fails(self):
        with self.assertRaisesRegex(AssertionError, "Missing or empty"):
            validate_native_evidence("fullopts", [])

    def test_zero_sized_listing_fails(self):
        with self.assertRaisesRegex(AssertionError, "Missing or empty"):
            validate_native_evidence("fullopts", [row("FullOpts", 0)])

    def test_tier0_only_cannot_claim_tier1(self):
        with self.assertRaisesRegex(AssertionError, "Missing requested tier"):
            validate_native_evidence("tier1", [row("Tier0")])

    def test_pgo_requires_instrumentation_and_promotion(self):
        for tiers in [["Instrumented Tier0"], ["Tier1"], ["Tier0", "Tier1"]]:
            with self.subTest(tiers=tiers), self.assertRaisesRegex(AssertionError, "Missing requested tier"):
                validate_native_evidence("pgo", list(map(row, tiers)))

    def test_osr_is_observed_and_no_individual_method_is_fabricated(self):
        rows = [row("Instrumented Tier0"), row("Tier1-OSR")]
        self.assertEqual(["Instrumented Tier0", "Tier1-OSR"], validate_native_evidence("pgo", rows))
        self.assertEqual(2, len(rows))


if __name__ == "__main__":
    unittest.main()
