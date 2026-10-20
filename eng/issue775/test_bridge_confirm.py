import math
import unittest
from bridge_confirm import HOSTS, AA_ORDER, FOCUS, PRIMARY
from bridge_confirm_report import host_interval, holm, aggregate


class ConfirmationTests(unittest.TestCase):
    def test_fixed_plan(self):
        self.assertEqual(HOSTS, (1, 2, 3))
        self.assertEqual(AA_ORDER, "XYYXYXXY")
        self.assertEqual(len(FOCUS), 3)
        self.assertEqual(len(PRIMARY), 4)
        for i in range(0, 8, 2):
            self.assertEqual(set(AA_ORDER[i:i+2]), {"X", "Y"})

    def test_identical_effect_is_null(self):
        result = host_interval([0., 0., 0.])
        self.assertEqual(result["geometricRatio"], 1)
        self.assertEqual(result["host95Ci"], [1, 1])
        self.assertEqual(result["twoSidedP"], 1)

    def test_constant_ratio(self):
        result = host_interval([math.log(2)] * 3)
        self.assertEqual(result["host95Ci"], [2, 2])

    def test_df_two_reference(self):
        # [0,1,2] has mean=1, sd=1, t=sqrt(3) and exact p=1-sqrt(3/5).
        result = host_interval([0., 1., 2.])
        self.assertAlmostEqual(result["twoSidedP"], 1-math.sqrt(3/5))
        self.assertAlmostEqual(math.log(result["host95Ci"][1]), 1+4.302652729911275/math.sqrt(3))

    def test_holm_is_monotone(self):
        self.assertEqual(holm({"a": .01, "b": .02, "c": .03, "d": .2}),
                         {"a": .04, "b": .06, "c": .06, "d": .2})

    def test_incomplete_cohort_rejected(self):
        with self.assertRaises(ValueError):
            aggregate([])

    def test_nonfinite_rejected(self):
        with self.assertRaises(ValueError):
            host_interval([0, 0, float("nan")])

    def test_pair_count_not_host_count(self):
        with self.assertRaises(ValueError):
            host_interval([0] * 12)

    def test_same_vm_rejected(self):
        with self.assertRaises(ValueError):
            aggregate([{"replicate": r, "bootId": "same"} for r in HOSTS])

    def test_mixed_driver_rejected(self):
        with self.assertRaises(ValueError):
            aggregate([{"replicate": r, "bootId": str(r), "driverSha": str(r), "runId": "1"} for r in HOSTS])


if __name__ == "__main__":
    unittest.main()
