#!/usr/bin/env python3
"""Synthetic correctness fixtures only: no benchmark process or RPC timing."""
import contextlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from analyze import aggregate_samples, analyze, compare
from prepare_controlled import select_cpus, validate_runtime_listing, pin_runtimeconfig
from run import run_campaign
from runtime_contract import RUNTIME, validate_metadata
from spec import CALLS, CELLS, CONTROLLED, LOCAL, PROFILES, SAMPLES, validate_schedule


def manifest(profile):
    pairs = PROFILES[profile]["pairs"]
    return {"profile": profile, "pairs": pairs,
            "processes": [{"arm": arm, "name": f"process-{i:02}-{arm}"}
                          for i, arm in enumerate("".join(pairs))]}


def process(arm):
    # A/O is adverse, B/A favorable, B/O adverse: no transitive release shortcut.
    scale = {"O": 1, "A": 2, "B": 1.5}[arm]
    return {"arm": arm, "cells": {c: {"meanNs": 100 * (scale if c.startswith("I") else 1),
                                     "p99Ns": 200 * (scale if c.startswith("I") else 1)} for c in CELLS}}


def rows(mean=100):
    return [{"cell": c, "sample": s, "completed": CALLS, "failure": None,
             "frequency": 1_000_000_000, "elapsedTicks": CALLS*mean,
             "ticks": [1]*1980 + [3]*20, "bytes": CALLS*8,
             "cpuMilliseconds": 2, "gcCollections": [1, 2, 0]}
            for c in CELLS for s in range(SAMPLES)]


class ToolFixtures(unittest.TestCase):
    def test_frozen_schedule_and_contrast_orientation(self):
        m = manifest(CONTROLLED)
        validate_schedule(m)
        self.assertEqual(len(m["processes"]), 36)
        expected = {"A/O": (2, "adverse exploratory signal"),
                    "B/A": (.75, "favorable exploratory signal"),
                    "B/O": (1.5, "adverse exploratory signal")}
        processes = [process(p["arm"]) for p in m["processes"]]
        for label, arms in PROFILES[CONTROLLED]["comparisons"].items():
            with self.subTest(contrast=label):
                r = compare(processes, m["pairs"], *arms)
                self.assertEqual(len(r["crossPairs"]), 4)
                self.assertEqual(len(r["samePairs"]), 4)
                for pair in r["crossPairs"]:
                    self.assertAlmostEqual(pair["meanRatios"]["I/T"], expected[label][0])
                self.assertEqual(r["decisions"], dict.fromkeys(["T", "U"], expected[label][1]))
                self.assertEqual(r["noiseEnvelopes"], {"T": 0, "U": 0})
        m["pairs"] = list(reversed(m["pairs"]))
        with self.assertRaises(ValueError):
            validate_schedule(m)

    def test_noise_is_contrast_specific_and_same_pairs_are_chronological(self):
        m = manifest(CONTROLLED)
        processes = [process(p["arm"]) for p in m["processes"]]
        processes[1]["cells"]["I/T"]["meanNs"] *= 10  # OO, excluded from B/A
        ba = compare(processes, m["pairs"], "A", "B")
        bo = compare(processes, m["pairs"], "O", "B")
        self.assertEqual(ba["noiseEnvelopes"]["T"], 0)
        self.assertAlmostEqual(bo["samePairs"][0]["meanRatios"]["I/T"], 10)
        self.assertGreater(bo["noiseEnvelopes"]["T"], 2)
        self.assertEqual(bo["decisions"]["T"], "inconclusive")
        self.assertEqual(bo["decisions"]["U"], "adverse exploratory signal")

    def test_equality_and_one_discordant_pair_cannot_pass(self):
        m = manifest(CONTROLLED)
        equal = [process("O") for _ in m["processes"]]
        self.assertEqual(compare(equal, m["pairs"], "A", "B")["decisions"]["T"], "inconclusive")
        p = [process(x["arm"]) for x in m["processes"]]
        p[5]["cells"]["I/T"]["meanNs"] = 300  # first AB, B worse than A
        r = compare(p, m["pairs"], "A", "B")
        self.assertLess(r["cellSummary"]["I/T"]["meanGeometricRatio"], 1)
        self.assertEqual(r["decisions"]["T"], "inconclusive")

    def test_elapsed_estimator_and_nearest_rank(self):
        samples = rows()[:5]
        r = aggregate_samples(samples)
        self.assertEqual(r["meanNs"], 100)  # not per-call mean 1.02
        self.assertEqual(r["p99Ns"], 1)  # 9,900 ones then 100 threes; no interpolation
        self.assertEqual(r["bytesPerCall"], 8)
        self.assertEqual(r["cpuNsPerCall"], 1000)
        self.assertEqual(r["gcCollections"], [5, 10, 0])
        samples[-1]["completed"] -= 1
        with self.assertRaises(ValueError):
            aggregate_samples(samples)

    def test_complete_three_arm_and_legacy_fixture_outputs(self):
        for profile, calls in [(CONTROLLED, 1440000), (LOCAL, 640000)]:
            with self.subTest(profile=profile), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                m = manifest(profile)
                ledger = []
                for p in m["processes"]:
                    p["samples"] = str(root / (p["name"] + ".samples.jsonl"))
                    data = rows({"O": 100, "A": 200, "B": 150}[p["arm"]])
                    Path(p["samples"]).write_text("\n".join(json.dumps(r) for r in data))
                    ledger.append({"name": p["name"], "valid": True})
                (root / "manifest.json").write_text(json.dumps(m))
                (root / "execution.jsonl").write_text("\n".join(json.dumps(r) for r in ledger))
                with contextlib.redirect_stdout(io.StringIO()):
                    result = analyze(root)
                self.assertEqual(result["completedCalls"], calls)
                self.assertEqual(len(result["processes"]), len(m["processes"]))
                if profile == CONTROLLED:
                    self.assertEqual(set(result["comparisons"]), {"A/O", "B/A", "B/O"})
                else:
                    self.assertEqual(len(result["crossPairs"]), 4)
                    self.assertNotIn("comparisons", result)
                self.assertTrue((root / "process-aggregates.csv").is_file())
                (root / "execution.jsonl").write_text(json.dumps(ledger[0]))
                with self.assertRaises(ValueError):
                    analyze(root)

    def test_cpu_selection_requires_two_reported_physical_cores(self):
        topology = [{"cpu": 4, "package": 0, "core": 2}, {"cpu": 2, "package": 0, "core": 2},
                    {"cpu": 7, "package": 0, "core": 3}]
        self.assertEqual(select_cpus(topology), [2, 7])
        with self.assertRaises(ValueError):
            select_cpus(topology[:2])
        with self.assertRaises(ValueError):
            select_cpus([{"cpu": 0, "package": -1, "core": -1}])

    def test_runtime_install_and_roll_forward_contract(self):
        root = Path("/private/runtime")
        listing = "\n".join(f"{name} {RUNTIME} [{root}/shared/{name}]" for name in
                            ["Microsoft.NETCore.App", "Microsoft.AspNetCore.App"])
        validate_runtime_listing(root / "dotnet", listing, "")
        for bad, sdk in [(listing.replace(RUNTIME, "10.0.13"), ""), (listing, "10.0.102 [sdk]"),
                         (listing.splitlines()[0], ""), (listing.replace("/private/runtime", "/other"), "")]:
            with self.assertRaises(ValueError):
                validate_runtime_listing(root / "dotnet", bad, sdk)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "probe.runtimeconfig.json"
            path.write_text(json.dumps({"runtimeOptions": {"framework": {"name": "Microsoft.NETCore.App", "version": "10.0.0"}}}))
            config = pin_runtimeconfig(path)["runtimeOptions"]
            self.assertEqual(config["framework"]["version"], RUNTIME)
            self.assertEqual(config["rollForward"], "Disable")

    def test_actual_runtime_metadata_and_affinity_are_mandatory(self):
        root = str(Path("/private/runtime").resolve())
        data = {"runtimeVersion": RUNTIME, "coreFrameworkLocation": f"{root}/shared/Microsoft.NETCore.App/{RUNTIME}/System.Private.CoreLib.dll",
                "processPath": f"{root}/dotnet", "allowedCpuList": "2,7", "processorCount": 2,
                "architecture": "X64", "serverGC": False, "performanceEnvironment": {}, "extraPerformanceEnvironment": {}}
        m = {"runtimeContract": {"privateRoot": root, "selectedCpus": [2, 7]}}
        validate_metadata(data, m)
        for key, value in [("runtimeVersion", "10.0.13"), ("processorCount", 1), ("allowedCpuList", "2-3"),
                           ("processPath", "/elsewhere/dotnet"), ("extraPerformanceEnvironment", {"COMPlus_JitMinOpts": "1"})]:
            changed = dict(data, **{key: value})
            with self.assertRaises(ValueError):
                validate_metadata(changed, m)

    def test_missing_sample_stops_even_with_zero_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            m = manifest(LOCAL)
            for p in m["processes"]:
                p.update(command=["not-executed"], cwd=str(root), samples=str(root / (p["name"] + ".samples.jsonl")))
            (root / "manifest.json").write_text(json.dumps(m))
            def incomplete(*args, **kwargs):
                Path(m["processes"][0]["samples"]).write_text("\n".join(json.dumps(r) for r in rows()[:-1]))
                return subprocess.CompletedProcess([], 0)
            with patch("run.subprocess.run", side_effect=incomplete) as execute, patch("run.snapshot", return_value={"synthetic": True}), contextlib.redirect_stdout(io.StringIO()):
                self.assertFalse(run_campaign(root))
            self.assertEqual(execute.call_count, 1)
            record = json.loads((root / "execution.jsonl").read_text())
            self.assertEqual(record["exitCode"], 0)
            self.assertEqual(record["samplesPresent"], 19)
            self.assertEqual(record["completedCalls"], 38000)
            self.assertFalse(record["valid"])
            self.assertEqual(len(Path(m["processes"][0]["samples"]).read_text().splitlines()), 19)

    def test_failed_process_is_retained_and_never_replaced(self):
        for outcome in [subprocess.CompletedProcess([], 7), OSError("missing host"), subprocess.TimeoutExpired([], 180)]:
            with self.subTest(outcome=type(outcome).__name__), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                m = manifest(LOCAL)
                for p in m["processes"]:
                    p.update(command=["not-executed"], cwd=str(root), samples=str(root / (p["name"] + ".samples.jsonl")))
                (root / "manifest.json").write_text(json.dumps(m))
                mock_args = {"side_effect": outcome} if isinstance(outcome, Exception) else {"return_value": outcome}
                with patch("run.subprocess.run", **mock_args) as execute, patch("run.snapshot", return_value={"synthetic": True}), contextlib.redirect_stdout(io.StringIO()):
                    self.assertFalse(run_campaign(root))
                self.assertEqual(execute.call_count, 1)
                entries = (root / "execution.jsonl").read_text().splitlines()
                self.assertEqual(len(entries), 1)
                self.assertFalse(json.loads(entries[0])["valid"])
                self.assertFalse(json.loads((root / "completion.json").read_text())["complete"])
                self.assertTrue((root / (m["processes"][0]["name"] + ".stderr")).exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
