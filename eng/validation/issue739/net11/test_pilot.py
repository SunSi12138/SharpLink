"""Offline fail-closed gate tests. These are not .NET 11 execution evidence."""
import tempfile
import unittest
from pathlib import Path

import run_pilot as pilot


def method(type_name, name, arity=0, runtime=False, builder=None):
    return {"type": type_name, "name": name, "genericArity": arity, "signature": "signature", "semanticSignature": "Void ()",
            "implementationFlags": 0x2000 if runtime else 0, "runtimeAsync": runtime,
            "stateMachineType": None if runtime else type_name + "+<" + name + ">d__0",
            "hasMoveNext": not runtime, "builderType": builder, "attributes": []}


def report(variant):
    logical = method("SharpLink.Client.SharpLinkClient", "AwaitLogicalInvocationAsync", 1, variant == "B")
    reader = method("SharpLink.Runtime.ReadOwnershipPipeReader", "AwaitReadAsync", runtime=variant == "B", builder="PoolingAsyncValueTaskMethodBuilder`1")
    value = {"runtime": pilot.ENVIRONMENT_VERSION, "framework": ".NET " + pilot.RUNTIME,
            "corelibPath": "/dotnet/shared/Microsoft.NETCore.App/" + pilot.RUNTIME + "/System.Private.CoreLib.dll",
            "assemblies": [
                {"path": "SharpLink.Client.dll", "sha256": variant, "methods": [logical]},
                {"path": "SharpLink.Runtime.dll", "sha256": variant, "methods": [reader]},
                {"path": "SharpLink.Benchmarks.dll", "sha256": "driver", "methods": [method("Program", "Main")]},
                {"path": "Fixture.dll", "sha256": variant, "methods": [method("Generated", "Dispatch", runtime=variant == "B")]},
            ]}
    for name in pilot.PRODUCTION_PROJECTS:
        if not any(x["path"] == name + ".dll" for x in value["assemblies"]):
            value["assemblies"].append({"path": name + ".dll", "sha256": variant, "methods": []})
    for assembly in value["assemblies"]:
        assembly["identity"] = {"name": assembly["path"], "version": "1.0.0.0", "culture": "", "publicKey": ""}
    return value


def calibration_row():
    return {"schemaVersion": 1, "sourceSha": pilot.SOURCE, "sample": "sample", "runtime": "11.0.0",
            "framework": ".NET " + pilot.RUNTIME, "operations": 32, "warmup": 8, "checks": 32,
            "processId": 100, "precise": True, "driverIncluded": True, "subtractionApplied": False,
            "executableSha256": "hash", "architecture": "X64", "serverGc": False, "bytes": 4352,
            "bytesPerOperation": 136.0, "elapsedSeconds": .01, "kind": "logical-incomplete-wrapper",
            "completed": 32, "expectedRuntime": "11.0.0", "threadStart": 1, "threadEnd": 1,
            "startedIncomplete": 32, "currentThreadBytes": 4352, "currentThreadBytesPerOperation": 136.0}


class Gates(unittest.TestCase):
    def test_stage_plans_preserve_pilot_and_bound_repeated_matrices(self):
        for stage, cells, expected, budget in (("pilot", 14, 84, 1100), ("stable-tiny", 12, 168, 1920), ("stable-micro", 16, 224, 1100)):
            cases, sequence, actual_budget = pilot.stage_plan(stage)
            self.assertEqual((len(cases), len(cases) * len(sequence), actual_budget), (cells, expected, budget))
            self.assertEqual([value for group, value in sequence if group == "ABBA"], list("ABBA") * (1 if stage == "pilot" else 3))
            self.assertEqual(sequence[-2:], [("AA", "A"), ("AA", "A")])
        self.assertIn(("tiny", "shm-add-c128"), pilot.stage_plan("stable-tiny")[0])
        self.assertIn(("reader", "reader-wrapped-incomplete-burst32"), pilot.stage_plan("stable-micro")[0])

    def test_repeatability_requires_six_and_separates_allocation_qps(self):
        for stage in ("pilot", "stable-tiny"):
            _, sequence, _ = pilot.stage_plan(stage)
            rows = [{"variant": variant, "group": group, "position": index + 1, "processId": index + 1,
                     "sample": str(index), "bytesPerOperation": 100, "qps": 100, "cpuNanosecondsPerOperation": 1, "p50Nanoseconds": 1, "p99Nanoseconds": 2} for index, (group, variant) in enumerate(sequence)]
            result = pilot.summarize_cell(rows, "tiny", "tcp-add-c1", stage, sequence)
            self.assertEqual(result["stable"], stage != "pilot")
            if stage != "pilot":
                rows[1]["qps"] = 300
                result = pilot.summarize_cell(rows, "tiny", "tcp-add-c1", stage, sequence)
                self.assertTrue(result["allocationStable"])
                self.assertFalse(result["qpsStable"])
                self.assertFalse(result["stable"])
                rows[-1]["bytesPerOperation"] = 9000
                result = pilot.summarize_cell(rows, "tiny", "tcp-add-c1", stage, sequence)
                self.assertTrue(result["allocationStable"])
                self.assertEqual(result["sameBinaryAA"][-1]["bytesPerOperation"], 9000)

    def test_cv_zero_and_invalid_input(self):
        self.assertEqual(pilot.cv([0, 0, 0]), 0)
        with self.assertRaises(ValueError):
            pilot.cv([0, float("nan")])

    def test_reader_burst_lifecycle_and_denominator(self):
        row = calibration_row()
        row.update(kind="reader-wrapped-incomplete-burst32", warmup=32, burstWidth=32, bursts=1,
                   wrapped=True, forcedIncomplete=True, continuationRegistrations=32, expectedContinuationRegistrations=32,
                   sameThreadVerified=True, immediateCompletionVerified=True, tokenWraps=0)
        row["warmupCounts"] = {}
        for name in ("checks", "completed", "reads", "resets", "sourceCompletions", "sourceGetResults", "advances", "startedIncomplete", "continuationRegistrations"):
            row[name] = 32
            row["warmupCounts"][name] = 32
        pilot.validate_sample(row, "reader", row["kind"], "sample", 32, 32, "hash")
        for name in ("sourceGetResults", "continuationRegistrations", "bursts", "burstWidth"):
            invalid = {**row, name: row[name] + 1}
            with self.subTest(name=name), self.assertRaises(ValueError):
                pilot.validate_sample(invalid, "reader", row["kind"], "sample", 32, 32, "hash")

    def test_compiler_command_native_and_managed_launchers(self):
        native = "/dotnet/sdk/11/Roslyn/bincore/csc /noconfig /features:runtime-async=off"
        managed = 'dotnet exec "/dotnet/sdk/11/Roslyn/bincore/csc.dll" /noconfig /features:runtime-async=on'
        self.assertEqual(pilot.compiler_commands(native + "\n" + managed + "\nFeatures=runtime-async=on"), [native, managed])

    def test_lowering_positive(self):
        result = pilot.verify_metadata(report("A"), report("B"))
        self.assertEqual(result["fixtureLoweringStatus"], "verified")

    def test_semantic_match_ignores_token_reindexing(self):
        a, b = report("A"), report("B")
        for assembly in b["assemblies"]:
            for value in assembly["methods"]:
                value["signature"] = "different-token-indexes"
        self.assertEqual(pilot.verify_metadata(a, b)["fixtureLoweringStatus"], "verified")

    def test_rejects_semantic_api_change(self):
        a, b = report("A"), report("B")
        b["assemblies"][0]["methods"][0]["semanticSignature"] = "String (Int32)"
        with self.assertRaisesRegex(ValueError, "Original method signatures"):
            pilot.verify_metadata(a, b)

    def test_lowering_rejects_ignored_switch(self):
        with self.assertRaisesRegex(ValueError, "B logical"):
            pilot.verify_metadata(report("A"), report("A"))

    def test_reader_rejects_removed_builder(self):
        a, b = report("A"), report("B")
        b["assemblies"][1]["methods"][0]["builderType"] = None
        with self.assertRaisesRegex(ValueError, "Pooled reader"):
            pilot.verify_metadata(a, b)

    def test_reader_rejects_unobserved_opt_out(self):
        a, b = report("A"), report("B")
        b["assemblies"][1]["methods"][0] = method("SharpLink.Runtime.ReadOwnershipPipeReader", "AwaitReadAsync", builder="PoolingAsyncValueTaskMethodBuilder`1")
        with self.assertRaisesRegex(ValueError, "B pooled reader"):
            pilot.verify_metadata(a, b)

    def test_driver_rejects_runtime_lowering(self):
        a, b = report("A"), report("B")
        b["assemblies"][2]["methods"][0] = method("Program", "Main", runtime=True)
        with self.assertRaisesRegex(ValueError, "Driver contains"):
            pilot.verify_metadata(a, b)

    def test_driver_rejects_byte_drift(self):
        a, b = report("A"), report("B")
        b["assemblies"][2]["sha256"] = "different"
        with self.assertRaisesRegex(ValueError, "driver assembly differs"):
            pilot.verify_metadata(a, b)

    def test_fixture_rejects_ignored_switch(self):
        a, b = report("A"), report("B")
        b["assemblies"][3]["methods"][0] = method("Generated", "Dispatch")
        with self.assertRaisesRegex(ValueError, "Generated fixture"):
            pilot.verify_metadata(a, b)

    def test_fixture_without_async_is_explicit_na(self):
        a, b = report("A"), report("B")
        for value in (a, b):
            value["assemblies"][3]["methods"] = []
        self.assertTrue(pilot.verify_metadata(a, b)["fixtureLoweringStatus"].startswith("N/A"))

    def test_runtime_requires_full_pin(self):
        a, b = report("A"), report("B")
        b["framework"] = ".NET 11.0.0"
        with self.assertRaisesRegex(ValueError, "runtime identity"):
            pilot.verify_metadata(a, b)

    def test_calibration_positive(self):
        pilot.validate_sample(calibration_row(), "calibration", "logical-incomplete-wrapper", "sample", 32, 8, "hash")

    def test_calibration_rejects_inexact_counters(self):
        for field in ("checks", "completed", "startedIncomplete", "operations"):
            row = calibration_row()
            row[field] -= 1
            with self.subTest(field=field), self.assertRaises(ValueError):
                pilot.validate_sample(row, "calibration", "logical-incomplete-wrapper", "sample", 32, 8, "hash")

    def test_calibration_rejects_wrong_denominator_or_hash(self):
        for field, value in (("bytesPerOperation", 135.0), ("currentThreadBytesPerOperation", float("nan")),
                             ("executableSha256", "wrong"), ("threadEnd", 2)):
            row = calibration_row()
            row[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                pilot.validate_sample(row, "calibration", "logical-incomplete-wrapper", "sample", 32, 8, "hash")

    def test_projection_preserves_cs_and_only_toggles_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "source"
            here = root / "eng/validation/issue739/net11"
            here.mkdir(parents=True)
            (root / "src/SharpLink.Runtime").mkdir(parents=True)
            (root / "src/SharpLink.Generator").mkdir()
            (root / "Directory.Build.props").write_text('<Project><PropertyGroup><TargetFramework Condition="\'$(MSBuildProjectName)\' != \'SharpLink.Generator\'">net10.0</TargetFramework><TargetFramework Condition="\'$(MSBuildProjectName)\' == \'SharpLink.Generator\'">netstandard2.0</TargetFramework></PropertyGroup></Project>')
            (root / "global.json").write_text('{}')
            (root / "src/SharpLink.Runtime/Test.cs").write_text('// unchanged production\n')
            (root / "src/SharpLink.Runtime/SharpLink.Runtime.csproj").write_text('<Project><TargetFramework>net10.0</TargetFramework></Project>')
            (root / "src/SharpLink.Generator/SharpLink.Generator.csproj").write_text('<Project/>')
            (here.parent / "calibration").mkdir()
            for name in ("Program.cs", "Calibration.csproj"):
                (here.parent / "calibration" / name).write_text('unchanged calibration')
            names = [str(x.relative_to(root)) for x in root.rglob('*') if x.is_file() and 'eng' not in x.relative_to(root).parts]
            targets = {variant: Path(temporary) / variant for variant in ('A', 'B')}
            for variant, target in targets.items():
                pilot.make_copy(root, here, target, variant, names)
                self.assertEqual((target / 'src/SharpLink.Runtime/Test.cs').read_bytes(), (root / 'src/SharpLink.Runtime/Test.cs').read_bytes())
                self.assertIn('net11.0', (target / 'src/SharpLink.Runtime/SharpLink.Runtime.csproj').read_text())
                self.assertIn('netstandard2.0', (target / 'Directory.Build.props').read_text())
                self.assertIn('<RestoreEnablePackagePruning>false</RestoreEnablePackagePruning>', (target / 'Directory.Build.props').read_text())
            for name in names:
                self.assertEqual(pilot.sha(targets['A'] / name), pilot.sha(targets['B'] / name))
            self.assertNotEqual(pilot.sha(targets['A'] / 'Directory.Build.targets'), pilot.sha(targets['B'] / 'Directory.Build.targets'))
            self.assertIn('net10.0', (root / 'Directory.Build.props').read_text())


if __name__ == '__main__':
    unittest.main()
