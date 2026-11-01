"""Source/guard tests only: do not substitute for external C# compilation."""

import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("external_api", ROOT / "eng/check-server-tcp-api-contract.py")
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)


class LegacySizingBoundaryTests(unittest.TestCase):
    def test_legacy_class_removed_but_generated_interfaces_preserved(self):
        source = (ROOT / "src/SharpLink.Abstractions/IRpcSizedCodec.cs").read_text()
        self.assertNotIn("RpcGeneratedCodecSizing", source)
        self.assertIn("public interface IRpcSizedCodec<T>", source)
        self.assertIn("public interface IRpcSizedCodecSnapshot", source)
        for member in ("CanExactSize", "TryGetEncodedSize", "SerializeSized", "ReleaseSnapshot"):
            self.assertIn(member, source)
        baseline = (ROOT / "eng/public-api/3.0.0/SharpLink.Abstractions.api.txt").read_text()
        self.assertNotIn("class RpcGeneratedCodecSizing", baseline)
        self.assertIn("interface IRpcSizedCodec<T>", baseline)
        self.assertIn("interface IRpcSizedCodecSnapshot", baseline)
        historical = (ROOT / "eng/public-api/2.0.0/SharpLink.Abstractions.api.txt").read_text()
        self.assertIn("class RpcGeneratedCodecSizing", historical)

    def test_negative_guard_checks_all_three_members(self):
        result = subprocess.CompletedProcess([], 1,
            "Probe.cs(12,9): error CS0234: RpcGeneratedCodecSizing missing", "")
        with patch.object(GATE, "compile_project", return_value=result) as compile_probe:
            with contextlib.redirect_stdout(io.StringIO()):
                GATE.verify_removed_legacy_sizing(Path("unused"))
        self.assertEqual(compile_probe.call_count, 3)
        for call, member in zip(compile_probe.call_args_list, ("IsSuppressed", "Enter()", "Exit()")):
            self.assertIn(f"SharpLink.Abstractions.RpcGeneratedCodecSizing.{member};", call.args[0])

    def test_negative_guard_rejects_success_missing_or_unrelated_errors(self):
        for code, output in (
            (0, ""), (1, "build failed without diagnostic"),
            (1, "Probe.cs(12,9): error CS0246: RpcGeneratedCodecSizing missing"),
            (1, "Probe.cs(12,9): error CS0234: AnotherType missing"),
            (1, "Dependency.cs(12,9): error CS0234: RpcGeneratedCodecSizing missing"),
        ):
            with self.subTest(code=code, output=output):
                result = subprocess.CompletedProcess([], code, output, "")
                with patch.object(GATE, "compile_project", return_value=result):
                    with self.assertRaises(RuntimeError):
                        GATE.verify_removed_legacy_sizing(Path("unused"))


if __name__ == "__main__":
    unittest.main()
