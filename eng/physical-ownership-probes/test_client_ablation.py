#!/usr/bin/env python3
"""Focused client-ablation gate fixtures. No .NET or timed workloads execute."""
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import client_ablation as ablation
from tcp import run_matrix as matrix

REQUEST = "PhysicalTcp.__IScalarStream_SharpLinkRequest_ACC1F20B0513B621"
METHODS = (
    "SharpLink.Client.SharpLinkClient+InterceptedAsyncEnumerable`1+<GetAsyncEnumerator>d__8[int]",
    f"SharpLink.Client.SharpLinkModuleRpcChannel+<InvokeServerStreamingDeferred>d__13`2[{REQUEST},int]",
    "SharpLink.Runtime.RpcSession+<PumpGeneratedOutboundStreamAsync>d__140`1[int]",
    "SharpLink.Server.ServerGeneratedBridge+<PumpOutboundStreamAsync>d__7`1[int]",
)


def native(methods=METHODS, tier="FullOpts", size=128):
    return "\n".join(f"; Assembly listing for method {method}:MoveNext():this ({tier})\n"
                     f"; native fixture\n; Total bytes of code {size}" for method in methods)


def product_snapshots(root):
    for arm in ("baseline", "candidate"):
        destination = root / arm
        destination.mkdir(parents=True)
        for name in ("Client", "Runtime", "Server", "Abstractions"):
            (destination / f"SharpLink.{name}.dll").write_bytes(f"{arm}-{name}".encode())
        (destination / "PhysicalTcpProbe.dll").write_bytes(b"same probe")
        (destination / "ScalarStreamModule.dll").write_bytes(b"same module")
        (destination / "PhysicalTcpProbe.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
            "frameworks": [{"name": name, "version": "10.0.0"}
                           for name in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App")]}}))
    (root / "prepared-manifest.json").write_text('{"fixture":true}')


def identity(prepared, manifest, arm="baseline"):
    hashes = manifest[arm + "BinaryHashes"]
    return {
        "runtimeVersion": "10.0.13", "frameworkLocations": [
            f"/private-runtime/shared/{name}/10.0.13/framework.dll"
            for name in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App")],
        "processorCount": 2, "tieredCompilation": "0", "mode": "dynamic-intercepted",
        "itemCount": 100, "workers": 1, "readyConnections": 1,
        "binaryHashes": hashes, "interceptorObserved": True,
        "wrapperTypes": ["InterceptedAsyncEnumerable[int]", "PooledAsyncStreamDispatcher[int]"],
        "streamType": f"SharpLinkModuleRpcChannel+<InvokeServerStreamingDeferred>[{REQUEST},System.Int32]",
        "loadedAssemblies": [{"name": Path(name).stem, "location": str(prepared / arm / name),
            "productVersion": "3.0.0+" + (ablation.BASELINE if arm == "baseline" and name == "SharpLink.Client.dll" else ablation.CANDIDATE)}
            for name in hashes],
    }


class ClientAblationTests(unittest.TestCase):
    def test_composition_changes_only_client_and_keeps_candidate_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            products, prepared = root / "products", root / "prepared"
            product_snapshots(products)
            manifest = ablation.compose_client_pair(products, prepared)
            differences = [name for name in manifest["baselineBinaryHashes"]
                           if manifest["baselineBinaryHashes"][name] != manifest["candidateBinaryHashes"][name]]
            self.assertEqual(["SharpLink.Client.dll"], differences)
            self.assertEqual(b"baseline-Client", (prepared / "baseline/SharpLink.Client.dll").read_bytes())
            for arm in ("baseline", "candidate"):
                for name in ("Runtime", "Server", "Abstractions"):
                    self.assertEqual(f"candidate-{name}".encode(), (prepared / arm / f"SharpLink.{name}.dll").read_bytes())
                config = json.loads((prepared / arm / "PhysicalTcpProbe.runtimeconfig.json").read_text())["runtimeOptions"]
                self.assertEqual("Disable", config["rollForward"])
                self.assertEqual(["10.0.13"] * 2, [f["version"] for f in config["frameworks"]])
            (products / "baseline/SharpLink.Client.dll").write_bytes(b"candidate-Client")
            with self.assertRaisesRegex(ValueError, "Exactly Client must differ"):
                ablation.compose_client_pair(products, root / "invalid")

    def test_native_requires_all_four_real_closed_generic_fullopts_targets(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "native.txt"
            path.write_text(native())
            rows = ablation.validate_native(path, REQUEST)
            self.assertEqual({"intercepted-iterator", "dynamic-iterator", "runtime-stream-pump", "server-stream-bridge"}, {r["target"] for r in rows})
            self.assertEqual([128] * 4, [r["bytes"] for r in rows])
            for content in (native(METHODS[:-1]), native(tier="Tier0"), native(size=0),
                            native().replace(REQUEST, "Wrong.Request"), native().replace(",int]", ",long]"),
                            native().replace("[int]", "[System.__Canon]")):
                with self.subTest(content=content):
                    path.write_text(content)
                    with self.assertRaises(ValueError):
                        ablation.validate_native(path, REQUEST)

    def test_identity_rejects_drift_external_assemblies_and_unobserved_path(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            product_snapshots(root / "products")
            prepared = root / "prepared"
            manifest = ablation.compose_client_pair(root / "products", prepared)
            data = identity(prepared, manifest)
            self.assertEqual(REQUEST, ablation.validate_process_identity(data, "baseline", prepared, manifest, Path("/private-runtime/dotnet")))
            changes = [dict(runtimeVersion="10.0.11"), dict(processorCount=10), dict(tieredCompilation="1"),
                       dict(itemCount=1), dict(binaryHashes={}), dict(interceptorObserved=False), dict(wrapperTypes=[]),
                       dict(streamType="short-circuit")]
            external = copy.deepcopy(data["loadedAssemblies"])
            external[0]["location"] = "/outside/SharpLink.Abstractions.dll"
            changes += [dict(loadedAssemblies=external), dict(loadedAssemblies=data["loadedAssemblies"] * 2)]
            wrong_source = copy.deepcopy(data["loadedAssemblies"])
            next(a for a in wrong_source if a["name"] == "SharpLink.Client")["productVersion"] = "3.0.0+wrong"
            changes += [dict(loadedAssemblies=wrong_source)]
            for change in changes:
                with self.subTest(change=change), self.assertRaises(ValueError):
                    ablation.validate_process_identity(dict(data, **change), "baseline", prepared, manifest, Path("/private-runtime/dotnet"))

    def test_ticks_preserve_counts_units_and_integer_durations(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "ticks.json"
            data = {"stopwatchFrequency": 10000000, "operations": 3, "workers": 1,
                    "samples": [{"worker": 0, "durationTicks": [15, 4, 9]}]}
            path.write_text(json.dumps(data))
            ablation.validate_ticks(path, {"operations": 3})
            for change in (dict(stopwatchFrequency=0), dict(operations=2), dict(workers=2),
                           dict(samples=[{"worker": 0, "durationTicks": [15, 4]}]),
                           dict(samples=[{"worker": 0, "durationTicks": [15, -1, 9]}]),
                           dict(samples=[{"worker": 0, "durationTicks": [15, 1.5, 9]}])):
                path.write_text(json.dumps(dict(data, **change)))
                with self.subTest(change=change), self.assertRaises(ValueError):
                    ablation.validate_ticks(path, {"operations": 3})

    def test_failed_preflight_never_starts_timing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            product_snapshots(root / "products")
            output = root / "output"
            output.mkdir()
            with patch.object(ablation, "cpu_topology", return_value={"reportedTopology": "fixture"}), \
                 patch.object(ablation, "run") as run, \
                 patch.object(ablation, "execute_sample", return_value=7) as execute:
                with self.assertRaisesRegex(RuntimeError, "exit 7"):
                    ablation.run_client_ablation(root / "products", output, Path("/private-runtime/dotnet"), Path("/sdk/dotnet"), "0,1", "fixture-tool")
            self.assertEqual(1, execute.call_count)
            self.assertEqual([Path("/sdk/dotnet"), "build-server", "shutdown"], run.call_args.args[0])
            self.assertEqual(1, run.call_count)
            self.assertEqual("Failed", json.loads((output / "preflight-gate.json").read_text())["status"])
            self.assertFalse((output / "measurement-start.json").exists())
            self.assertFalse((output / "client-tcp").exists())

    def test_missing_native_closes_gate_before_timing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            product_snapshots(root / "products")
            output = root / "output"
            output.mkdir()
            def preflight(command, log, host, arm, round_number, environment, timeout):
                Path(command[-2]).write_text(json.dumps({"kind": "non-timed-preflight", "operations": 256}))
                Path(environment["DOTNET_JitStdOutFile"]).write_text(native(METHODS[:-1]))
                return 0
            with patch.object(ablation, "cpu_topology", return_value={"reportedTopology": "fixture"}), \
                 patch.object(ablation, "run") as run, \
                 patch.object(ablation, "execute_sample", side_effect=preflight), \
                 patch.object(ablation, "validate_process_identity", return_value=REQUEST):
                with self.assertRaisesRegex(ValueError, "Missing real-path native targets"):
                    ablation.run_client_ablation(root / "products", output, Path("/private-runtime/dotnet"), Path("/sdk/dotnet"), "0,1", "fixture-tool")
            self.assertEqual(1, run.call_count)
            self.assertEqual("Failed", json.loads((output / "preflight-gate.json").read_text())["status"])
            self.assertFalse((output / "measurement-start.json").exists())

    def test_topology_distinguishes_smt_from_reported_cores(self):
        with tempfile.TemporaryDirectory() as directory:
            sysfs = Path(directory)
            for cpu, core in ((0, 0), (1, 0), (2, 1)):
                root = sysfs / f"cpu{cpu}/topology"
                root.mkdir(parents=True)
                for name, value in (("physical_package_id", "0"), ("core_id", str(core)), ("thread_siblings_list", "0,1" if core == 0 else "2")):
                    (root / name).write_text(value)
            with patch.object(ablation.os, "sched_getaffinity", return_value={0, 1, 2}, create=True):
                smt = ablation.cpu_topology("0,1", sysfs)
                self.assertEqual("same-core SMT siblings", smt["reportedTopology"])
                self.assertEqual([0, 1], smt["selectedLogicalCpus"])
                self.assertEqual(3, len(smt["available"]))
                self.assertEqual("distinct reported cores", ablation.cpu_topology("0,2", sysfs)["reportedTopology"])
                for affinity in ("0,0", "0", "0,3"):
                    with self.assertRaises(ValueError):
                        ablation.cpu_topology(affinity, sysfs)
                for name in ("physical_package_id", "core_id"):
                    path = sysfs / "cpu0/topology" / name
                    path.write_text("-1")
                    with self.assertRaisesRegex(ValueError, "unknown package/core topology"):
                        ablation.cpu_topology("0,1", sysfs)
                    path.write_text("0")

    def test_exit_zero_missing_ticks_and_wrong_identity_are_excluded_before_summary(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            product_snapshots(root / "products")
            prepared = root / "prepared"
            manifest = ablation.compose_client_pair(root / "products", prepared)
            results = root / "results"
            (results / "runs").mkdir(parents=True)
            (results / "ticks").mkdir()
            for r in range(1, 7):
                for arm in ("baseline", "candidate"):
                    name = f"dynamic-intercepted-n100-c1-r{r}-{arm}"
                    sample = identity(prepared, manifest, arm)
                    sample.update(operations=3)
                    for metric in ("operationsPerSecond", "p50Us", "p99Us", "p999Us", "allocatedBytesPerOperation", "cpuUsPerOperation"):
                        sample[metric] = 1 if arm == "baseline" else 2
                    if r == 2 and arm == "candidate":
                        sample["runtimeVersion"] = "10.0.11"
                    (results / "runs" / (name + ".json")).write_text(json.dumps(sample))
                    (results / "runs" / (name + ".host.json")).write_text('{"exitCode":0,"status":"success"}')
                    if not (r == 1 and arm == "candidate"):
                        (results / "ticks" / (name + ".json")).write_text(json.dumps({
                            "stopwatchFrequency": 10000000, "operations": 3, "workers": 1,
                            "samples": [{"worker": 0, "durationTicks": [15, 4, 9]}]}))
            originals = {p.name: p.read_text() for p in (results / "runs").iterdir()}
            report = ablation.validate_measurements(results, prepared, manifest, Path("/private-runtime/dotnet"))
            self.assertEqual("Failed", report["status"])
            self.assertEqual("Missing evidence: ticks", report["samples"]["dynamic-intercepted-n100-c1-r1-candidate"]["reason"])
            self.assertIn("Wrong executing runtime", report["samples"]["dynamic-intercepted-n100-c1-r2-candidate"]["reason"])
            result = subprocess.run([sys.executable, str(Path(ablation.__file__).parent / "tcp/summarize.py"),
                                     "--input", str(results), "--validation-manifest", str(results / "sample-validation.json")],
                                    capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            summary = json.loads((results / "summary.json").read_text())
            integrity = json.loads((results / "summary-integrity.json").read_text())
            self.assertEqual(4, summary[0]["pairs"])
            self.assertEqual("Failed", integrity["evidenceValidation"])
            self.assertEqual(10, integrity["completedRawSamples"])
            self.assertEqual([1, 2], [row["round"] for row in integrity["incompletePairs"]])
            self.assertEqual(["invalid-evidence", "invalid-evidence"], [row["status"] for row in integrity["failedSamples"]])
            self.assertEqual([0, 0], [row["exitCode"] for row in integrity["failedSamples"]])
            self.assertEqual(originals, {p.name: p.read_text() for p in (results / "runs").iterdir()})

    def test_missing_metric_cannot_claim_complete_when_summary_keeps_five_pairs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runs = root / "runs"
            runs.mkdir()
            for r in range(1, 7):
                for arm in ("baseline", "candidate"):
                    name = f"dynamic-intercepted-n100-c1-r{r}-{arm}"
                    sample = {"mode": "dynamic-intercepted", "itemCount": 100, "workers": 1}
                    for metric in ("operationsPerSecond", "p50Us", "p99Us", "p999Us", "allocatedBytesPerOperation", "cpuUsPerOperation"):
                        sample[metric] = 1 if arm == "baseline" else 2
                    if r == 2 and arm == "candidate":
                        del sample["p999Us"]
                    (runs / (name + ".json")).write_text(json.dumps(sample))
                    (runs / (name + ".host.json")).write_text('{"exitCode":0,"status":"success"}')
            command = [sys.executable, str(Path(ablation.__file__).parent / "tcp/summarize.py"), "--input", str(root)]
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(5, json.loads((root / "summary.json").read_text())[0]["pairs"])
            self.assertEqual("invalid-sample", json.loads((root / "summary-integrity.json").read_text())["failedSamples"][0]["status"])
            with self.assertRaisesRegex(ValueError, "all 12 measured samples"):
                ablation.validate_summary(root)
            path = runs / "dynamic-intercepted-n100-c1-r2-candidate.json"
            sample = json.loads(path.read_text())
            sample["p999Us"] = 2
            path.write_text(json.dumps(sample))
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            ablation.validate_summary(root)
            summary = json.loads((root / "summary.json").read_text())
            summary[0]["mode"] = "static-intercepted"
            (root / "summary.json").write_text(json.dumps(summary))
            with self.assertRaisesRegex(ValueError, "fixed scene and six pairs"):
                ablation.validate_summary(root)

    def test_schedule_attempts_twelve_once_with_balanced_order_despite_failures(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            product_snapshots(root / "products")
            commands = []
            def sample(command, log, host, arm, round_number, environment, timeout):
                commands.append((arm, round_number))
                self.assertEqual("0", environment["DOTNET_TieredCompilation"])
                self.assertNotIn("DOTNET_JitDisasm", environment)
                self.assertEqual("measure", command[-2])
                if len(commands) == 1:
                    raise subprocess.TimeoutExpired(command, timeout)
                return 4
            args = ["run_matrix.py", "--prepared", str(root / "products"), "--output", str(root / "results"),
                    "--baseline-revision", "old-client", "--candidate-revision", "new-client", "--runtime-mode", "fullopts",
                    "--export-ticks", "--keep-going", "--items", "100", "--modes", "dynamic-intercepted"]
            with patch.object(sys, "argv", args), patch.object(matrix, "execute_sample", side_effect=sample), \
                 patch.dict(matrix.os.environ, {"DOTNET_JitDisasm": "must not leak"}):
                with self.assertRaisesRegex(RuntimeError, "12 failures"):
                    matrix.main()
            expected = [(arm, r) for r in range(1, 7) for arm in (("baseline", "candidate") if r % 2 else ("candidate", "baseline"))]
            self.assertEqual(expected, commands)
            self.assertEqual(12, len(json.loads((root / "results/matrix-failures.json").read_text())))
            plan = json.loads((root / "results/matrix-manifest.json").read_text())
            self.assertEqual((3, 6, 1, 6), tuple(plan["arguments"][key] for key in ("warmup", "seconds", "workers", "rounds")))


if __name__ == "__main__":
    unittest.main()
