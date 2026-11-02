"""Source-only calibration/consumer-binding controls; no compiler or SDK execution."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("release_version_guard", ROOT / "eng/verify-release-versions.py")
GUARD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GUARD)
AUTHORITY = "SharpLink.Abstractions.SharpLinkGeneratedManifestVersions"
EMITTER = "src/SharpLink.Generator/RpcGenerator.ManifestEmitter.cs"
BOOTSTRAP = "src/SharpLink.Generator/RpcGenerator.ReferencedManifestBootstrap.cs"
PROJECT = "src/SharpLink.Generator/SharpLink.Generator.csproj"
VERSIONS = "src/SharpLink.Abstractions/SharpLinkGeneratedManifestVersions.cs"


class ReleaseVersionBindingTests(unittest.TestCase):
    def test_current_calibration_and_actual_consumers(self):
        manifest, observed = GUARD.verify(ROOT)
        self.assertEqual(len(observed), len(manifest["versions"]))

    def test_source_mutations_cannot_bypass_shared_authority(self):
        mutations = (
            (EMITTER, f"public int ApiVersion => {{{AUTHORITY}.Api}};", "public int ApiVersion => 6;"),
            (EMITTER, f"public int ProtocolVersion => {{{AUTHORITY}.Protocol}};", "public int ProtocolVersion => 2;"),
            (EMITTER, f"typeof(SharpLink.Generated.{{manifestTypeName}}), {{{AUTHORITY}.Api}},", "typeof(SharpLink.Generated.{manifestTypeName}), 6,"),
            (EMITTER, f"private const string GeneratedAbiIdentity = {AUTHORITY}.AbiIdentity;", 'private const string GeneratedAbiIdentity = "wrong";'),
            (EMITTER, '\\"{GeneratedAbiIdentity}\\")]', '\\"wrong\\")]'),
            (BOOTSTRAP, f"attribute.ConstructorArguments[1].Value is not {AUTHORITY}.Api", "attribute.ConstructorArguments[1].Value is not 6"),
            (BOOTSTRAP, f"attribute.ConstructorArguments[2].Value is not {AUTHORITY}.Protocol", "attribute.ConstructorArguments[2].Value is not 2"),
            (BOOTSTRAP, "!string.Equals(abiIdentity, GeneratedAbiIdentity, StringComparison.Ordinal)", "!string.Equals(abiIdentity, GeneratedAbiIdentity, StringComparison.OrdinalIgnoreCase)"),
            (PROJECT, "..\\SharpLink.Abstractions\\SharpLinkGeneratedManifestVersions.cs", "..\\SharpLink.Shared\\WrongVersions.cs"),
            (PROJECT, "$(DefineConstants);SHARPLINK_GENERATOR", "$(DefineConstants);WRONG_SYMBOL"),
            (VERSIONS, "#if SHARPLINK_GENERATOR\ninternal", "#if SHARPLINK_GENERATOR\npublic"),
            (VERSIONS, "public const int Api = 5;", "public const int Api = 6;"),
        )
        manifest = json.loads((ROOT / "eng/release-versions.json").read_text())
        paths = {entry["path"] for entry in manifest["versions"]}
        paths.update((EMITTER, BOOTSTRAP, PROJECT, VERSIONS,
                      "Directory.Build.props", "eng/release-versions.json"))
        for path, before, after in mutations:
            with self.subTest(path=path, replacement=after), tempfile.TemporaryDirectory(prefix="sharplink-version-mutant-") as directory:
                target = Path(directory)
                for relative in paths:
                    destination = target / relative
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    destination.write_bytes((ROOT / relative).read_bytes())
                text = (target / path).read_text()
                self.assertEqual(text.count(before), 1, "mutant must target exactly one actual source occurrence")
                (target / path).write_text(text.replace(before, after))
                with self.assertRaises(ValueError):
                    GUARD.verify(target)


if __name__ == "__main__":
    unittest.main()
