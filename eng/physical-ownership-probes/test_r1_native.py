"""R1 driver fixtures: no runtime process or performance workload."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import client_ablation
import r1_native as r1
from test_client_ablation import identity, native, product_snapshots


class R1NativeTests(unittest.TestCase):
    def reference(self, root):
        products = root / "products"
        product_snapshots(products)
        reference = root / "reference"
        reference.mkdir()
        client_ablation.compose_client_pair(products, reference / "client-prepared")
        (reference / "client-ablation-plan.json").write_text(json.dumps({
            "toolRevision": r1.REFERENCE_TOOL, "newClientRevision": r1.CANDIDATE,
            "commonProductRevision": r1.CANDIDATE, "runtimeMode": "default-pgo"}))
        client = root / "SharpLink.Client.dll"
        client.write_bytes(b"R1 client")
        client.with_suffix(".pdb").write_bytes(b"R1 symbols")
        return reference, client

    def test_procfs_fallback_requires_complete_explicit_unique_ids(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            info = root / "cpuinfo"
            valid = "processor : 0\nphysical id : 0\ncore id : 0\n\nprocessor : 1\nphysical id : 0\ncore id : 1\n"
            info.write_text(valid)
            with patch.object(r1.os, "sched_getaffinity", return_value={0, 1}):
                topology = r1.topology_for_host("0,1", root / "absent-sysfs", info)
                self.assertEqual("distinct reported cores", topology["reportedTopology"])
                for invalid in (valid.replace("core id : 1", "core id : -1"),
                                valid.split("\n\n")[0], valid + "\n" + valid,
                                valid.replace("physical id : 0", "unknown : 0")):
                    info.write_text(invalid)
                    with self.assertRaises((ValueError, KeyError)):
                        r1.topology_for_host("0,1", root / "absent-sysfs", info)
            info.write_text(valid)
            with patch.object(r1.os, "sched_getaffinity", return_value={0, 1}):
                with self.assertRaises(ValueError):
                    r1.topology_for_host("0,0", root / "absent-sysfs", info)
            sysfs = root / "partial-sysfs"
            sysfs.mkdir()
            with self.assertRaises(FileNotFoundError):
                r1.topology_for_host("0,1", sysfs, info)

    def test_composition_keeps_exact_common_dlls(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            reference, client = self.reference(root)
            manifest = r1.compose(reference, client, root / "prepared")
            base, candidate = manifest["baselineBinaryHashes"], manifest["candidateBinaryHashes"]
            self.assertEqual(["SharpLink.Client.dll"], [k for k in base if base[k] != candidate[k]])
            self.assertEqual(r1.CANDIDATE, manifest["productRevisions"]["baseline"]["SharpLink.Client"])
            self.assertEqual(r1.R1, manifest["productRevisions"]["candidate"]["SharpLink.Client"])
            self.assertEqual(r1.CANDIDATE, manifest["productRevisions"]["candidate"]["SharpLink.Runtime"])
            (reference / "client-prepared/candidate/SharpLink.Runtime.dll").write_bytes(b"tampered")
            with self.assertRaisesRegex(ValueError, "archived manifest"):
                r1.compose(reference, client, root / "bad")

    def test_revisions_fail_closed_and_default_behavior_unchanged(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            reference, client = self.reference(root)
            prepared = root / "prepared"
            manifest = r1.compose(reference, client, prepared)
            data = identity(prepared, manifest, "candidate")
            data.update(tieredCompilation="default", tieredPgo="default")
            for a in data["loadedAssemblies"]:
                if a["name"].startswith("SharpLink."):
                    a["productVersion"] = "3.0.0+" + manifest["productRevisions"]["candidate"][a["name"]]
            runtime = Path("/private-runtime/dotnet")
            client_ablation.validate_process_identity(data, "candidate", prepared, manifest, runtime, "default-pgo", product_revisions=manifest["productRevisions"]["candidate"])
            with self.assertRaisesRegex(ValueError, "revision differs"):
                client_ablation.validate_process_identity(data, "candidate", prepared, manifest, runtime, "default-pgo")
            with self.assertRaises(KeyError):
                client_ablation.validate_process_identity(data, "candidate", prepared, manifest, runtime, "default-pgo", product_revisions={})

    def test_both_failed_arms_retained_without_retry_or_timing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(r1, "execute_sample", return_value=17) as execute:
                with self.assertRaisesRegex(ValueError, "retained both"):
                    r1.preflight(root / "prepared", {}, Path("/private-runtime/dotnet"), "0,1", root / "preflight")
            self.assertEqual(2, execute.call_count)
            for call in execute.call_args_list:
                self.assertEqual(["preflight", "default-pgo"], call.args[0][-2:])
                self.assertEqual(["0", "0"], call.args[0][-5:-3])
            gate = json.loads((root / "preflight/gate.json").read_text())
            self.assertEqual("Failed", gate["status"])
            self.assertEqual({"baseline", "candidate"}, set(gate["arms"]))
            self.assertEqual(0, gate["timedProcesses"])


if __name__ == "__main__":
    unittest.main()
