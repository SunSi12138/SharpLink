import unittest
from summarize import summarize


class SummaryTests(unittest.TestCase):
    def setUp(self):
        self.meta = dict(schemaVersion=1, sample="s", pid=1, markerBytes=1000, operations=10,
                         markerStartMilliseconds=10, markerStopMilliseconds=20,
                         tickEvents=1, eventsLostReported=0)
        self.measured = dict(schemaVersion=1, sample="s", bytes=1000, operations=10, processId=1)
        self.tick = dict(schemaVersion=1, sample="s", pid=1, timeRelativeMilliseconds=15,
                         allocationAmount64=600, eventVersion=4, objectSize=60,
                         typeName="T", frames=["F"], unresolvedFrames=0)
        self.rules = [dict(owner="first", typeContains=["T"]), dict(owner="second", frameContains=["F"])]

    def run_summary(self):
        return summarize(self.meta, self.measured, [self.tick], self.rules)

    def test_exclusive_mapping_and_estimate(self):
        s = self.run_summary()
        self.assertEqual(s["classifiedSampleWeightBytesK"], 600)
        self.assertEqual(list(s["owners"]), ["first"])
        self.assertIsNone(s["exactOwnerCoverage"])
        self.assertIsNone(s["exactObjectsPerOperation"])
        self.assertEqual(s["weightedObjectsPerOperationEstimate"], 1)
        self.assertEqual(s["signedPreciseMinusTickBytesAminusT"], 400)

    def test_measurement_schema_two_preserves_workload_context(self):
        self.measured.update(schemaVersion=2, rpc="oneway-bounded", receiveCreditBounded=True, creditAcquired=10, creditReleased=10)
        s = self.run_summary()
        self.assertEqual(s["measurementSchemaVersion"], 2)
        self.assertEqual(s["workloadContext"]["rpc"], "oneway-bounded")
        self.assertTrue(s["workloadContext"]["receiveCreditBounded"])
        self.assertEqual(s["preciseProcessBytesA"], 1000)

    def test_measurement_schema_rejects_unknown_or_wrong_types(self):
        for version in (0, 3, None, "2", True, 1.0):
            self.measured["schemaVersion"] = version
            with self.assertRaises(ValueError): self.run_summary()

    def test_loss_unknown_is_not_zero(self):
        self.meta.pop("eventsLostReported")
        self.assertEqual(self.run_summary()["lossStatus"], "unknown")

    def test_positive_loss(self):
        self.meta["eventsLostReported"] = 5
        self.assertEqual(self.run_summary()["lossStatus"], "reported-loss")

    def test_overcapture_not_clamped(self):
        self.tick["allocationAmount64"] = 1200
        s = self.run_summary()
        self.assertEqual(s["captureMassRatioTOverA"], 1.2)
        self.assertEqual(s["signedPreciseMinusTickBytesAminusT"], -200)

    def test_unknown_weight_not_imputed(self):
        self.tick["allocationAmount64"] = None
        s = self.run_summary()
        self.assertEqual(s["invalidOrUnknownWeightTickEvents"], 1)
        self.assertEqual(s["representedTickWeightBytesT"], 0)
        self.assertIsNone(s["classifiedSampleFractionKOverT"])

    def test_missing_stack_and_unattributed(self):
        self.tick.update(typeName=None, frames=[], objectSize=None)
        s = self.run_summary()
        self.assertEqual(s["missingStackWeightBytes"], 600)
        self.assertEqual(s["missingTypeWeightBytes"], 600)
        self.assertEqual(s["unclassifiedTickWeightBytesTminusK"], 600)
        self.assertEqual(s["objectSizeMissingWeightBytes"], 600)

    def test_boundaries_excluded(self):
        for ts in (10, 20, float("nan")):
            self.tick["timeRelativeMilliseconds"] = ts
            with self.assertRaises(ValueError): self.run_summary()

    def test_mismatched_sample_pid_counter_rejected(self):
        for key, value in [("pid", 2), ("sample", "wrong"), ("schemaVersion", 2)]:
            original = self.tick[key]
            self.tick[key] = value
            with self.assertRaises(ValueError): self.run_summary()
            self.tick[key] = original
        self.measured["bytes"] = 999
        with self.assertRaises(ValueError): self.run_summary()

    def test_measured_process_id_required_and_validated(self):
        self.measured["processId"] = 2
        with self.assertRaises(ValueError): self.run_summary()
        self.measured.pop("processId")
        with self.assertRaises(ValueError): self.run_summary()

    def test_exact_type_rule_rejects_substring_and_unrelated_frame(self):
        self.rules = [dict(owner="exact", typeEquals=["T"], frameContains=["F"])]
        self.assertEqual(self.run_summary()["classifiedSampleWeightBytesK"], 600)
        self.tick["typeName"] = "Wrapper[T]"
        self.assertEqual(self.run_summary()["classifiedSampleWeightBytesK"], 0)
        self.tick["typeName"] = "T"
        self.tick["frames"] = ["Harness"]
        self.assertEqual(self.run_summary()["classifiedSampleWeightBytesK"], 0)

    def test_bad_event_count_rejected(self):
        self.meta["tickEvents"] = 2
        with self.assertRaises(ValueError): self.run_summary()

    def test_old_object_size_schema_is_unknown(self):
        self.tick["eventVersion"] = 3
        s = self.run_summary()
        self.assertEqual(s["objectSizeMissingWeightBytes"], 600)
        self.assertIsNone(s["weightedObjectsPerOperationEstimate"])
        self.assertEqual(s["objectEstimateStatus"], "unavailable")

if __name__ == "__main__":
    unittest.main()
