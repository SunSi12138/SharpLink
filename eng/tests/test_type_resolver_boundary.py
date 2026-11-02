"""Source/oracle controls only; not external compiler or runtime acceptance."""
import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("external_resolver_api", ROOT / "eng/check-server-tcp-api-contract.py")
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)


class TypeResolverBoundaryTests(unittest.TestCase):
    def test_only_public_type_alias_is_removed(self):
        source = (ROOT / "src/SharpLink.Runtime/Codec/RpcManifestCodecProvider.cs").read_text()
        self.assertIn("IRpcRuntimeContext runtimeContext,\n        Assembly ownerAssembly)", source)
        self.assertNotIn("IRpcRuntimeContext runtimeContext,\n        Type contractType)", source)
        self.assertIn("RpcGeneratedManifestRegistration registration,\n        Type contractType)", source)
        self.assertIn("ReferenceEquals(contractType.Assembly, registration.Manifest.OwnerAssembly)", source)
        snapshot = (ROOT / "eng/public-api/3.0.0/SharpLink.Runtime.api.txt").read_text()
        self.assertIn("GetProvider(SharpLink.Abstractions.IRpcRuntimeContext runtimeContext, System.Reflection.Assembly ownerAssembly)", snapshot)
        self.assertNotIn("GetProvider(SharpLink.Abstractions.IRpcRuntimeContext runtimeContext, System.Type contractType)", snapshot)
        test = (ROOT / "test/SharpLink.UnitTests/Runtime/RpcManifestCodecProviderTests.cs").read_text()
        self.assertIn("typeof(RpcManifestCodecProviderTests).Assembly);", test)
        self.assertIn("nameof(IRpcContractCodecProviderResolver)", test)

    def test_assembly_positive_and_type_negative_are_paired(self):
        self.assertIn("GetProvider(runtime, typeof(ConsumerProbe).Assembly)", GATE.POSITIVE)
        result = subprocess.CompletedProcess([], 1,
            "Probe.cs(2,3): error CS1503: cannot convert from 'System.Type' to 'System.Reflection.Assembly'", "")
        with patch.object(GATE, "compile_project", return_value=result) as mocked:
            with contextlib.redirect_stdout(io.StringIO()):
                GATE.verify_removed_type_resolver_alias(Path("unused"))
        self.assertEqual(mocked.call_count, 1)
        self.assertIn("GetProvider(null!, typeof(ConsumerProbe))", mocked.call_args.args[0])

    def test_type_guard_rejects_success_or_unrelated_diagnostics(self):
        for code, output in ((0, ""), (1, "no diagnostics"),
                (1, "Probe.cs(2,3): error CS0246: System.Type System.Reflection.Assembly"),
                (1, "Dependency.cs(2,3): error CS1503: System.Type System.Reflection.Assembly"),
                (1, "Probe.cs(2,3): error CS1503: System.Type System.String"),
                (1, "Probe.cs(2,3): error CS1503: System.String System.Reflection.Assembly")):
            with self.subTest(code=code, output=output):
                with patch.object(GATE, "compile_project", return_value=subprocess.CompletedProcess([], code, output, "")):
                    with self.assertRaises(RuntimeError):
                        GATE.verify_removed_type_resolver_alias(Path("unused"))


if __name__ == "__main__":
    unittest.main()
