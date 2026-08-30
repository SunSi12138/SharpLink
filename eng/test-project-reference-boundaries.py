#!/usr/bin/env python3
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).with_name("check-project-reference-boundaries.py")
spec = importlib.util.spec_from_file_location("project_reference_guard", SCRIPT)
guard = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = guard
assert spec.loader is not None
spec.loader.exec_module(guard)


POLICY = """schema_version: 1
scope:
  production_root: src
  production_project_glob: "**/*.csproj"
  reference_kind: ProjectReference
  default: deny

projects:
  abstractions: src/Abstractions/Abstractions.csproj
  client: src/Client/Client.csproj
  server: src/Server/Server.csproj
  sdk: src/Sdk/Sdk.csproj
  generator: src/Generator/Generator.csproj

allowed_references:
  - from: client
    to: abstractions
    mode: assembly
  - from: server
    to: abstractions
    mode: assembly
  - from: sdk
    to: abstractions
    mode: assembly
  - from: sdk
    to: generator
    mode: analyzer

temporary_exceptions: []
"""


EMPTY_PROJECT = '<Project Sdk="Microsoft.NET.Sdk" />\n'


class ProjectReferenceBoundaryGuardTests(unittest.TestCase):
    def make_repo(self):
        temp = tempfile.TemporaryDirectory()
        root = Path(temp.name)
        (root / "doc").mkdir()
        (root / "doc" / "project-reference-boundaries.yml").write_text(POLICY, encoding="utf-8")
        for name in ("Abstractions", "Client", "Server", "Sdk", "Generator"):
            project_dir = root / "src" / name
            project_dir.mkdir(parents=True)
            (project_dir / f"{name}.csproj").write_text(EMPTY_PROJECT, encoding="utf-8")
        return temp, root

    def run_guard(self, root):
        return guard.run_guard(
            root,
            root / "doc" / "project-reference-boundaries.yml",
            evaluate_active=False,
        )

    def set_project(self, root, name, body):
        (root / "src" / name / f"{name}.csproj").write_text(body, encoding="utf-8")

    def assert_violation(self, result, message):
        self.assertTrue(result.violations)
        self.assertIn(message, "\n".join(result.violations))

    def test_current_like_graph_passes_including_conditioned_analyzer_edge(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, "Client", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="..\\Abstractions\\Abstractions.csproj" />
</ItemGroup></Project>""")
            self.set_project(root, "Server", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="../Abstractions/Abstractions.csproj" />
</ItemGroup></Project>""")
            self.set_project(root, "Sdk", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="../Abstractions/Abstractions.csproj" />
<ProjectReference Include="../Generator/Generator.csproj" Condition="'$(PublishAot)' != 'true'"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup></Project>""")
            result = self.run_guard(root)
            self.assertEqual((), result.violations)

    def test_unregistered_production_project_fails(self):
        temp, root = self.make_repo()
        with temp:
            extra = root / "src" / "Extra"
            extra.mkdir()
            (extra / "Extra.csproj").write_text(EMPTY_PROJECT, encoding="utf-8")
            result = self.run_guard(root)
            self.assert_violation(result, "unregistered production project: src/Extra/Extra.csproj")

    def test_condition_hidden_forbidden_edge_fails(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, "Client", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="../Server/Server.csproj" Condition="'$(SomeProperty)' == 'true'" />
</ItemGroup></Project>""")
            result = self.run_guard(root)
            self.assert_violation(result, "condition-hidden forbidden production edge client -> server")

    def test_conditioned_imported_forbidden_edge_fails(self):
        temp, root = self.make_repo()
        with temp:
            imported = root / "eng" / "architecture.props"
            imported.parent.mkdir()
            imported.write_text("""<Project><ItemGroup>
<ProjectReference Include="../Server/Server.csproj" Condition="'$(Never)' == 'true'" />
</ItemGroup></Project>""", encoding="utf-8")
            self.set_project(root, "Client", """<Project Sdk="Microsoft.NET.Sdk">
<Import Project="../../eng/architecture.props" Condition="'$(ImportArchitecture)' == 'true'" />
</Project>""")
            result = self.run_guard(root)
            self.assert_violation(result, "condition-hidden imported forbidden reference client -> server")

    def test_assembly_reference_output_assembly_false_fails(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, "Client", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="../Abstractions/Abstractions.csproj" ReferenceOutputAssembly="false" />
</ItemGroup></Project>""")
            result = self.run_guard(root)
            self.assert_violation(result, "assembly edge requires ReferenceOutputAssembly=true")

    def test_assembly_output_item_type_analyzer_fails(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, "Client", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="../Abstractions/Abstractions.csproj" OutputItemType="Analyzer" />
</ItemGroup></Project>""")
            result = self.run_guard(root)
            self.assert_violation(result, "assembly edge must not use OutputItemType=Analyzer")

    def test_property_driven_mode_metadata_fails(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, "Client", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="../Abstractions/Abstractions.csproj" ReferenceOutputAssembly="$(ReferenceAssembly)" />
</ItemGroup></Project>""")
            result = self.run_guard(root)
            self.assert_violation(result, "ReferenceOutputAssembly must be a literal value")

    def test_conditioned_child_mode_metadata_fails(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, "Client", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="../Abstractions/Abstractions.csproj">
  <ReferenceOutputAssembly Condition="'$(X)' == 'true'">true</ReferenceOutputAssembly>
</ProjectReference>
</ItemGroup></Project>""")
            result = self.run_guard(root)
            self.assert_violation(result, "ReferenceOutputAssembly must not have a Condition")

    def test_dynamic_project_reference_include_fails_closed(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, "Client", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="$(ServerProject)" />
</ItemGroup></Project>""")
            result = self.run_guard(root)
            self.assert_violation(result, "dynamic/unresolvable production ProjectReference Include is denied")

    def test_item_definition_mode_metadata_fails(self):
        temp, root = self.make_repo()
        with temp:
            (root / "Directory.Build.props").write_text("""<Project><ItemDefinitionGroup>
<ProjectReference><ReferenceOutputAssembly>false</ReferenceOutputAssembly></ProjectReference>
</ItemDefinitionGroup></Project>""", encoding="utf-8")
            result = self.run_guard(root)
            self.assert_violation(result, "ItemDefinitionGroup must not supply production ProjectReference mode metadata")

    def test_project_reference_update_mode_metadata_fails(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, "Client", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="../Abstractions/Abstractions.csproj" />
<ProjectReference Update="../Abstractions/Abstractions.csproj" OutputItemType="Analyzer" />
</ItemGroup></Project>""")
            result = self.run_guard(root)
            self.assert_violation(result, "ProjectReference Update must not supply/override mode metadata")

    def test_active_msbuild_forbidden_edge_is_checked(self):
        temp, root = self.make_repo()
        with temp:
            def fake_run(command, **kwargs):
                project = Path(command[2]).name
                items = []
                if project == "Client.csproj":
                    items = [{"Identity": "../Server/Server.csproj"}]
                payload = json.dumps({"Items": {"ProjectReference": items}})
                return subprocess.CompletedProcess(command, 0, payload, "")

            with mock.patch.object(guard.subprocess, "run", side_effect=fake_run):
                result = guard.run_guard(root, root / "doc" / "project-reference-boundaries.yml")
            self.assert_violation(result, "active MSBuild forbidden edge client -> server")

    def test_active_msbuild_mode_mismatch_is_checked(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, "Client", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="../Abstractions/Abstractions.csproj" />
</ItemGroup></Project>""")

            def fake_run(command, **kwargs):
                project = Path(command[2]).name
                items = []
                if project == "Client.csproj":
                    items = [{
                        "Identity": "../Abstractions/Abstractions.csproj",
                        "ReferenceOutputAssembly": "false",
                    }]
                payload = json.dumps({"Items": {"ProjectReference": items}})
                return subprocess.CompletedProcess(command, 0, payload, "")

            with mock.patch.object(guard.subprocess, "run", side_effect=fake_run):
                result = guard.run_guard(root, root / "doc" / "project-reference-boundaries.yml")
            self.assert_violation(result, "active MSBuild reference-mode violation")


if __name__ == "__main__":
    unittest.main()
