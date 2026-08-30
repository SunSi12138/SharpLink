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

POLICY = '''schema_version: 1
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
'''
EMPTY_PROJECT = '<Project Sdk="Microsoft.NET.Sdk" />\n'
FORBIDDEN_REF = '<ProjectReference Include="../Server/Server.csproj" Condition="\'$(Never)\' == \'true\'" />'


class GuardTests(unittest.TestCase):
    def make_repo(self, relative_root=None):
        temp = tempfile.TemporaryDirectory()
        root = Path(temp.name)
        if relative_root is not None:
            root = root / relative_root
        (root / 'doc').mkdir(parents=True)
        (root / 'doc/project-reference-boundaries.yml').write_text(POLICY, encoding='utf-8')
        for name in ('Abstractions', 'Client', 'Server', 'Sdk', 'Generator'):
            d = root / 'src' / name
            d.mkdir(parents=True)
            (d / f'{name}.csproj').write_text(EMPTY_PROJECT, encoding='utf-8')
        return temp, root

    def set_project(self, root, name, body):
        (root / 'src' / name / f'{name}.csproj').write_text(body, encoding='utf-8')

    def run_guard(self, root, active=False):
        return guard.run_guard(root, root / 'doc/project-reference-boundaries.yml', evaluate_active=active)

    def assert_violation(self, result, text):
        joined = '\n'.join(result.violations)
        self.assertIn(text, joined, joined)

    def write_forbidden(self, path, include='../Server/Server.csproj'):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(f'''<Project><ItemGroup>
<ProjectReference Include="{include}" Condition="'$(Never)' == 'true'" />
</ItemGroup></Project>''', encoding='utf-8')

    def test_current_like_graph_passes(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Abstractions/Abstractions.csproj" /></ItemGroup></Project>')
            self.set_project(root, 'Server', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Abstractions/Abstractions.csproj" /></ItemGroup></Project>')
            self.set_project(root, 'Sdk', '''<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="../Abstractions/Abstractions.csproj" />
<ProjectReference Include="../Generator/Generator.csproj" Condition="'$(PublishAot)' != 'true'" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup></Project>''')
            self.assertEqual((), self.run_guard(root).violations)

    def test_unregistered_project(self):
        temp, root = self.make_repo()
        with temp:
            p = root / 'src/Extra/Extra.csproj'; p.parent.mkdir(); p.write_text(EMPTY_PROJECT)
            self.assert_violation(self.run_guard(root), 'unregistered production project: src/Extra/Extra.csproj')

    def test_condition_hidden_forbidden(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', f'<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{FORBIDDEN_REF}</ItemGroup></Project>')
            self.assert_violation(self.run_guard(root), 'condition-hidden forbidden production edge client -> server')

    def test_lowercase_projectreference(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><projectreference Include="../Server/Server.csproj" Condition="\'$(Never)\' == \'true\'" /></ItemGroup></Project>')
            self.assert_violation(self.run_guard(root), 'condition-hidden forbidden production edge client -> server')

    def test_conditioned_import_provenance_marks_hidden(self):
        temp, root = self.make_repo()
        with temp:
            imported = root / 'eng/architecture.props'; self.write_forbidden(imported)
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><Import Project="../../eng/architecture.props" Condition="\'$(X)\' == \'true\'" /></Project>')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_unknown_import_property_fails_closed(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><Import Project="$(VSToolsPath)architecture.props" Condition="\'$(X)\' == \'true\'" /></Project>')
            self.assert_violation(self.run_guard(root), 'repository import is not statically traversable')

    def test_directory_packages_default_scanned(self):
        temp, root = self.make_repo()
        with temp:
            self.write_forbidden(root / 'Directory.Packages.props')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_directory_packages_override_from_directory_build_props(self):
        temp, root = self.make_repo()
        with temp:
            (root / 'Directory.Build.props').write_text('<Project><PropertyGroup><DirectoryPackagesPropsPath>eng/custom-packages.props</DirectoryPackagesPropsPath></PropertyGroup></Project>')
            self.write_forbidden(root / 'eng/custom-packages.props')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_late_project_body_package_override_does_not_suppress_default(self):
        temp, root = self.make_repo()
        with temp:
            self.write_forbidden(root / 'Directory.Packages.props')
            (root / 'eng/safe.props').parent.mkdir(parents=True, exist_ok=True)
            (root / 'eng/safe.props').write_text('<Project />')
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DirectoryPackagesPropsPath>../../eng/safe.props</DirectoryPackagesPropsPath></PropertyGroup></Project>')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_custom_after_directory_build_props_scanned(self):
        temp, root = self.make_repo()
        with temp:
            (root / 'Directory.Build.props').write_text('<Project><PropertyGroup><CustomAfterDirectoryBuildProps>eng/after.props</CustomAfterDirectoryBuildProps></PropertyGroup></Project>')
            self.write_forbidden(root / 'eng/after.props')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_custom_after_props_can_override_packages(self):
        temp, root = self.make_repo()
        with temp:
            (root / 'Directory.Build.props').write_text('<Project><PropertyGroup><CustomAfterDirectoryBuildProps>eng/after.props</CustomAfterDirectoryBuildProps></PropertyGroup></Project>')
            (root / 'eng').mkdir(exist_ok=True)
            (root / 'eng/after.props').write_text('<Project><PropertyGroup><DirectoryPackagesPropsPath>custom-packages.props</DirectoryPackagesPropsPath></PropertyGroup></Project>')
            self.write_forbidden(root / 'eng/custom-packages.props')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_directory_build_targets_override(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DirectoryBuildTargetsPath>../../eng/architecture.targets</DirectoryBuildTargetsPath></PropertyGroup></Project>')
            self.write_forbidden(root / 'eng/architecture.targets')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_conditioned_imported_targets_override_keeps_default(self):
        temp, root = self.make_repo()
        with temp:
            self.write_forbidden(root / 'Directory.Build.targets')
            (root / 'eng').mkdir(exist_ok=True)
            (root / 'eng/override.props').write_text('<Project><PropertyGroup><DirectoryBuildTargetsPath>architecture.targets</DirectoryBuildTargetsPath></PropertyGroup></Project>')
            (root / 'eng/architecture.targets').write_text('<Project />')
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><Import Project="../../eng/override.props" Condition="\'$(UseCustom)\' == \'true\'" /></Project>')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_choose_targets_override_keeps_default(self):
        temp, root = self.make_repo()
        with temp:
            self.write_forbidden(root / 'Directory.Build.targets')
            (root / 'eng').mkdir(exist_ok=True)
            (root / 'eng/architecture.targets').write_text('<Project />')
            self.set_project(root, 'Client', '''<Project Sdk="Microsoft.NET.Sdk"><Choose><When Condition="'$(UseCustom)' == 'true'"><PropertyGroup>
<DirectoryBuildTargetsPath>../../eng/architecture.targets</DirectoryBuildTargetsPath>
</PropertyGroup></When></Choose></Project>''')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_custom_before_directory_build_targets(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><CustomBeforeDirectoryBuildTargets>../../eng/before.targets</CustomBeforeDirectoryBuildTargets></PropertyGroup></Project>')
            self.write_forbidden(root / 'eng/before.targets')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_custom_after_directory_build_targets_from_project(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><CustomAfterDirectoryBuildTargets>../../eng/after.targets</CustomAfterDirectoryBuildTargets></PropertyGroup></Project>')
            self.write_forbidden(root / 'eng/after.targets')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_custom_after_directory_build_targets_from_targets_file(self):
        temp, root = self.make_repo()
        with temp:
            (root / 'Directory.Build.targets').write_text('<Project><PropertyGroup><CustomAfterDirectoryBuildTargets>eng/after.targets</CustomAfterDirectoryBuildTargets></PropertyGroup></Project>')
            self.write_forbidden(root / 'eng/after.targets')
            self.assert_violation(self.run_guard(root), 'condition-hidden imported forbidden reference client -> server')

    def test_dynamic_auto_override_fails_closed(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><DirectoryBuildTargetsPath>$(SomePath)</DirectoryBuildTargetsPath></PropertyGroup></Project>')
            self.assert_violation(self.run_guard(root), 'repository import is not statically traversable')

    def test_target_mode_mutation(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Abstractions/Abstractions.csproj" /></ItemGroup><Target Name="X"><ItemGroup><ProjectReference ReferenceOutputAssembly="false" /></ItemGroup></Target></Project>')
            self.assert_violation(self.run_guard(root), 'ProjectReference target mutation must not supply/override mode metadata')

    def test_item_definition_mode(self):
        temp, root = self.make_repo()
        with temp:
            (root / 'Directory.Build.props').write_text('<Project><ItemDefinitionGroup><ProjectReference><ReferenceOutputAssembly>false</ReferenceOutputAssembly></ProjectReference></ItemDefinitionGroup></Project>')
            self.assert_violation(self.run_guard(root), 'ItemDefinitionGroup must not supply production ProjectReference mode metadata')

    def test_update_mode(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Abstractions/Abstractions.csproj" /><ProjectReference Update="../Abstractions/Abstractions.csproj" OutputItemType="Analyzer" /></ItemGroup></Project>')
            self.assert_violation(self.run_guard(root), 'ProjectReference Update must not supply/override mode metadata')

    def test_dynamic_project_reference(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="$(ServerProject)" /></ItemGroup></Project>')
            self.assert_violation(self.run_guard(root), 'dynamic/unresolvable production ProjectReference Include is denied')

    def test_assembly_reference_output_false(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Abstractions/Abstractions.csproj" ReferenceOutputAssembly="false" /></ItemGroup></Project>')
            self.assert_violation(self.run_guard(root), 'assembly edge requires ReferenceOutputAssembly=true')

    def test_assembly_output_analyzer(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Abstractions/Abstractions.csproj" OutputItemType="Analyzer" /></ItemGroup></Project>')
            self.assert_violation(self.run_guard(root), 'assembly edge must not use OutputItemType=Analyzer')

    def test_property_driven_mode(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Abstractions/Abstractions.csproj" ReferenceOutputAssembly="$(X)" /></ItemGroup></Project>')
            self.assert_violation(self.run_guard(root), 'ReferenceOutputAssembly must be a literal value')

    def test_conditioned_child_mode(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Abstractions/Abstractions.csproj"><ReferenceOutputAssembly Condition="\'$(X)\' == \'true\'">true</ReferenceOutputAssembly></ProjectReference></ItemGroup></Project>')
            self.assert_violation(self.run_guard(root), 'ReferenceOutputAssembly must not have a Condition')

    def test_checkout_path_obj(self):
        temp, root = self.make_repo(Path('obj') / 'SharpLink')
        with temp:
            self.assertEqual((), self.run_guard(root).violations)

    def test_source_obj_project_still_in_scope(self):
        temp, root = self.make_repo()
        with temp:
            p = root / 'src/Client/obj/Shadow.csproj'; p.parent.mkdir(); p.write_text(EMPTY_PROJECT)
            self.assert_violation(self.run_guard(root), 'unregistered production project: src/Client/obj/Shadow.csproj')

    def test_active_forbidden(self):
        temp, root = self.make_repo()
        with temp:
            def fake(command, **kwargs):
                items = [{'Identity':'../Server/Server.csproj'}] if Path(command[2]).name == 'Client.csproj' else []
                return subprocess.CompletedProcess(command, 0, json.dumps({'Items':{'ProjectReference':items}}), '')
            with mock.patch.object(guard.subprocess, 'run', side_effect=fake):
                result = self.run_guard(root, active=True)
            self.assert_violation(result, 'active MSBuild forbidden edge client -> server')

    def test_active_mode_mismatch(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Abstractions/Abstractions.csproj" /></ItemGroup></Project>')
            def fake(command, **kwargs):
                items = [{'Identity':'../Abstractions/Abstractions.csproj','ReferenceOutputAssembly':'false'}] if Path(command[2]).name == 'Client.csproj' else []
                return subprocess.CompletedProcess(command, 0, json.dumps({'Items':{'ProjectReference':items}}), '')
            with mock.patch.object(guard.subprocess, 'run', side_effect=fake):
                result = self.run_guard(root, active=True)
            self.assert_violation(result, 'active MSBuild reference-mode violation')

    def test_active_empty_roa_invalid(self):
        temp, root = self.make_repo()
        with temp:
            self.set_project(root, 'Client', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Abstractions/Abstractions.csproj" /></ItemGroup></Project>')
            def fake(command, **kwargs):
                items = [{'Identity':'../Abstractions/Abstractions.csproj','ReferenceOutputAssembly':''}] if Path(command[2]).name == 'Client.csproj' else []
                return subprocess.CompletedProcess(command, 0, json.dumps({'Items':{'ProjectReference':items}}), '')
            with mock.patch.object(guard.subprocess, 'run', side_effect=fake):
                result = self.run_guard(root, active=True)
            self.assert_violation(result, 'active ReferenceOutputAssembly is not boolean')


if __name__ == '__main__':
    unittest.main()
