"""Source/oracle controls only; actual external C# compilation remains required."""
import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("external_lifecycle_api", ROOT / "eng/check-server-tcp-api-contract.py")
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)


class ClientLifecycleBoundaryTests(unittest.TestCase):
    def test_positive_uses_real_forwarding_consumers_and_sdk_constraint(self):
        self.assertIn("using SharpLink.Sdk;", GATE.PREFIX)
        for name in ("ExternalClientDecorator", "ExternalMultiClusterDecorator"):
            self.assertIn(name, GATE.POSITIVE)
            self.assertIn(f"public sealed class {name}", GATE.CLIENT_DECORATORS)
        for member in ("LifecycleState", "Readiness", "ClusterState"):
            self.assertIn(f"=> inner.{member};", GATE.CLIENT_DECORATORS)
        for member in ("StartAsync", "WaitForReadyAsync", "WaitForShutdownAsync",
                       "CreateProxy<TContract>", "GetLocalContract<TContract>", "CreateContractChannel"):
            self.assertIn(f"=> inner.{member}(", GATE.CLIENT_DECORATORS)
        self.assertNotIn("new RpcProxy", GATE.CLIENT_DECORATORS)
        self.assertNotIn("new RpcLocalContract", GATE.CLIENT_DECORATORS)

    def test_required_members_have_no_compatibility_default(self):
        client = (ROOT / "src/SharpLink.Abstractions/ISharpLinkClient.cs").read_text()
        multi = (ROOT / "src/SharpLink.Abstractions/ISharpLinkMultiClusterClient.cs").read_text()
        for source in (client, multi):
            self.assertIn("SharpLinkClientLifecycleState LifecycleState { get; }", source)
            self.assertIn("SharpLinkReadinessState Readiness { get; }", source)
            for name, result in (("StartAsync", "ValueTask"), ("WaitForReadyAsync", "ValueTask"),
                                 ("WaitForShutdownAsync", "Task")):
                self.assertIn(f"{result} {name}(CancellationToken cancellationToken = default);", source)
            self.assertIn("ValueTask ConnectAsync(CancellationToken cancellationToken = default);", source)
        self.assertIn("SharpLinkClusterState ClusterState { get; }", client)
        self.assertIn("SharpLinkClusterState GetClusterRuntimeState(SharpLinkClusterKey cluster);", multi)
        self.assertIn("SharpLinkReadinessState GetClusterReadiness(SharpLinkClusterKey cluster);", multi)
        self.assertIn("CancellationToken cancellationToken = default);", multi.split("ValueTask WaitForReadyAsync(\n", 1)[1].split("///", 1)[0])
        self.assertIn("does not expose endpoint readiness details", client)
        self.assertIn("does not support endpoint readiness waits", client)
        self.assertIn("does not expose non-throwing cluster status queries", multi)

    def test_package_probe_checks_fourteen_actual_abstract_members(self):
        source = (ROOT / "test/SharpLink.PackageSmoke/ExternalApiBoundaryProbe.cs").read_text()
        self.assertEqual(source.count("RequireAbstract(client,"), 6)
        self.assertEqual(source.count("RequireAbstract(multi,"), 8)
        self.assertIn("method is null || !method.IsAbstract", source)
        self.assertIn("VerifyRequiredLifecycleMembers();", source)

    def test_real_constructor_arities_remain_internal(self):
        proxy = (ROOT / "src/SharpLink.Abstractions/RpcProxy.cs").read_text()
        local = (ROOT / "src/SharpLink.Abstractions/RpcLocalContract.cs").read_text()
        self.assertIn("internal RpcProxy(TContract service, IRpcContractChannel channel)", proxy)
        self.assertIn("internal RpcLocalContract(", local)
        for name in ("Guid issuerId", "long registrationGeneration", "long contractId", "RpcHash128 rpcAssemblyHash"):
            self.assertIn(name, local)
        results = [subprocess.CompletedProcess([], 1, f"Probe.cs(2,3): error CS1729: {name} has no constructor that takes {arity} arguments", "")
                   for name, arity in (("RpcProxy", 2), ("RpcLocalContract", 4))]
        with patch.object(GATE, "compile_project", side_effect=results) as mocked:
            with contextlib.redirect_stdout(io.StringIO()):
                GATE.verify_issued_handle_constructors(Path("unused"))
        self.assertEqual(mocked.call_count, 2)
        self.assertIn("new RpcProxy<IService>(null!, null!)", mocked.call_args_list[0].args[0])
        self.assertIn("new RpcLocalContract(System.Guid.NewGuid(), 1, 1, default)", mocked.call_args_list[1].args[0])

    def test_constructor_guard_rejects_unrelated_or_wrong_arity_diagnostics(self):
        for code, output in ((0, ""), (1, "missing diagnostic"),
                (1, "Probe.cs(2,3): error CS0246: RpcProxy 2 arguments"),
                (1, "Dependency.cs(2,3): error CS1729: RpcProxy 2 arguments"),
                (1, "Probe.cs(2,3): error CS1729: OtherType 2 arguments"),
                (1, "Probe.cs(2,3): error CS1729: RpcProxy 3 arguments")):
            with self.subTest(code=code, output=output):
                result = subprocess.CompletedProcess([], code, output, "")
                with patch.object(GATE, "compile_project", return_value=result):
                    with self.assertRaises(RuntimeError):
                        GATE.verify_issued_handle_constructors(Path("unused"))

    def test_manifest_oracle_uses_current_exact_identity(self):
        source = (ROOT / "test/SharpLink.PackageSmoke/Program.cs").read_text()
        self.assertIn("manifest.ApiVersion != SharpLinkGeneratedManifestVersions.Api", source)
        self.assertIn("manifest.ProtocolVersion != SharpLinkGeneratedManifestVersions.Protocol", source)
        self.assertIn("string.Equals(manifest.AbiIdentity, SharpLinkGeneratedManifestVersions.AbiIdentity, StringComparison.Ordinal)", source)
        self.assertNotIn("manifest.ProtocolVersion != 2", source)


if __name__ == "__main__":
    unittest.main()
