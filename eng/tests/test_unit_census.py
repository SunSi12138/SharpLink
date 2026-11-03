"""Python-only census controls; no local SDK and no C# test bodies."""
import contextlib
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[2]
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);obj=importlib.util.module_from_spec(spec);spec.loader.exec_module(obj);return obj
r=load('census_test_results',ROOT/'eng/unit-census-results.py')
runner=load('census_test_runner',ROOT/'eng/run-unit-census.py')

def fixture():
    method={'source':'test/SharpLink.UnitTests/K.cs','namespace':'Known','className':'K','methodName':'M','arity':0,
            'parameters':['System.Boolean','System.Threading.CancellationToken'],'return':'System.Threading.Tasks.Task',
            'attributeLine':4,'signatureLine':7,'endLine':10,'configurations':['Release','Debug'],'initialEligibility':'ordinary','expectedCases':2}
    catalog={'methods':[method],'assemblyFullName':'SharpLink.UnitTests, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null'}
    row={'uid':'actual0','displayName':'same display','type':{'assemblyFullName':catalog['assemblyFullName'],'namespace':'Known','typeName':'Known.K','methodName':'M','methodArity':0,'parameterTypeFullNames':method['parameters'],'returnTypeFullName':method['return']},'location':{'file':'/_/'+method['source'],'lineStart':4,'lineEnd':10},'traits':[{'key':'X','value':'a'},{'key':'X','value':'a'}]}
    second=copy.deepcopy(row);second['uid']='actual1'
    return catalog,{'schemaVersion':1,'tests':[row,second]}

class Controls(unittest.TestCase):
    def reject(self,fn):
        with self.assertRaises((r.Rejected,ValueError,KeyError,TypeError)):fn()
    def test_assembly_metadata_items_and_imports(self):
        proof=r.assembly_metadata_proof();c={'assemblyMetadataProof':proof}
        packages=Path('/synthetic/packages');sdk=Path('/synthetic/sdk')
        imports={packages/proof['packageTarget']:proof['packageTargetSHA256'],sdk/proof['sdkTarget']:proof['sdkTargetSHA256']}
        row={'Identity':'Microsoft.Testing.Platform.Application','Value':'true'}
        good={'Items':{'AssemblyMetadata':[row]}}
        with patch.object(r,'file_identity',side_effect=lambda p:[imports[p]]):
            r.assembly_metadata_qualification(packages,sdk,good,imports,c)
            for rows in ([],[row,row],[dict(row,Value='false')],[dict(row,Identity='Foreign')]):
                self.reject(lambda:r.assembly_metadata_qualification(packages,sdk,{'Items':{'AssemblyMetadata':rows}},imports,c))
            for path in imports:
                subset=dict(imports);del subset[path]
                self.reject(lambda:r.assembly_metadata_qualification(packages,sdk,good,subset,c))
        with patch.object(r,'file_identity',return_value=['wrong']):
            self.reject(lambda:r.assembly_metadata_qualification(packages,sdk,good,imports,c))
        self.assertEqual(r.read_json(ROOT/'eng/unit-census.json')['assemblyMetadataProof'],proof)

    def using_fixture(self):
        return {'Items':{'Using':[{'Identity':name,'Static':'True' if static else ''} for name,static in
            [('TUnit.Assertions',False),('TUnit.Assertions.Extensions',False),('TUnit.Core',False),('TUnit.Core.HookType',True)]]}}
    def test_global_using_exact_items(self):
        good=self.using_fixture();r.global_using_rows(good)
        for mode in ('missing','extra','duplicate','alias','static'):
            value=copy.deepcopy(good);rows=value['Items']['Using']
            if mode=='missing':rows.pop()
            elif mode=='extra':rows.append({'Identity':'Foreign'})
            elif mode=='duplicate':rows.append(rows[0])
            elif mode=='alias':rows[0]['Alias']='Foreign'
            else:rows[0]['Static']='True'
            self.reject(lambda:r.global_using_rows(value))
    def test_global_using_source_and_import_guards(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'src';packages=Path(d)/'packages';sdk=Path(d)/'sdk'
            proof=r.global_using_proof();c={'globalUsingProof':proof};value=self.using_fixture()
            source=root/r.GLOBAL_USINGS_PATH;source.parent.mkdir(parents=True);source.write_text(r.GLOBAL_USINGS_TEXT)
            value['Items']['Compile']=[{'FullPath':str(source)}]
            imports={sdk/proof['sdkTarget']:proof['sdkTargetSHA256']}
            imports.update({packages/name:sha for name,sha in proof['packageFiles'].items()})
            with patch.object(r,'file_identity',side_effect=lambda p:[imports[p]]):
                r.global_using_qualification(root,packages,sdk,value,imports,c)
                for path in imports:
                    subset=dict(imports);del subset[path]
                    self.reject(lambda:r.global_using_qualification(root,packages,sdk,value,subset,c))
                for text in (r.GLOBAL_USINGS_TEXT+'class Foreign {}',r.GLOBAL_USINGS_TEXT.replace('TUnit.Core;','Foreign;')):
                    source.write_text(text);self.reject(lambda:r.global_using_qualification(root,packages,sdk,value,imports,c))
                source.write_text(r.GLOBAL_USINGS_TEXT);value['Items']['Compile'].append(value['Items']['Compile'][0])
                self.reject(lambda:r.global_using_qualification(root,packages,sdk,value,imports,c))
    def test_global_using_catalog_and_diagnostic_known_names(self):
        c=r.read_json(ROOT/'eng/unit-census.json');self.assertEqual(c['globalUsingProof'],r.global_using_proof())
        facts=r.metadata_facts(ROOT,c['project'],{'Items':{'Compile':[{'FullPath':str(ROOT/r.GLOBAL_USINGS_PATH)},{'FullPath':'/PRIVATE_SENTINEL'}]}},c)
        self.assertTrue(facts['Compile']['knownGeneratedPresent'][r.GLOBAL_USINGS_PATH]);self.assertEqual(facts['Compile']['unknownExtraCount'],1)
        self.assertNotIn('PRIVATE_SENTINEL',json.dumps(facts))

    def test_catalog_exact_package_compile_proof(self):
        self.assertEqual(r.read_json(ROOT/'eng/unit-census.json')['packageCompileProof'],r.package_compile_proof())
    def package_source_fixture(self, root):
        from zipfile import ZipFile
        import hashlib
        packages=root/'packages';folder=packages/'tunit.core/1.73.19'
        source=folder/r.TUNIT_CORE_SOURCE_MEMBER;props=folder/r.TUNIT_CORE_PROPS_MEMBER
        source.parent.mkdir(parents=True);source.write_bytes(b'namespace TUnit.Generated {}')
        props.write_bytes(b'<Project/>');archive=folder/'tunit.core.1.73.19.nupkg'
        with ZipFile(archive,'w') as zipped:
            zipped.writestr(r.TUNIT_CORE_SOURCE_MEMBER,source.read_bytes())
            zipped.writestr(r.TUNIT_CORE_PROPS_MEMBER,props.read_bytes())
        hashes={'TUNIT_CORE_ARCHIVE_SHA256':hashlib.sha256(archive.read_bytes()).hexdigest(),
                'TUNIT_CORE_PROPS_SHA256':hashlib.sha256(props.read_bytes()).hexdigest(),
                'TUNIT_CORE_NAMESPACE_SHA256':hashlib.sha256(source.read_bytes()).hexdigest()}
        metadata={'Items':{'Compile':[{'FullPath':str(source)},{'FullPath':str(root/'src/K.cs')}]}}
        return packages,source,props,archive,hashes,metadata
    def test_exact_package_source_and_import(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);packages,source,props,archive,hashes,meta=self.package_source_fixture(root)
            with patch.multiple(r,**hashes):
                allowed=r.package_compile_inputs(root/'src',packages,meta,{props:None})
                self.assertEqual(allowed,{source:'package:tunit.core/1.73.19/'+r.TUNIT_CORE_SOURCE_MEMBER})
                self.reject(lambda:r.package_compile_inputs(root/'src',packages,meta,{}))
    def test_package_source_wrong_missing_duplicate_or_extra(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);packages,source,props,archive,hashes,meta=self.package_source_fixture(root)
            cases=[[],meta['Items']['Compile'][1:],list(reversed(meta['Items']['Compile'])),
                   meta['Items']['Compile']+[{'FullPath':str(source)}],
                   meta['Items']['Compile']+[{'FullPath':str(packages/'foreign.cs')}],
                   [{'FullPath':str(source).replace('/net10.0/','/net8.0/')},meta['Items']['Compile'][1]]]
            with patch.multiple(r,**hashes):
                for rows in cases:self.reject(lambda:r.package_compile_inputs(root/'src',packages,{'Items':{'Compile':rows}}))
    def test_package_source_archive_props_and_source_tampering(self):
        for which in ('source','props','archive'):
            with tempfile.TemporaryDirectory() as d:
                root=Path(d);packages,source,props,archive,hashes,meta=self.package_source_fixture(root)
                {'source':source,'props':props,'archive':archive}[which].write_bytes(b'changed')
                with patch.multiple(r,**hashes):self.reject(lambda:r.package_compile_inputs(root/'src',packages,meta))
    def test_package_source_symlink_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);packages,source,props,archive,hashes,meta=self.package_source_fixture(root)
            target=root/'target';source.rename(target);source.symlink_to(target)
            with patch.multiple(r,**hashes):self.reject(lambda:r.package_compile_inputs(root/'src',packages,meta))

    def test_params_token_repeat_display(self):
        c,d=fixture();a=r.classify(d,c);self.assertEqual(a['summary']['observed'],2);self.assertTrue(r.stable(a,r.classify(d,c)))
        self.assertEqual(a['rows'][0]['native']['traits'],d['tests'][0]['traits'])
    def test_duplicate_missing_uid(self):
        for change in ('duplicate','absent','empty'):
            c,d=fixture()
            if change=='duplicate':d['tests'][1]['uid']='actual0'
            elif change=='absent':del d['tests'][0]['uid']
            else:d['tests'][0]['uid']=''
            self.reject(lambda:r.classify(d,c))
    def test_foreign_signature_span(self):
        for field,value in [('assemblyFullName','foreign'),('namespace','Other'),('methodName','Other'),('parameterTypeFullNames',['System.Boolean']),('methodArity',True)]:
            c,d=fixture();d['tests'][0]['type'][field]=value;self.reject(lambda:r.classify(d,c))
        for file in ('/tmp/K.cs','/_/../K.cs','/_/test//K.cs','/_/unknown.cs'):
            c,d=fixture();d['tests'][0]['location']['file']=file;self.reject(lambda:r.classify(d,c))
        c,d=fixture();d['tests'][0]['location']['lineEnd']=11;self.reject(lambda:r.classify(d,c))
    def test_missing_method_cardinality(self):
        c,d=fixture();d['tests']=[];self.reject(lambda:r.classify(d,c))
        c,d=fixture();d['tests'].pop();self.reject(lambda:r.classify(d,c))
    def test_explicit_is_initial_only(self):
        c,d=fixture();c['methods'][0]['initialEligibility']='explicit';s=r.classify(d,c)['summary']
        self.assertEqual(s['sourceExplicit'],2);self.assertEqual(s['sourceInitialOrdinarySelectionEligible'],0)
        self.assertEqual(s['runtimeSkipped'],'notObserved');self.assertEqual(s['executionDependencyClosure'],'notObserved')
    def test_unknown_not_qualified(self):
        for key,value in [('expectedCases',None),('initialEligibility','unknown')]:
            c,d=fixture();c['methods'][0][key]=value;a=r.classify(d,c);self.reject(lambda:r.stable(a,a))
    def test_debug_not_release(self):
        c,d=fixture();c['methods'][0]['configurations']=['Debug'];self.reject(lambda:r.classify(d,c))
    def test_second_metadata_drift(self):
        c,d=fixture();a=r.classify(d,c);d['tests'][1]['displayName']='drift';self.reject(lambda:r.stable(a,r.classify(d,c)))
    def test_duplicate_json_and_trailing(self):
        with tempfile.TemporaryDirectory() as tmp:
            p=Path(tmp)/'x'
            for text in ('{"a":1,"a":2}','{}\nnoise','{"x":NaN}','{'):
                p.write_text(text);self.reject(lambda:r.read_json(p))
    def test_budget(self):
        self.assertEqual(r.budget(3600,600,0),600)
        self.reject(lambda:r.budget(1024,20,0));self.reject(lambda:r.budget(3600,20,64))
    def test_exact_seals(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);p=root/'x';p.write_text('one');s=r.snapshot(root);p.write_text('two');self.reject(lambda:r.verify_snapshot(root,s))
            p.unlink();p.symlink_to('/etc/hosts');self.reject(lambda:r.snapshot(root));p.unlink();os.mkfifo(p);self.reject(lambda:r.snapshot(root));p.unlink()
            p.write_text('x');os.link(p,root/'alias');self.reject(lambda:r.snapshot(root))
    def test_provider_unknown_fails(self):
        self.reject(lambda:r.filter_qualification(Path('/tmp'),Path('/tmp'),{},{}))
    def test_exception_occurrences_and_owner(self):
        error=RuntimeError('root');group=ExceptionGroup('group',[error,error]);group.causes=[error,error];group.outcome={'remainingOwned':{},'stage':'real'}
        graph=runner.exception_graph([error,group,error]);self.assertEqual(graph['orderedRootOccurrences'][0],graph['orderedRootOccurrences'][2])
        self.assertEqual(graph['nodes'][1]['customCauses'],[0,0]);self.assertEqual(graph['nodes'][1]['originalOwnerReceipt']['stage'],'real')
    def test_formatter_failure_retained(self):
        class Hostile(Exception):
            def __str__(self):raise RuntimeError('format')
        errors=[Hostile()];g=runner.exception_graph(errors);self.assertEqual(len(g['orderedRootOccurrences']),2);self.assertIn('messageFailure',g['nodes'][0])
    def test_durable_write_failure_retains_root(self):
        errors=[RuntimeError('root')]
        with tempfile.TemporaryDirectory() as tmp,patch.object(runner.os,'open',side_effect=OSError('write')):
            self.assertFalse(runner.persist(Path(tmp),errors,1));self.assertEqual(len(errors),3);self.assertEqual(errors[0].args,('root',))
    def test_bootstrap_missing_and_broken_real_child(self):
        for broken in (False,True):
            with tempfile.TemporaryDirectory() as tmp:
                root=Path(tmp);(root/'eng').mkdir();shutil.copy(ROOT/'eng/run-unit-census.py',root/'eng/run-unit-census.py')
                if broken:(root/'eng/unit-census-results.py').write_text('raise RuntimeError("PRIVATE_SENTINEL")')
                p=subprocess.run([sys.executable,'-B',root/'eng/run-unit-census.py','--configuration','Release'],capture_output=True,timeout=10)
                self.assertEqual(p.returncode,1);self.assertEqual(p.stdout,b'');self.assertEqual(p.stderr,b'UNIT_CENSUS_FAILED_BOOTSTRAP\n')
    def test_final_seal_failure_revokes(self):
        with patch.object(runner,'execute',lambda lane:(setattr(lane,'stable',True),setattr(lane,'unfiltered',True))),patch.object(runner.Lane,'check',side_effect=r.Rejected('UC_SEAL')),contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(runner.guarded_main(),1)
        self.assertNotIn('"censusQualified": true',output.getvalue())
    def test_ledger1_mutation_during_next_owned_command(self):
        owner=load('census_mutation_owner',ROOT/'eng/package-smoke-process.py');owner.enable_subreaper()
        with tempfile.TemporaryDirectory() as tmp:
            lane=runner.Lane(Path(tmp),r,owner);c,d=fixture();raw=lane.logs/'first.stdout';raw.write_text(json.dumps(d));lane.logseal=r.snapshot(lane.logs)
            lane.ledger(1,raw,c);ledger=lane.evidence/'ledger1/ledger.json'
            with contextlib.redirect_stdout(io.StringIO()),self.assertRaises(BaseExceptionGroup):
                lane.run('second',[sys.executable,'-c','from pathlib import Path;Path('+repr(str(ledger))+').write_text("changed")'],seconds=2)
            self.assertTrue(lane.errors);self.assertEqual(lane.commands['second']['receipt']['remainingOwned'],{})
    def test_parse_input_mutation(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=runner.Lane(Path(tmp),r,None);c,d=fixture();raw=lane.logs/'first.stdout';raw.write_text(json.dumps(d));lane.logseal=r.snapshot(lane.logs)
            original=r.classify
            def mutate(doc,catalog):
                result=original(doc,catalog);raw.write_text('{}');return result
            with patch.object(r,'classify',side_effect=mutate),self.assertRaises(BaseExceptionGroup):lane.ledger(1,raw,c)
            self.assertEqual(lane.observations,[])
    def test_summary_render_failure_keeps_original(self):
        original=RuntimeError('body');seen=[]
        def fail(lane):
            seen.append(lane);raise original
        with patch.object(runner,'execute',side_effect=fail),patch('builtins.print',side_effect=OSError('render')):
            self.assertEqual(runner.guarded_main(),1)
        self.assertIs(seen[0].errors[0],original);self.assertTrue(any(type(e) is OSError for e in seen[0].errors))
    def test_known_provider_cannot_qualify_unknown_hooks(self):
        proof={'reviewed':True,'evaluatedHooks':[],'files':[],'effectiveDiscoveryFilter':'non-narrowing'}
        self.reject(lambda:r.filter_qualification(Path('/tmp'),Path('/tmp'),{'Items':{'TestingPlatformBuilderHook':[{'Identity':'foreign'}]}},{'providerProof':proof}))
        self.reject(lambda:r.filter_qualification(Path('/tmp'),Path('/tmp'),{'Items':{'TestingPlatformBuilderHook':[]}},{'providerProof':proof}))
    def test_full_source_catalog_synthetic_metadata_only(self):
        c=r.read_json(ROOT/'eng/unit-census.json');r.verify_catalog(ROOT,c);rows=[]
        for method in c['methods']:
            if 'Release' not in method['configurations']:continue
            self.assertIsNotNone(method['expectedCases'])
            for index in range(method['expectedCases']):
                rows.append({'uid':'SYNTHETIC-'+str(len(rows)),'displayName':'synthetic display',
                    'type':{'assemblyFullName':c['assemblyFullName'],'namespace':method['namespace'],'typeName':method['namespace']+'.'+method['className'],'methodName':method['methodName'],'methodArity':method['arity'],'parameterTypeFullNames':method['parameters'],'returnTypeFullName':method['return']},
                    'location':{'file':'/_/'+method['source'],'lineStart':method['attributeLine'],'lineEnd':method['endLine']}})
        result=r.classify({'schemaVersion':1,'tests':rows},c)
        self.assertTrue(r.stable(result,result));self.assertEqual(result['summary']['runtimeSkipped'],'notObserved')
    def provider_fixture(self,root):
        c=r.read_json(ROOT/'eng/unit-census.json');proof=c['providerProof'];packages=root/'packages';packages.mkdir();dll=packages/'proof.dll';dll.write_bytes(b'known exact binary')
        proof['files']=[{'path':'proof.dll','sha256':r.sha(dll)}]
        hooks=copy.deepcopy(proof['hooks']);obj=root/'test/SharpLink.UnitTests/obj/Release/net10.0';obj.mkdir(parents=True)
        entry=obj/'MicrosoftTestingPlatformEntryPoint.cs';entry.write_text(r.expected_entrypoint())
        registration=obj/'SelfRegisteredExtensions.cs';registration.write_text(r.expected_registration(hooks))
        output=root/'test/SharpLink.UnitTests/bin/Release/net10.0';output.mkdir(parents=True)
        (output/'Microsoft.Testing.Extensions.CodeCoverage.dll').write_bytes(dll.read_bytes());proof['codeCoverageStaticIL']['assemblySHA256']=r.sha(dll)
        metadata={'Properties':{'RootNamespace':'SharpLink.UnitTests','TestingPlatformHostFactory':'','Language':'C#','GenerateTestingPlatformEntryPoint':'true','GenerateTestingPlatformApplicationHelper':'true','GenerateSelfRegisteredExtensions':'true'},'Items':{'TestingPlatformBuilderHook':hooks,'TestingPlatformCommandLineOptionDefault':[],'Compile':[{'FullPath':str(entry)},{'FullPath':str(registration)}]}}
        return c,packages,metadata,output,obj
    def test_bound_provider_templates_and_actual_inputs(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);c,packages,meta,output,obj=self.provider_fixture(root)
            self.assertTrue(r.filter_qualification(root,packages,meta,c,{}, {})['unfilteredQualified'])
            r.discovery_inputs(['/known/dotnet','/known/Unit.dll','--list-tests','json'],{})
            for env in ({'commandLineOptions__treenode-filter__0':'secret'}, {'COMMANDLINEOPTIONDEFAULTS:filter-uid':'secret'}):
                self.reject(lambda:r.filter_qualification(root,packages,meta,c,env,{}))
                self.reject(lambda:r.discovery_inputs(['/known/dotnet','/known/Unit.dll','--list-tests','json'],env))
            for tail in (['@x'],['--list-tests','json','--filter-uid','x'],['--enable-dynamic-extensions']):
                self.reject(lambda:r.discovery_inputs(['/known/dotnet','/known/Unit.dll']+tail,{}))
    def test_provider_config_and_default_rejections(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);c,packages,meta,output,obj=self.provider_fixture(root)
            config=output/'SharpLink.UnitTests.testconfig.json'
            for value in ({'commandLineOptions':{'treenode-filter':['x']}},{'COMMANDLINEOPTIONS:filter-uid:0':'x'},{'commandLineOptionDefaults':{'filter-uid':['x']}}):
                config.write_text(json.dumps(value));self.reject(lambda:r.filter_qualification(root,packages,meta,c,{},{}))
            config.unlink();meta['Items']['TestingPlatformCommandLineOptionDefault']=[{'Identity':'filter-uid','Value':'x'}]
            self.reject(lambda:r.filter_qualification(root,packages,meta,c,{},{}))
    def test_provider_late_hook_compiled_entry_and_bytes(self):
        for mutation in ('hook','compile','host','generated','bytes','selected-asset','flag'):
            with tempfile.TemporaryDirectory() as tmp:
                root=Path(tmp);c,packages,meta,output,obj=self.provider_fixture(root)
                if mutation=='hook':meta['Items']['TestingPlatformBuilderHook'][0]['TypeFullName']='Foreign.Hook'
                elif mutation=='compile':meta['Items']['Compile'].pop()
                elif mutation=='host':meta['Properties']['TestingPlatformHostFactory']='Other.Host'
                elif mutation=='generated':
                    p=obj/'SelfRegisteredExtensions.cs';p.write_text(p.read_text()+' class Foreign {}')
                elif mutation=='bytes':(packages/'proof.dll').write_bytes(b'changed')
                elif mutation=='selected-asset':(output/'Microsoft.Testing.Extensions.CodeCoverage.dll').write_bytes(b'other TFM')
                else:meta['Properties']['GenerateTestingPlatformEntryPoint']='false'
                self.reject(lambda:r.filter_qualification(root,packages,meta,c,{},{}))
    def test_props_presence_not_actual_import(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);c,packages,meta,output,obj=self.provider_fixture(root);folder=packages/'known/1.0';folder.mkdir(parents=True);props=folder/'Known.props';props.write_text('<Project/>')
            c['providerProof']['files']=[{'packageRoot':'known/1.0','basename':'Known.props','allowedSHA256':[r.sha(props)],'requiredSHA256':[r.sha(props)]}]
            self.reject(lambda:r.filter_qualification(root,packages,meta,c,{},{}))
            self.assertTrue(r.filter_qualification(root,packages,meta,c,{}, {props:r.file_identity(props)})['unfilteredQualified'])
    def test_generated_lexer_cannot_ignore_code_or_directives(self):
        self.assertEqual(r.generated_tokens('class X {} // note'),r.generated_tokens('class X{}'))
        self.assertNotEqual(r.generated_tokens('class X{} class Y{}'),r.generated_tokens('class X{}'))
        self.reject(lambda:r.generated_tokens('#if X\nclass X{}\n#endif'))
    def test_bootstrap_temp_initialization_real_child(self):
        for operation in ('mkdtemp','chmod'):
            target=ROOT/'eng/run-unit-census.py'
            script='import tempfile,os,runpy; '+('tempfile.' if operation=='mkdtemp' else 'os.')+operation+'=lambda *a,**k: (_ for _ in ()).throw(OSError("PRIVATE_INIT_SENTINEL")); runpy.run_path('+repr(str(target))+',run_name="__main__")'
            child=subprocess.run([sys.executable,'-B','-c',script],capture_output=True,timeout=10)
            self.assertEqual(child.returncode,1);self.assertEqual(child.stdout,b'');self.assertEqual(child.stderr,b'UNIT_CENSUS_FAILED_BOOTSTRAP\n')
    def test_descriptor_replacement_between_stat_and_open(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'x';path.write_bytes(b'original');real=os.open;replaced=[]
            def race(name,flags,*args,**kwargs):
                if name=='x' and not replaced:
                    replaced.append(True);path.rename(Path(tmp)/'old');path.write_bytes(b'foreign')
                return real(name,flags,*args,**kwargs)
            with patch.object(r.os,'open',side_effect=race):self.reject(lambda:r.file_identity(path))
    def test_same_inode_restore_detected_by_ctime(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'x';path.write_bytes(b'original');real=os.read;changed=[]
            def race(fd,size):
                value=real(fd,size)
                if not changed:
                    changed.append(True);info=path.stat();path.write_bytes(b'changed');path.write_bytes(b'original');os.utime(path,ns=(info.st_atime_ns,info.st_mtime_ns))
                return value
            with patch.object(r.os,'read',side_effect=race):self.reject(lambda:r.file_identity(path))
    def test_snapshot_detects_new_empty_directory_during_scan(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);(root/'x').write_text('x');real=r.file_identity
            def race(path):
                value=real(path);(root/'extra').mkdir(exist_ok=True);return value
            with patch.object(r,'file_identity',side_effect=race):self.reject(lambda:r.snapshot(root))
    def test_evidence_parent_extra_directory_and_symlink(self):
        for kind in ('directory','symlink'):
            with tempfile.TemporaryDirectory() as tmp:
                lane=runner.Lane(Path(tmp),r,None)
                if kind=='directory':(lane.evidence/'extra').mkdir()
                else:(lane.evidence/'extra').symlink_to('/tmp',target_is_directory=True)
                with self.assertRaises(BaseExceptionGroup):lane.check()
    def test_summary_mutation_after_seal_fails(self):
        seen=[];real=runner.Lane.extend_evidence
        def mutate(lane,new):
            real(lane,new)
            if 'summary.json' in new:(lane.evidence/'summary.json').write_text('{}')
        def success(lane):seen.append(lane);lane.stable=True;lane.unfiltered=True
        with patch.object(runner,'execute',side_effect=success),patch.object(runner.Lane,'extend_evidence',new=mutate),contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(runner.guarded_main(),1)
        self.assertTrue(seen[0].errors);self.assertNotIn('"censusQualified": true',output.getvalue())
    def test_deadline_crossed_inside_final_scan(self):
        with tempfile.TemporaryDirectory() as tmp:
            lane=runner.Lane(Path(tmp),r,None);clock=[1.0];lane.stop=2.0;real=r.verify_snapshot
            def slow(*args):
                real(*args);clock[0]=3.0
            with patch.object(runner.time,'monotonic',side_effect=lambda:clock[0]),patch.object(r,'verify_snapshot',side_effect=slow),self.assertRaises(BaseExceptionGroup):lane.check()
    def test_deadline_crossed_inside_summary_serialization(self):
        clock=[1.0];real=r.write_json
        def success(lane):lane.stable=True;lane.unfiltered=True;lane.stop=2.0
        def slow(directory,name,value):
            real(directory,name,value)
            if name=='summary.json':clock[0]=3.0
        original_module=runner.module
        def modules(name,path):return r if name=='unit_census_results' else original_module(name,path)
        with patch.object(runner,'execute',side_effect=success),patch.object(runner,'module',side_effect=modules),patch.object(runner.time,'monotonic',side_effect=lambda:clock[0]),patch.object(r,'write_json',side_effect=slow),contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(runner.guarded_main(),1)
        self.assertNotIn('"censusQualified": true',output.getvalue())
    def test_exact_graph_and_release_properties(self):
        c=r.read_json(ROOT/'eng/unit-census.json');name=c['project'];spec=c['projectMetadata'][name]
        original={'Properties':{'MSBuildProjectFullPath':str(ROOT/name),'Configuration':'Release','PublishAot':'','TargetFramework':spec['targetFramework'],'AssemblyName':spec['assemblyName'],'TargetPath':str(ROOT/spec['output']),'DefineConstants':'TRACE;RELEASE;NET;NET10_0;NETCOREAPP'},'Items':{'ProjectReference':[{'FullPath':str(ROOT/p)} for p in spec['references']]}}
        r.project_metadata(ROOT,name,original,c)
        for key,value in [('DefineConstants','TRACE;DEBUG'),('Configuration','Debug'),('TargetFramework','net9.0'),('TargetPath','/foreign.dll'),('PublishAot','true')]:
            meta=copy.deepcopy(original);meta['Properties'][key]=value;self.reject(lambda:r.project_metadata(ROOT,name,meta,c))
        meta=copy.deepcopy(original);meta['Items']['ProjectReference'].pop();self.reject(lambda:r.project_metadata(ROOT,name,meta,c))
    def test_postcompile_set_rejects_extra_or_missing_with_normal_entrypoints(self):
        c=r.read_json(ROOT/'eng/unit-census.json');names=c['unitSources']+c['linkedSources']+c['generatedCompile']
        for variation in ('extra','missing','duplicate'):
            items=[{'FullPath':str(ROOT/p)} for p in names]
            if variation=='extra':items.append({'FullPath':str(ROOT/'test/SharpLink.UnitTests/ForeignBootstrap.cs')})
            elif variation=='missing':items.pop(0)
            else:items.append(items[-1])
            self.reject(lambda:r.compiled_inputs(ROOT,{'Items':{'Compile':items}},c))
    def test_package_folder_single_canonical_identity(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);packages=root/'packages';packages.mkdir();identity=r.legacy.directory_identity_strict(packages)
            r.package_folder_identity({str(packages):{}},str(packages)+'/',packages,identity)
            r.package_folder_identity({str(packages)+'/':{}},str(packages),packages,identity)
            self.reject(lambda:r.package_folder_identity({str(packages):{},str(packages)+'/':{}},str(packages),packages,identity))
            foreign=root/'foreign';foreign.mkdir();self.reject(lambda:r.package_folder_identity({str(foreign):{}},str(foreign),packages,identity))
            packages.rename(root/'original');packages.symlink_to(root/'original',target_is_directory=True)
            self.reject(lambda:r.package_folder_identity({str(packages):{}},str(packages),packages,identity))
    def test_postcompile_full_ledger_positive_and_generated_code_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);prefix='test/SharpLink.UnitTests/obj/Release/net10.0/'
            c={'unitSources':['test/SharpLink.UnitTests/K.cs'],'linkedSources':[], 'generatedCompile':[prefix+'SharpLink.UnitTests.AssemblyInfo.cs',prefix+'.NETCoreApp,Version=v10.0.AssemblyAttributes.cs',prefix+'SelfRegisteredExtensions.cs',prefix+'MicrosoftTestingPlatformEntryPoint.cs']}
            for name in c['unitSources']+c['generatedCompile']:(root/name).parent.mkdir(parents=True,exist_ok=True);(root/name).write_text('class K {}')
            (root/(prefix+'SharpLink.UnitTests.AssemblyInfo.cs')).write_text('using System;\nusing System.Reflection;\n[assembly: System.Reflection.AssemblyConfigurationAttribute("Release")]\n[assembly: System.Reflection.AssemblyVersionAttribute("3.0.0.0")]\n')
            (root/(prefix+'.NETCoreApp,Version=v10.0.AssemblyAttributes.cs')).write_text('using System; using System.Reflection; [assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(".NETCoreApp,Version=v10.0", FrameworkDisplayName = ".NET 10.0")]')
            meta={'Items':{'Compile':[{'FullPath':str(root/name)} for name in c['unitSources']+c['generatedCompile']]}}
            self.assertEqual(len(r.compiled_inputs(root,meta,c)),5)
            info=root/(prefix+'SharpLink.UnitTests.AssemblyInfo.cs');original=info.read_text()
            c['assemblyMetadataProof']=r.assembly_metadata_proof()
            self.reject(lambda:r.compiled_inputs(root,meta,c))
            info.write_text(original+r.MTP_ASSEMBLY_MARKER+'\n')
            self.assertEqual(len(r.compiled_inputs(root,meta,c)),5)
            for marker in (r.MTP_ASSEMBLY_MARKER.replace('"true"','"false"'),r.MTP_ASSEMBLY_MARKER.replace('Microsoft.Testing.Platform.Application','Foreign'),r.MTP_ASSEMBLY_MARKER+'\n'+r.MTP_ASSEMBLY_MARKER):
                info.write_text(original+marker+'\n');self.reject(lambda:r.compiled_inputs(root,meta,c))
            info.write_text(original+r.MTP_ASSEMBLY_MARKER+'\n')
            with tempfile.TemporaryDirectory() as external:
                path=Path(external)/'TUnit.Core.GeneratedNamespace.cs';path.write_text('namespace TUnit.Generated {}')
                package_sources={path:'package:tunit.core/1.73.19/'+r.TUNIT_CORE_SOURCE_MEMBER}
                self.reject(lambda:r.compiled_inputs(root,meta,c,package_sources))
                extended=copy.deepcopy(meta);extended['Items']['Compile'].insert(0,{'FullPath':str(path)})
                ledger=r.compiled_inputs(root,extended,c,package_sources)
                self.assertEqual(len(ledger),6);self.assertIn(package_sources[path],ledger)
                self.reject(lambda:r.compiled_inputs(root,extended,c))
            with (root/(prefix+'SharpLink.UnitTests.AssemblyInfo.cs')).open('a') as file:file.write('class ForeignBootstrap {}')
            self.reject(lambda:r.compiled_inputs(root,meta,c))
    def test_public_rejection_codes_never_echo_unknown_errors(self):
        known=r.Rejected('UC_COMPILE_SET');unknown=r.Rejected('PRIVATE_SENTINEL');ordinary=RuntimeError('PRIVATE_SENTINEL')
        result=r.public_rejections([BaseExceptionGroup('PRIVATE_SENTINEL',[known,unknown,ordinary]),known])
        self.assertEqual(result['codes'],['UC_COMPILE_SET']);self.assertNotIn('PRIVATE_SENTINEL',json.dumps(result));self.assertEqual(result['unclassifiedExceptionNodes'],2)
    def test_metadata_diagnostics_only_counts_hashes_and_known_labels(self):
        c=r.read_json(ROOT/'eng/unit-census.json');name=c['project'];value={'Properties':{'DefineConstants':'TRACE;RELEASE;PRIVATE_SENTINEL','MSBuildAllProjects':'PRIVATE_SENTINEL'},'Items':{'Compile':[{'FullPath':'/PRIVATE_SENTINEL'}],'ProjectReference':[]}}
        result=r.metadata_facts(ROOT,name,value,c);serialized=json.dumps(result)
        self.assertNotIn('PRIVATE_SENTINEL',serialized);self.assertEqual(result['Compile']['extraCount'],1);self.assertEqual(result['constants']['count'],3)
        self.assertEqual(r.metadata_facts(ROOT,name,{'Properties':None,'Items':None},c)['Compile']['rowCount'],None)
    def test_metadata_diagnostics_do_not_change_validation(self):
        c=r.read_json(ROOT/'eng/unit-census.json');name=c['project'];spec=c['projectMetadata'][name]
        v={'Properties':{'MSBuildProjectFullPath':str(ROOT/name),'Configuration':'Release','PublishAot':'','TargetFramework':spec['targetFramework'],'AssemblyName':spec['assemblyName'],'TargetPath':str(ROOT/spec['output']),'DefineConstants':'TRACE;RELEASE;PRIVATE_SENTINEL'},'Items':{'ProjectReference':[{'FullPath':str(ROOT/p)} for p in spec['references']]}}
        before=copy.deepcopy(v);r.metadata_facts(ROOT,name,v,c);self.assertEqual(v,before);self.reject(lambda:r.project_metadata(ROOT,name,v,c))
    def test_failure_summary_exports_controlled_metadata_only(self):
        c=r.read_json(ROOT/'eng/unit-census.json');original_module=runner.module
        def modules(name,path):return r if name=='unit_census_results' else original_module(name,path)
        def fail(lane):
            lane.source=ROOT;lane.metadata_project=c['project'];lane.metadata_catalog=c
            lane.metadata_input={'Properties':{'DefineConstants':'PRIVATE_SENTINEL'},'Items':{}}
            lane.validation_phase='project-properties';raise r.Rejected('UC_RELEASE_CONSTANTS')
        with patch.object(runner,'module',side_effect=modules),patch.object(runner,'execute',side_effect=fail),contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(runner.guarded_main(),1)
        summary=json.loads(out.getvalue());self.assertEqual(summary['rejections']['codes'],['UC_RELEASE_CONSTANTS']);self.assertFalse(summary['censusQualified']);self.assertNotIn('PRIVATE_SENTINEL',out.getvalue())
    def test_metadata_diagnostic_failure_preserves_original_root(self):
        c=r.read_json(ROOT/'eng/unit-census.json');original_module=runner.module;seen=[];body=RuntimeError('body')
        def modules(name,path):return r if name=='unit_census_results' else original_module(name,path)
        def fail(lane):
            seen.append(lane);lane.source=ROOT;lane.metadata_project=c['project'];lane.metadata_catalog=c;lane.metadata_input={};raise body
        with patch.object(runner,'module',side_effect=modules),patch.object(runner,'execute',side_effect=fail),patch.object(r,'metadata_facts',side_effect=ValueError('diagnostic')),contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(runner.guarded_main(),1)
        self.assertIs(seen[0].errors[0],body);self.assertEqual(len(seen[0].errors),2)
    def test_actual_owner_timeout_and_descendant_join(self):
        owner=load('census_test_owner',ROOT/'eng/package-smoke-process.py');owner.enable_subreaper()
        with tempfile.TemporaryDirectory() as tmp:
            logs=Path(tmp)
            with self.assertRaises(owner.CommandFailure) as caught:
                owner.run_owned('timeout',[sys.executable,'-c','import time; time.sleep(20)'],ROOT,os.environ.copy(),logs,.1)
            self.assertTrue(caught.exception.outcome['timedOut']);self.assertEqual(caught.exception.outcome['remainingOwned'],{})
            with self.assertRaises(owner.CommandFailure) as child:
                owner.run_owned('child',[sys.executable,'-c','import subprocess,sys; subprocess.Popen([sys.executable,"-c","import time;time.sleep(20)"])'],ROOT,os.environ.copy(),logs,2)
            self.assertEqual(child.exception.outcome['remainingOwned'],{});self.assertTrue(child.exception.outcome['cleanupErrors'])

if __name__=='__main__':
    # unittest messages may include synthetic-only values; no real raw reports.
    unittest.main()
