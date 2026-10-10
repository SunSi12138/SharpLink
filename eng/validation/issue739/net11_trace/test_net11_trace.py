"""Offline contract tests; synthetic rows never count as capture/execution evidence."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from project import HERE, NET11, TRACE, pilot, summary, driver_source
from run import contrasts, decoder_source
from validate import CASES, SEQUENCE, OPERATIONS, WARMUP, TOOL_RUNTIME, validate_sample, validate_trace


def sample_row(traced=True):
    return dict(schemaVersion=1, sourceSha=pilot.SOURCE, sample="sample", processId=7,
                runtime="11.0.0", framework=".NET " + pilot.RUNTIME, corelibPath="/dotnet/" + pilot.RUNTIME + "/System.Private.CoreLib.dll", corelibSha256="targetcore",
                operations=OPERATIONS, warmup=WARMUP, checks=OPERATIONS, received=OPERATIONS+1, expectedReceived=OPERATIONS+1,
                concurrency=1, connectionCount=1, threadPoolMinimumWorkers=132, threadPoolMinimumIo=132,
                transport="tcp", kind="add", precise=True, driverIncluded=True, subtractionApplied=False,
                diagnostic=True, net11TraceDiagnostic=True, traceEnabled=traced,
                markerWarmupCompleted=True, markerEnabledAtStart=traced, markerEnabledAtStop=traced,
                markerProvider="SharpLink-Issue739", markerBoundary="marker window wider than precise bytes",
                executableSha256="driver", architecture="X64", serverGc=False,
                bytes=OPERATIONS*100, gen0=1, ticksStart=100, ticksEnd=200, stopwatchFrequency=100,
                markerStartTicks=99, markerStopTicks=201, bytesPerOperation=100., elapsedSeconds=1., qps=float(OPERATIONS),
                cpuMilliseconds=1., cpuNanosecondsPerOperation=1e6/OPERATIONS, p50Nanoseconds=1., p99Nanoseconds=2.)


def trace_fixture():
    row = sample_row()
    meta = dict(schemaVersion=1, sample="sample", pid=7, markerBytes=row["bytes"], operations=OPERATIONS,
                markerProvider="SharpLink-Issue739", markerStartMilliseconds=10., markerStopMilliseconds=20.,
                decoderVersion="3.1.30.0", decoderRuntime=TOOL_RUNTIME,
                decoderCorelibPath="/dotnet/"+TOOL_RUNTIME+"/System.Private.CoreLib.dll", decoderCorelibSha256="toolcore",
                traceSha256="trace", conversionCompleted=True, eventsLostReported=0, lossNotifications=[],
                unrecognizedAllocationEvents=0, tickEvents=1, allocationTickVersions=[4])
    tick = dict(schemaVersion=1, sample="sample", pid=7, timeRelativeMilliseconds=15.,
                eventVersion=4, allocationAmount64=row["bytes"]+100, typeName="UnknownRC1Type", frames=["Runtime.SomeMethod()"], unresolvedFrames=0, objectSize=64)
    capture = dict(schemaVersion=1, traceSha256="trace", exitCode=0, abortReason=None, toolVersion="9.0.661903+commit",
                   providerConfiguration=dict(profile="gc-verbose", additionalProviders="SharpLink-Issue739:0xffffffffffffffff:5", bufferMiB=256))
    return row, meta, [tick], capture


class Contracts(unittest.TestCase):
    def test_bounded_preregistered_shape(self):
        self.assertEqual(CASES, ("tcp-add-c1", "shm-add-c1"))
        self.assertEqual(SEQUENCE, (("A", False), ("B", False), ("B", True), ("A", True), ("A", True), ("B", True), ("B", False), ("A", False), ("A", False), ("A", False)))
        self.assertEqual(len(CASES)*len(SEQUENCE), 20)
        self.assertEqual(len(CASES)*sum(trace for _, trace in SEQUENCE), 8)
        self.assertEqual((OPERATIONS, WARMUP), (131072, 32768))

    def test_sample_accepts_both_modes_only_in_diagnostic_validator(self):
        for traced in (False, True):
            row = sample_row(traced)
            validate_sample(row, CASES[0], "sample", traced, "driver", "targetcore")
            with self.assertRaisesRegex(ValueError, "Unexpected instrumentation"):
                pilot.validate_sample(row, "tiny", CASES[0], "sample", OPERATIONS, WARMUP, "driver")

    def test_stable_validator_still_rejects_trace_even_without_diagnostic_flag(self):
        row = sample_row(True)
        row["diagnostic"] = False
        with self.assertRaisesRegex(ValueError, "Unexpected instrumentation"):
            pilot.validate_sample(row, "tiny", CASES[0], "sample", OPERATIONS, WARMUP, "driver")

    def test_sample_rejects_counter_identity_instrumentation_and_window_drift(self):
        for key, bad in (("operations", OPERATIONS-1), ("warmup", WARMUP-1), ("checks", OPERATIONS-1), ("received", OPERATIONS),
                         ("processId", 0), ("concurrency", 32), ("corelibSha256", "other"), ("executableSha256", "other"),
                         ("runtime", "10.0.12"), ("traceEnabled", False), ("markerEnabledAtStop", False), ("markerWarmupCompleted", False),
                         ("diagnostic", False), ("bytesPerOperation", float("nan")), ("markerStartTicks", 101), ("markerStopTicks", 199),
                         ("elapsedSeconds", 2.), ("qps", 1.), ("checks", True), ("p99Nanoseconds", 0.), ("cpuNanosecondsPerOperation", 1.)):
            row = sample_row()
            row[key] = bad
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_sample(row, CASES[0], "sample", True, "driver", "targetcore")

    def test_driver_overlay_is_bounded_warmed_and_only_changes_copied_driver(self):
        original_path = NET11 / "Driver/Program.cs"
        before = original_path.read_bytes()
        text = driver_source(before.decode())
        self.assertEqual(original_path.read_bytes(), before)
        self.assertIn('operations != 131072 || warmup != 32768', text)
        self.assertLess(text.index('Markers.Log.Stop(markerWarmupSample'), text.index('GC.Collect()'))
        self.assertLess(text.index('Markers.Log.Start(args[4])'), text.index('long bytesStart'))
        self.assertLess(text.index('long ticksEnd'), text.index('Markers.Log.Stop(args[4]'))
        self.assertIn('diagnostic = true, traceEnabled = traceExpected', text)
        with self.assertRaisesRegex(ValueError, "anchor"):
            driver_source(text)

    def test_decoder_overlay_retains_old_parser_and_exposes_runtime_and_unknown_events(self):
        text = decoder_source((TRACE / "Program.cs").read_text())
        self.assertIn('unrecognizedAllocationEvents++', text)
        self.assertIn('decoderCorelibSha256', text)
        self.assertIn('long? objectSize = e.Version >= 4 ? tick.ObjectSize : null;', text)
        self.assertIn('OnLostEvents', text)
        with self.assertRaises(ValueError):
            decoder_source(text)

    def test_first_capture_compatibility_and_loss_stays_diagnostic(self):
        row, meta, ticks, capture = trace_fixture()
        meta["eventsLostReported"] = 3
        result = validate_trace(meta, row, ticks, capture, "trace", "toolcore")
        self.assertEqual(result["eventsLostReported"], 3)
        self.assertEqual(result["checkedTickCount"], 1)
        raw = summary.summarize(meta, row, ticks, [])
        self.assertEqual(raw["lossStatus"], "reported-loss")
        self.assertEqual(raw["signedPreciseMinusTickBytesAminusT"], -100)
        self.assertIsNone(raw["exactOwnerCoverage"])
        self.assertIsNone(raw["exactObjectsPerOperation"])
        self.assertEqual(raw["classifiedSampleWeightBytesK"], 0)

    def test_individual_bad_payload_cannot_hide_behind_readable_tick(self):
        for key, bad in (("eventVersion", 5), ("eventVersion", 1), ("allocationAmount64", None), ("allocationAmount64", 0),
                         ("typeName", None), ("typeName", " "), ("typeName", 7), ("sample", "other"), ("pid", 8), ("unresolvedFrames", -1)):
            row, meta, ticks, capture = trace_fixture()
            bad_tick = copy.deepcopy(ticks[0])
            bad_tick[key] = bad
            ticks.append(bad_tick)
            meta["tickEvents"] = 2
            with self.subTest(key=key, bad=bad), self.assertRaises(ValueError):
                validate_trace(meta, row, ticks, capture, "trace", "toolcore")

    def test_explicit_empty_wire_name_is_unnamed_unattributed_mass(self):
        row, meta, ticks, capture = trace_fixture()
        ticks[0]["typeName"] = ""
        ticks[0]["frames"] = ["System.Runtime.CompilerServices.AsyncHelpers.AllocContinuation()", "SharpLink.Runtime.ReadOwnershipPipeReader.AwaitReadAsync()"]
        result = validate_trace(meta, row, ticks, capture, "trace", "toolcore")
        self.assertEqual(result["unnamedTypeTickEvents"], 1)
        self.assertEqual(result["unnamedTypeWeightBytes"], ticks[0]["allocationAmount64"])
        rules = json.loads((HERE / "owners.json").read_text())
        # Frozen rules all require a nonempty type predicate. A stack is never a substitute.
        self.assertTrue(all(rule.get("typeContains") or rule.get("typeEquals") for rule in rules))
        value = summary.summarize(meta, row, ticks, rules)
        self.assertEqual(value["classifiedSampleWeightBytesK"], 0)
        self.assertEqual(value["missingTypeWeightBytes"], ticks[0]["allocationAmount64"])
        self.assertEqual(value["owners"]["UNATTRIBUTED"]["sampledTickWeightBytes"], ticks[0]["allocationAmount64"])
        self.assertIsNone(value["exactObjectsPerOperation"])
        self.assertIsNone(value["exactOwnerCoverage"])
        del ticks[0]["typeName"]
        with self.assertRaises((ValueError, KeyError)):
            validate_trace(meta, row, ticks, capture, "trace", "toolcore")

    def test_preserved_rc1_empty_name_raw_v4_payload_layout(self):
        import struct
        # Actual first-capture payload; raw UTF16 terminator at byte26, not decoder loss.
        raw = bytes.fromhex("00A1010000000000000000A101000000000018800BE3A97F0000000000000000C8A0C0FEA17F00008000000000000000")
        self.assertEqual(len(raw), 48)
        self.assertEqual(struct.unpack_from("<Q", raw, 10)[0], 106752)
        self.assertEqual(struct.unpack_from("<Q", raw, 18)[0], 0x7fa9e30b8018)
        self.assertEqual(raw[26:28], b"\x00\x00")
        self.assertEqual(struct.unpack_from("<Q", raw, 40)[0], 128)

    def test_no_object_size_is_unknown_not_reconstructed(self):
        row, meta, ticks, capture = trace_fixture()
        ticks[0].update(eventVersion=3, objectSize=None)
        meta["allocationTickVersions"] = [3]
        result = validate_trace(meta, row, ticks, capture, "trace", "toolcore")
        self.assertEqual(result["ticksWithoutObjectSize"], 1)
        self.assertIsNone(summary.summarize(meta, row, ticks, [])["weightedObjectsPerOperationEstimate"])

    def test_missing_stack_mass_retained_if_some_stack_is_usable(self):
        row, meta, ticks, capture = trace_fixture()
        ticks.append(ticks[0] | {"frames": []})
        meta["tickEvents"] = 2
        result = validate_trace(meta, row, ticks, capture, "trace", "toolcore")
        self.assertEqual(result["ticksWithoutStacks"], 1)
        self.assertGreater(summary.summarize(meta, row, ticks, [])["missingStackWeightBytes"], 0)
        ticks[0]["frames"] = []
        with self.assertRaisesRegex(ValueError, "No resolved"):
            validate_trace(meta, row, ticks, capture, "trace", "toolcore")

    def test_capture_identity_tool_runtime_and_drain_fail_closed(self):
        for target, key, bad in (("meta", "markerBytes", 0), ("meta", "pid", 99), ("meta", "decoderVersion", "3.1.31.0"),
                                 ("meta", "decoderRuntime", "11.0.0"), ("meta", "decoderCorelibSha256", "other"),
                                 ("meta", "eventsLostReported", None), ("meta", "unrecognizedAllocationEvents", 1),
                                 ("capture", "toolVersion", "9.0.0"), ("capture", "exitCode", 1), ("capture", "abortReason", "size")):
            row, meta, ticks, capture = trace_fixture()
            (meta if target == "meta" else capture)[key] = bad
            with self.subTest(target=target, key=key), self.assertRaises(ValueError):
                validate_trace(meta, row, ticks, capture, "trace", "toolcore")

    def test_observer_contrasts_keep_arms_trace_labels_and_aa_separate(self):
        rows = []
        for position, (variant, traced) in enumerate(SEQUENCE, 1):
            row = sample_row(traced) | dict(variant=variant, sample=str(position), position=position, case=CASES[0])
            row["bytesPerOperation"] = (100 if variant == "A" else 120) + (10 if traced else 0)
            if position > 8:
                row["bytesPerOperation"] = 99999
            rows.append(row)
        value = contrasts(rows)
        self.assertFalse(value["stableAcceptance"])
        self.assertEqual(value["withinLoweringObserverContrasts"]["A"]["bytesPerOperation"]["rightMinusLeft"], 10)
        self.assertEqual(value["betweenLoweringContrasts"]["untraced"]["bytesPerOperation"]["rightMinusLeft"], 20)
        self.assertEqual(value["sameBinaryAA"][0]["bytesPerOperation"], 99999)
        self.assertEqual(value["groups"]["A-untraced"]["mean"]["bytesPerOperation"], 100)
        with self.assertRaises(ValueError):
            contrasts(rows[:-1])


if __name__ == "__main__":
    unittest.main()
