#!/usr/bin/env python3
import importlib.util
import tempfile
import unittest
import sys
from pathlib import Path

SCRIPT = Path(__file__).with_name("check-project-reference-boundaries.py")
spec = importlib.util.spec_from_file_location("project_reference_guard", SCRIPT)
guard = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = guard
assert spec.loader is not None
spec.loader.exec_module(guard)

POLICY = '''schema_version: 1
scope:
  production_root: src
  production_project_glob: "**/*.csproj"
projects:
  abstractions: src/Abstractions/Abstractions.csproj
  sdk: src/Sdk/Sdk.csproj
  generator: src/Generator/Generator.csproj
allowed_references:
  - from: sdk
    to: abstractions
    mode: assembly
  - from: sdk
    to: generator
    mode: analyzer
temporary_exceptions: []
'''

ANALYZER_REF = '''<ProjectReference Include="../Generator/Generator.csproj"
    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />'''


class MetadataFilterTests(unittest.TestCase):
    def make_repo(self):
        temp = tempfile.TemporaryDirectory()
        root = Path(temp.name)
        (root / "doc").mkdir()
        (root / "doc/project-reference-boundaries.yml").write_text(POLICY, encoding="utf-8")
        for name in ("Abstractions", "Sdk", "Generator"):
            path = root / "src" / name
            path.mkdir(parents=True)
            (path / f"{name}.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk" />', encoding="utf-8")
        return temp, root

    def run_guard(self, root):
        return guard.run_guard(root, root / "doc/project-reference-boundaries.yml", evaluate_active=False)

    def assert_violation(self, result, text):
        joined = "\n".join(result.violations)
        self.assertIn(text, joined, joined)

    def set_sdk(self, root, body):
        (root / "src/Sdk/Sdk.csproj").write_text(body, encoding="utf-8")

    def test_target_mutation_remove_metadata_mode_field_fails(self):
        temp, root = self.make_repo()
        with temp:
            self.set_sdk(root, f'''<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{ANALYZER_REF}</ItemGroup>
<Target Name="X"><ItemGroup><ProjectReference RemoveMetadata="OutputItemType" /></ItemGroup></Target></Project>''')
            self.assert_violation(self.run_guard(root), "RemoveMetadata must not remove ProjectReference mode metadata OutputItemType")

    def test_target_mutation_keep_metadata_must_preserve_both_mode_fields(self):
        temp, root = self.make_repo()
        with temp:
            self.set_sdk(root, f'''<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{ANALYZER_REF}</ItemGroup>
<Target Name="X"><ItemGroup><ProjectReference KeepMetadata="SomeOtherMetadata" /></ItemGroup></Target></Project>''')
            self.assert_violation(self.run_guard(root), "KeepMetadata must preserve ProjectReference mode metadata ReferenceOutputAssembly, OutputItemType")

    def test_update_remove_metadata_mode_field_fails_even_when_conditioned(self):
        temp, root = self.make_repo()
        with temp:
            self.set_sdk(root, f'''<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{ANALYZER_REF}
<ProjectReference Update="../Generator/Generator.csproj" RemoveMetadata="ReferenceOutputAssembly" Condition="'$(Never)' == 'true'" />
</ItemGroup></Project>''')
            self.assert_violation(self.run_guard(root), "RemoveMetadata must not remove ProjectReference mode metadata ReferenceOutputAssembly")

    def test_update_keep_metadata_missing_mode_field_fails(self):
        temp, root = self.make_repo()
        with temp:
            self.set_sdk(root, f'''<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{ANALYZER_REF}
<ProjectReference Update="../Generator/Generator.csproj" KeepMetadata="OutputItemType" Condition="'$(Never)' == 'true'" />
</ItemGroup></Project>''')
            self.assert_violation(self.run_guard(root), "KeepMetadata must preserve ProjectReference mode metadata ReferenceOutputAssembly")

    def test_dynamic_remove_metadata_fails_closed(self):
        temp, root = self.make_repo()
        with temp:
            self.set_sdk(root, f'''<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{ANALYZER_REF}
<ProjectReference Update="../Generator/Generator.csproj" RemoveMetadata="$(ModeMetadata)" />
</ItemGroup></Project>''')
            self.assert_violation(self.run_guard(root), "dynamic RemoveMetadata is denied")

    def test_keep_metadata_preserving_both_mode_fields_is_allowed(self):
        temp, root = self.make_repo()
        with temp:
            self.set_sdk(root, f'''<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{ANALYZER_REF}
<ProjectReference Update="../Generator/Generator.csproj" KeepMetadata="ReferenceOutputAssembly;OutputItemType" />
</ItemGroup></Project>''')
            self.assertEqual((), self.run_guard(root).violations)


if __name__ == "__main__":
    unittest.main()
