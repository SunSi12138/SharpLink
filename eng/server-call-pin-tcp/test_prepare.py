"""Offline frozen-byte and compiled-identity mutation controls; no CLR invocation."""
import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import prepare
from common import ARMS, CONTRACT, ROOT, digest, inventory, write, verify_preparation_witness

class PreparationTests(unittest.TestCase):
    def test_source_commit_tree_cleanliness_all_fail_closed(self):
        for arm in ARMS:
            good=[CONTRACT[arm+'Source'],CONTRACT[arm+'Tree'],'']
            with patch('prepare.capture',side_effect=good):prepare.verify_source(Path('/synthetic'),arm)
            for wrong in [('0'*40,), (good[0],'0'*40), (good[0],good[1],' M src/file.cs')]:
                with patch('prepare.capture',side_effect=wrong),self.assertRaises(ValueError):
                    prepare.verify_source(Path('/synthetic'),arm)
        with self.assertRaises(ValueError):prepare.identity('unknown')

    def test_single_protocol_identity_properties(self):
        for arm in ARMS:
            self.assertEqual({'Arm':arm,'SourceTree':CONTRACT[arm+'Tree'],
                'ExpectedProductCommit':CONTRACT[arm+'Source']},prepare.identity(arm))
            self.assertEqual(['-p:Arm='+arm.upper(),'-p:SourceTree='+CONTRACT[arm+'Tree'],
                '-p:ProductCommit='+CONTRACT[arm+'Source']],prepare.identity_properties(arm))
        prepare.verify_identity_source(ROOT/'fixture')
        with tempfile.TemporaryDirectory() as td:
            root=Path(td)
            (root/'bad.props').write_text('<Project><PropertyGroup><SourceTree>'+CONTRACT['candidateTree']+'</SourceTree></PropertyGroup></Project>')
            with self.assertRaises(ValueError):prepare.verify_identity_source(root)

    def test_compiled_metadata_not_evaluated_claim_enforced(self):
        for arm in ARMS:
            expected=prepare.identity(arm)
            with patch('prepare.capture',return_value=json.dumps(expected)):
                self.assertEqual(expected,prepare.verify_embedded(Path('/not-loaded.dll'),arm,Path('/reader'),Path('/dotnet')))
            for key in expected:
                bad=dict(expected);bad[key]='wrong'
                with patch('prepare.capture',return_value=json.dumps(bad)),self.assertRaises(ValueError):
                    prepare.verify_embedded(Path('/not-loaded.dll'),arm,Path('/reader'),Path('/dotnet'))
            with patch('prepare.capture',return_value=json.dumps(dict(expected,Extra='value'))),self.assertRaises(ValueError):
                prepare.verify_embedded(Path('/not-loaded.dll'),arm,Path('/reader'),Path('/dotnet'))

    def test_writable_locations_exist_before_first_sdk_build(self):
        with tempfile.TemporaryDirectory() as td,patch.dict(os.environ,clear=True):
            prepare.build_environment(Path(td))
            for key in ('DOTNET_CLI_HOME','NUGET_HTTP_CACHE_PATH','NUGET_SCRATCH','XDG_CACHE_HOME','XDG_DATA_HOME'):
                self.assertTrue(Path(os.environ[key]).is_dir())

    def test_correctness_core_only_and_tcp_dualframework_domains(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);path=root/'PhysicalTcpProbe.runtimeconfig.json'
            core={'name':'Microsoft.NETCore.App','version':'10.0.2'}
            asp={'name':'Microsoft.AspNetCore.App','version':'10.0.2'}
            write(path,{'runtimeOptions':{'frameworks':[core,asp]}});prepare.pin_runtime(root)
            result=json.loads(path.read_text())['runtimeOptions']
            self.assertEqual('Disable',result['rollForward'])
            self.assertEqual(['10.0.14']*2,[f['version'] for f in result['frameworks']])
            for frameworks in ([core],[asp],[core,asp,core]):
                write(path,{'runtimeOptions':{'frameworks':frameworks}})
                with self.assertRaises(ValueError):prepare.pin_runtime(root)
            write(path,{'runtimeOptions':{'framework':core}})
            prepare.pin_runtime_config(path,('Microsoft.NETCore.App',))
            with self.assertRaises(ValueError):prepare.pin_runtime(root)

    def fixture(self, root):
        source=root/'source';unit=root/'unit';integration=root/'integration';output=root/'prepared'
        for folder in (source,unit,integration,output):folder.mkdir()
        for name in prepare.CLOSURE:
            for folder in (unit,integration):(folder/name).write_bytes(name.encode())
            if name.startswith('SharpLink.'):
                product=source/'src'/name[:-4]/'bin/Release/net10.0';product.mkdir(parents=True)
                (product/name).write_bytes(name.encode())
        generator=source/'src/SharpLink.Generator/bin/Release/netstandard2.0';generator.mkdir(parents=True)
        (generator/'SharpLink.Generator.dll').write_bytes(b'own-generator')
        graph={};libraries={};cache=root/'cache'
        for name in prepare.CLOSURE:
            product=name.startswith('SharpLink.');simple=name[:-4]
            key=simple+('/3.0.0' if product else '/10.0.12')
            asset=name if product else 'lib/net10.0/'+name
            graph[key]={'runtime':{asset:{}}};libraries[key]={'type':'project' if product else 'package'}
            if not product:
                package=cache/simple.lower()/'10.0.12'/asset;package.parent.mkdir(parents=True)
                package.write_bytes(name.encode())
        for folder,assembly in ((unit,'SharpLink.UnitTests'),(integration,'SharpLink.IntegrationTests')):
            write(folder/(assembly+'.deps.json'),{'runtimeTarget':{'name':'.NETCoreApp,Version=v10.0'},
                  'targets':{'.NETCoreApp,Version=v10.0':graph},'libraries':libraries})
            assets=source/'test'/assembly/'obj/project.assets.json';assets.parent.mkdir(parents=True)
            write(assets,{'packageFolders':{str(cache):{}}})
        return source,unit,integration,output

    def test_exact_common_bytes_freeze_and_cross_output_mutation_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            source,unit,integration,output=self.fixture(Path(td))
            frozen,manifest=prepare.freeze_closure('baseline',source,unit,integration,output)
            self.assertEqual(set(prepare.CLOSURE),set(manifest['closure']))
            self.assertEqual(9,len(manifest['closure']))
            self.assertTrue(all(digest(frozen/name)==sha for name,sha in manifest['closure'].items()))
            self.assertEqual(digest(frozen/'SharpLink.Generator.dll'),manifest['Sdk']['generatorSha256'])
            self.assertEqual(inventory(unit),manifest['correctness']['unitSnapshot'])
            (integration/'SharpLink.Client.dll').write_bytes(b'other-build')
            with self.assertRaises(ValueError):prepare.freeze_closure('candidate',source,unit,integration,output)

    def test_package12_dependency_graph_and_actual_copied_hashes_fail_closed(self):
        with tempfile.TemporaryDirectory() as td:
            source,unit,integration,_=self.fixture(Path(td))
            graph=prepare.verify_tested_dependency_graph(source,unit,integration)
            self.assertEqual(9,len(graph))
            dep=unit/'SharpLink.UnitTests.deps.json';original=json.loads(dep.read_text())
            target='.NETCoreApp,Version=v10.0';key='System.Threading.RateLimiting/10.0.12'
            for mutate in (lambda d:d['runtimeTarget'].update(name='wrong'),
                           lambda d:d['targets'][target].pop(key),
                           lambda d:d['targets'][target][key]['runtime'].update({'extra.dll':{}}),
                           lambda d:d['libraries'][key].update(type='project')):
                bad=copy.deepcopy(original);mutate(bad);write(dep,bad)
                with self.assertRaises(ValueError):prepare.verify_tested_dependency_graph(source,unit,integration)
                write(dep,original)
            assets=source/'test/SharpLink.UnitTests/obj/project.assets.json'
            original_assets=json.loads(assets.read_text())
            bad=copy.deepcopy(original_assets);bad['packageFolders']['/extra/cache']={};write(assets,bad)
            with self.assertRaises(ValueError):prepare.verify_tested_dependency_graph(source,unit,integration)
            write(assets,original_assets)
            (unit/'System.Threading.RateLimiting.dll').write_bytes(b'other-version')
            with self.assertRaises(ValueError):prepare.verify_tested_dependency_graph(source,unit,integration)

    def test_build_graph_once_no_pack_and_no_fixture_production_reference(self):
        with tempfile.TemporaryDirectory() as td,patch('prepare.run') as run,patch('prepare.pin_runtime_config'),patch('prepare.capture',return_value=json.dumps({'Properties':{'NuGetAudit':'true','NuGetAuditMode':'all','TreatWarningsAsErrors':'true'}})):
            root=Path(td)
            for name in ('SharpLink.UnitTests','SharpLink.IntegrationTests'):
                project=root/'test'/name/(name+'.csproj');project.parent.mkdir(parents=True)
                project.write_text('<Project />')
            prepare.build_correctness('candidate',root,Path('/sdk/dotnet'),root/'cache',root)
            run.assert_called_once()
            command=list(map(str,run.call_args.args[0]))
            self.assertEqual('build',command[1]);self.assertNotIn('pack',command)
            self.assertIn('-p:SourceRevisionId='+CONTRACT['candidateSource'],command)
            solution=(root/'candidate-correctness.slnx').read_text()
            self.assertIn('SharpLink.UnitTests.csproj',solution);self.assertIn('SharpLink.IntegrationTests.csproj',solution)
        for path in (ROOT/'fixture').rglob('*.csproj'):
            self.assertNotIn('../../../src',path.read_text())
        for path in ROOT.rglob('*'):
            if path.is_file() and path.suffix in ('.py','.props','.targets','.csproj') and path.name != 'test_prepare.py':
                self.assertNotIn('NuGetAudit=false',path.read_text(),str(path))

    def test_prepared_witness_binds_source_tree_metadata_and_tcp_bytes(self):
        from test_protocol import synthetic_unit_record
        from unit_gate.record import CLOSURE_NAMES
        manifest={'arms':{arm:{'snapshot':{name:'b'*64 for name in CLOSURE_NAMES}} for arm in ARMS}}
        synthetic_unit_record(manifest=manifest);verify_preparation_witness(manifest)
        for arm in ARMS:
            for key in ('commit','tree'):
                bad=copy.deepcopy(manifest);bad['arms'][arm]['source'][key]='0'*40
                with self.assertRaises(ValueError):verify_preparation_witness(bad)
            for kind in ('probe','module'):
                for key in ('Arm','SourceTree','ExpectedProductCommit'):
                    bad=copy.deepcopy(manifest);bad['arms'][arm]['fixtureMetadata'][kind][key]='wrong'
                    with self.assertRaises(ValueError):verify_preparation_witness(bad)
            bad=copy.deepcopy(manifest);bad['arms'][arm]['snapshot']['SharpLink.Client.dll']='c'*64
            with self.assertRaises(ValueError):verify_preparation_witness(bad)
        for arm in ARMS:
            for name in ('Client','Generator'):
                for key in ('assemblyName','mvid','informationalVersion'):
                    bad=copy.deepcopy(manifest);bad['arms'][arm]['productMetadata'][name][key]='wrong'
                    with self.assertRaises(ValueError):verify_preparation_witness(bad)
        bad=copy.deepcopy(manifest);bad['metadataPreflight']='failed'
        with self.assertRaises(ValueError):verify_preparation_witness(bad)

if __name__=='__main__':unittest.main()
