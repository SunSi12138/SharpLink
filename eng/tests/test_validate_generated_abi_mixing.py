import importlib.util
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "validate_generated_abi_mixing",
    Path(__file__).resolve().parents[1] / "validate-generated-abi-mixing.py",
)
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)


class AbiMixingValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name)
        self.project = self.path / "NewGeneratorOldAbstractions.csproj"
        self.source = self.path / "MixedContract.cs"
        self.project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>'
            '<PackageReference Include="SharpLink.Sdk" Version="[$(SharpLinkCurrentVersion)]" />'
            '<PackageReference Include="SharpLink.Abstractions" Version="[1.1.1]" />'
            '</ItemGroup></Project>',
            encoding="utf-8",
        )
        self.source.write_text(
            "[RpcContract] public interface INewGeneratorOldAbstractionsService {"
            " ValueTask<int> AddAsync(int a, int b); }"
            "[RpcService] public class Implementation {}",
            encoding="utf-8",
        )

    def fixture(self):
        MOD.validate_fixture(self.project, "new-generator-old-abstractions")

    def test_valid_fixture_and_positive_control_copy(self):
        self.fixture()
        positive_dir = self.path / "positive"
        MOD.make_positive(self.project, positive_dir)
        content = (positive_dir / self.project.name).read_text()
        self.assertEqual(content.count('Version="[$(SharpLinkCurrentVersion)]"'), 2)
        self.assertEqual((positive_dir / "MixedContract.cs").read_bytes(), self.source.read_bytes())

    def test_missing_project_is_not_a_rejection(self):
        self.project.unlink()
        with self.assertRaisesRegex(ValueError, "missing ABI fixture project"):
            self.fixture()
        self.assertFalse(MOD.is_expected_rejection("restore", "MSBUILD : error MSB1009: Project file does not exist."))

    def test_missing_source_is_not_a_rejection(self):
        self.source.unlink()
        with self.assertRaisesRegex(ValueError, "missing ABI fixture source"):
            self.fixture()

    def test_tampered_package_graph_is_rejected(self):
        self.project.write_text(self.project.read_text().replace("[1.1.1]", "[3.0.0]"))
        with self.assertRaisesRegex(ValueError, "expected exact package graph"):
            self.fixture()

    def test_unrelated_errors_and_log_noise_do_not_pass(self):
        for phase, log in [
            ("build", "MixedContract.cs(1,15): error CS0246: The type UnrelatedTypo could not be found"),
            ("build", "MixedContract.cs(1,15): error CS0029: Cannot convert string to int"),
            ("build", "MixedContract.cs(1,15): error CS0246: IRpcStub could not be found"),
            ("restore", "error NU1301: Unable to load the service index from source"),
            ("restore", "error NU1101: Unable to find package SharpLink.Sdk"),
            ("restore", "warning NU1605: Package downgrade: SharpLink.Abstractions"),
            ("build", "Note: SharpLink.Generator/IRpcStub appears in a path but no diagnostic"),
        ]:
            with self.subTest(phase=phase, log=log):
                self.assertFalse(MOD.is_expected_rejection(phase, log))

    def test_targeted_diagnostics_are_allowed(self):
        self.assertTrue(MOD.is_expected_rejection(
            "restore",
            "error NU1605: Detected package downgrade: SharpLink.Abstractions from 3.0.0 to 1.1.1",
        ))
        self.assertTrue(MOD.is_expected_rejection(
            "build",
            "obj/Release/net10.0/SharpLink.Generator/RpcStub.g.cs(4,3): error CS0246: IRpcStub could not be found",
        ))


if __name__ == "__main__":
    unittest.main()
