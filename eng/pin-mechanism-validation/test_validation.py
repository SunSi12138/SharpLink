#!/usr/bin/env python3
"""Pure-Python negative controls only. Never starts a product/test worker."""
import contextlib, copy, importlib.util, io, json, re, tempfile, unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest.mock import patch
HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('pin_runner',HERE/'run.py');v=importlib.util.module_from_spec(spec);spec.loader.exec_module(v)

def check_workflow_env_contexts(workflow):
    # This bounded scanner supplements YAML parsing for our block-style workflow.
    # GitHub context availability excludes runner from workflow/job env, but permits
    # it under steps.env. https://docs.github.com/en/actions/reference/workflows-and-actions/contexts#context-availability
    scope=None
    for line in workflow.splitlines():
        if not line.strip() or line.lstrip().startswith('#'):continue
        indent=len(line)-len(line.lstrip())
        if scope is not None and indent<=scope:scope=None
        if re.fullmatch(r'\s*env:\s*',line) and indent in (0,4):scope=indent;continue
        if scope is not None:
            for expression in re.findall(r'\$\{\{(.*?)\}\}',line):
                if re.search(r'\brunner\s*(?:\.|\[)',expression):
                    raise ValueError('runner context is unavailable in workflow/job env')

def sample(arm='baseline',pair=1):
    rows=[]
    for name in v.CASES:
        route,n=name.rsplit('-n',1);n=int(n);ops=100
        rows.append(dict(case=name,route=v.ROUTES[route],itemsPerInvocation=n,ops=ops,allocatedBytes=10000,cpuTicks=10000000,elapsedTicks=2000000000,warmupSeconds=1,measureSeconds=2,producer='synchronous-finite',dataFrames=ops*n,completeFrames=ops,payloadChecksum=ops*n*(n-1)//2,producerGets=ops,producerMoves=ops*(n+1),producerDisposes=ops,callMapEmpty=True,callStatesReleased=True,finalQueuedBytes=0,sendStates=0,sendWaiters=0,sendCredit=1000000,connectionWindow=1000000,passed=True))
    return dict(schemaVersion=1,status='pass',failure=None,arm=arm,pair=pair,sourceTree=v.BASE_TREE if arm=='baseline' else v.SOURCE_TREE,reviewedSourceCommit=v.LABELS[arm],runtimeVersion='10.0.14',runtime='.NET 10.0.14',architecture='X64',processorCount=4,stopwatchFrequency=1000000000,cpuTicksPerSecond=10000000,scope=v.SCOPE,boundary=v.BOUNDARY,dynamicScope='module_token_registration_only_not_DynamicChannel_or_module_lease_dispatch',transport='in_memory_validating_sink_with_deterministic_per_item_credit_return',measuredProducer='synchronous-finite',cases=rows[pair-1:]+rows[:pair-1],asyncControl=dict(passed=True,scope='one_cold_async_producer_control_allocation_only_not_timing_or_benefit',ops=32,producerGets=32,producerMoves=64,producerDisposes=32,allocatedBytes=1000,callStatesReleased=True,callMapEmpty=True))

def provenance_fixture(root,base=None):
    base=base or 'dd0dcacd8edf9552e9face66f4a6473b258428b2'
    candidate='c'*40;tool='d'*40;release='e'*40;tooltree='f'*40
    here=root/'eng/pin-mechanism-validation';here.mkdir(parents=True)
    request=dict(schema_version=1,baseline_sha=base,candidate_sha=candidate,tools_sha=tool,tools_tree=tooltree)
    (root/v.REQUEST).write_text(json.dumps(request));files=['eng/pin-mechanism-validation/run.py','eng/pin-mechanism-validation/test_validation.py']
    (here/'tool-files.json').write_text(json.dumps(files))
    replies={('rev-parse','HEAD'):release,('status','--porcelain','--untracked-files=all'):'',
        ('show','-s','--format=%P',base):v.BASE_PARENTS[base],
        ('show','-s','--format=%P','HEAD'):tool,
        ('diff','--name-status','--no-renames',tool,'HEAD'):'A\t'+v.REQUEST,
        ('rev-parse',tool+'^{tree}'):tooltree,
        ('show','-s','--format=%P',tool):candidate,
        ('show','-s','--format=%P',candidate):base,
        ('rev-parse',base+'^{tree}'):v.BASE_TREE,
        ('rev-parse',candidate+'^{tree}'):v.SOURCE_TREE,
        ('diff','--name-status','--no-renames',base,candidate):'\n'.join(v.SOURCE_DIFF),
        ('diff','--name-status','--no-renames',candidate,tool):'\n'.join('A\t'+f for f in files)}
    return here,request,release,replies

def closure_deps():
    graph={};libraries={}
    for name in v.CLOSURE_PRODUCTS+v.CLOSURE_PACKAGES:
        product=name in v.CLOSURE_PRODUCTS;version='3.0.0' if product else '10.0.12';key=name+'/'+version
        graph[key]={'runtime':{(name+'.dll' if product else 'lib/net10.0/'+name+'.dll'):{}}}
        libraries[key]={'type':'project' if product else 'package'}
    graph['SharpLink.Server/3.0.0']['dependencies']={name:'10.0.12' for name in v.CLOSURE_PACKAGES}
    return dict(runtimeTarget={'name':'.NETCoreApp,Version=v10.0'},targets={'.NETCoreApp,Version=v10.0':graph},libraries=libraries)

def fake_frozen(root):
    product=root/'product';product.mkdir()
    for name in v.CLOSURE_NAMES:(product/name).write_text(name)
    generator=root/'SharpLink.Generator.dll';generator.write_text('generator')
    return dict(directory=product,hashes=v.hashes(product),generator=generator,generator_sha256=v.file_sha(generator))

class Validation(unittest.TestCase):
    def rejects(self,d):
        with self.assertRaises(v.GateError):v.validate_measurement(d,'baseline',1)
    def test_exact_eight_cells_and_fixed_alternating_schedule(self):
        self.assertEqual(len(v.validate_measurement(sample(),'baseline',1)),8)
        self.assertEqual(v.schedule(),[(p,a) for p in range(1,7) for a in (('baseline','candidate') if p%2 else ('candidate','baseline'))])
        for mutate in (lambda d:d['cases'].pop(),lambda d:d['cases'].append(d['cases'][0]),lambda d:d['cases'].reverse()):
            d=sample();mutate(d);self.rejects(d)
    def test_missing_fields_and_wrong_source_runtime_boundary_never_pass(self):
        for field in ('schemaVersion','status','arm','pair','sourceTree','reviewedSourceCommit','runtimeVersion','runtime','architecture','processorCount','scope','boundary'):
            d=sample();del d[field];self.rejects(d)
        for field,value in [('sourceTree',v.SOURCE_TREE),('runtimeVersion','10.0.2'),('pair',True),('processorCount',True),('boundary','inner_try_acquire_only')]:
            d=sample();d[field]=value;self.rejects(d)
    def test_module_configured_not_mislabeled_actual_dynamic(self):
        d=sample();d['cases'][3]['route']='DynamicChannel';self.rejects(d)
        d=sample();d['cases'][0]['producer']='asynchronous';self.rejects(d)
    def test_full_producer_wire_and_teardown_conservation(self):
        for field in ('dataFrames','completeFrames','payloadChecksum','producerGets','producerMoves','producerDisposes','finalQueuedBytes','sendStates','sendWaiters','sendCredit'):
            d=sample();d['cases'][2][field]+=1;self.rejects(d)
        for field in ('callMapEmpty','callStatesReleased'):
            d=sample();d['cases'][2][field]=False;self.rejects(d)
    def test_positive_measurable_costs_and_exact_windows(self):
        for field,value in [('ops',0),('cpuTicks',0),('elapsedTicks',1),('allocatedBytes',-1),('ops',True),('allocatedBytes',float('nan')),('warmupSeconds',0),('measureSeconds',1)]:
            d=sample();d['cases'][0][field]=value;self.rejects(d)
    def test_async_control_is_required_and_not_a_timing_cell(self):
        d=sample();del d['asyncControl'];self.rejects(d)
        for field,value in [('producerMoves',32),('callStatesReleased',False),('allocatedBytes',-1),('cpuTicks',5)]:
            d=sample();d['asyncControl'][field]=value;self.rejects(d)
    def test_all_96_scalar_rows_and_48_individual_pairs_retained(self):
        results=[sample(a,p) for p,a in v.schedule()]
        self.assertEqual(sum(len(v.scalar_rows(r)) for r in results),96)
        pairs=v.paired_summaries(results);self.assertEqual(len(pairs),48)
        self.assertEqual({(r['pair'],r['case']) for r in pairs},{(p,c) for p in range(1,7) for c in v.CASES})
        for r in pairs:self.assertEqual(r['cpu_ratio'],1);self.assertEqual(r['allocated_bytes_per_invocation_delta'],0)
        with self.assertRaises(v.GateError):v.paired_summaries(results[:-1])
        with self.assertRaises(v.GateError):v.paired_summaries(results[:-1]+[results[0]])
    def test_scalar_allowlist_excludes_raw_paths_or_extra_report_text(self):
        d=sample();d['cases'][0]['rawReport']='/private/raw.json';d['cases'][0]['exception']='unbounded'
        text=json.dumps(v.scalar_rows(d));self.assertNotIn('rawReport',text);self.assertNotIn('/private',text);self.assertNotIn('exception',text)
        self.assertLess(len(text),16000)
    def test_pin_12_and_relevant_control_census(self):
        original=json.loads((HERE.parent/'static-stream-unit-validation/expected-uids.json').read_text());selected=v.focused(original)
        self.assertEqual(len(v.PIN),12);self.assertEqual(len(set(selected)),len(selected))
        self.assertTrue(set(v.PIN).issubset(selected));self.assertLess(len(selected),150)
        self.assertTrue(any('TransportOutputFaultTests' in u for u in selected))
        self.assertTrue(any('UnaryServiceThrow' in u for u in selected))
        self.assertFalse(any('TypedWindowUpdate' in u for u in selected))
    def test_actual_dynamic_exact_execution_not_other_argument_or_skip(self):
        counters=dict(total=1,passed=1,failed=0,skipped=0,cancelled=0,timedOut=0,flaky=0)
        r=dict(schemaVersion=1,assemblyName='SharpLink.IntegrationTests',tunitVersion='1.73.19.0',runtimeVersion='.NET 10.0.14',publicationGeneration='a'*32,summary=counters,groups=[dict(summary=counters,tests=[dict(id=v.DYNAMIC_UID,status='passed')])])
        v.validate_integration(r,0,False)
        for field,value in [('id',v.DYNAMIC_UID.replace('.3.1.0','.1.1.0')),('status','skipped')]:
            bad=copy.deepcopy(r);bad['groups'][0]['tests'][0][field]=value
            with self.assertRaises(v.GateError):v.validate_integration(bad,0,False)
        with self.assertRaises(v.GateError):v.validate_integration(r,0,True)
    def test_workflow_no_upload_retry_disabled_audit_or_extra_campaign(self):
        w=(HERE.parents[1]/'.github/workflows/server-call-pin-mechanism-no-upload.yml').read_text()
        self.assertIn('contents: read',w);self.assertIn('persist-credentials: false',w)
        self.assertIn('sha512sum --check',w);self.assertIn('Sdk/10.0.102',w);self.assertIn('Runtime/10.0.14',w)
        for value in ('upload-artifact','actions/cache','NuGetAudit=false','continue-on-error','dotnet test','workflow_dispatch'):self.assertNotIn(value,w)
        source=(HERE/'run.py').read_text();self.assertNotIn("tcp(roots",source);self.assertNotIn('NuGetAudit=false',source)
    def test_workflow_runner_context_requires_step_scope_or_shell_bootstrap(self):
        w=(HERE.parents[1]/'.github/workflows/server-call-pin-mechanism-no-upload.yml').read_text()
        check_workflow_env_contexts(w)
        self.assertIn('NUGET_HTTP_CACHE_PATH="$(mktemp -d "$RUNNER_TEMP/pin-nuget-http-XXXXXX")"',w)
        self.assertIn('echo "NUGET_HTTP_CACHE_PATH=$NUGET_HTTP_CACHE_PATH" >> "$GITHUB_ENV"',w)
        for expression in ('${{ runner.temp }}',"${{ runner['temp'] }}",'${{ format(\'{0}\', runner.temp) }}'):
            for broken in ('env:\n  CACHE: '+expression+'\n',
                           w.replace('    env:\n','    env:\n      CACHE: '+expression+'\n',1)):
                with self.assertRaises(ValueError):check_workflow_env_contexts(broken)
        check_workflow_env_contexts('jobs:\n  one:\n    steps:\n      - run: echo ok\n        env:\n          CACHE: ${{ runner.temp }}\n')
    def test_exact_transitive_runtime_closure_rejects_unknown_missing_or_altered_assets(self):
        d=closure_deps();self.assertEqual(set(v.runtime_closure(d)),v.CLOSURE_NAMES)
        target='.NETCoreApp,Version=v10.0';server='SharpLink.Server/3.0.0'
        mutations=[
            lambda x:x['targets'][target].pop('Microsoft.Extensions.Logging.Abstractions/10.0.12'),
            lambda x:x['targets'][target][server]['dependencies'].update({'Unexpected.Package':'1.0'}),
            lambda x:x['targets'][target][server]['dependencies'].update({'Microsoft.Extensions.Logging.Abstractions':'10.0.14'}),
            lambda x:x['targets'][target][server].update(native={'native.so':{}}),
            lambda x:x['targets'][target][server].update(runtimeTargets={'win/native.dll':{}}),
            lambda x:x['targets'][target][server].update(resources={'en/a.dll':{}}),
            lambda x:x['targets'][target][server].update(runtime={'../SharpLink.Server.dll':{}}),
            lambda x:x['targets'][target][server].update(runtime={'/SharpLink.Server.dll':{}}),
            lambda x:x['targets'][target][server].update(runtime={'Wrong.dll':{}}),
            lambda x:x['libraries'][server].update(type='package'),
            lambda x:x['runtimeTarget'].update(name='.NETCoreApp,Version=v10.0/linux-x64')]
        for change in mutations:
            bad=copy.deepcopy(d);change(bad)
            with self.assertRaises(v.GateError):v.runtime_closure(bad)
        # An unreferenced test package is not admitted into the product runtime closure.
        d['targets'][target]['TUnit/1.0']={'runtime':{'TUnit.dll':{}}}
        self.assertEqual(set(v.runtime_closure(d)),v.CLOSURE_NAMES)

    def test_closure_hash_mismatch_missing_and_symlink_fail_with_bounded_diagnostics(self):
        with tempfile.TemporaryDirectory() as p:
            root=Path(p);f=fake_frozen(root);v.assert_closure(f['directory'],f['hashes'],'control')
            name='SharpLink.Server.dll';path=f['directory']/name;path.write_text('changed')
            capture=io.StringIO()
            with contextlib.redirect_stdout(capture),self.assertRaises(v.GateError):v.assert_closure(f['directory'],f['hashes'],'control')
            row=json.loads(capture.getvalue().split('PIN CLOSURE MISMATCH ',1)[1])
            self.assertEqual(set(row),{'stage','assembly','expected_sha256','actual_sha256'})
            self.assertEqual(row['assembly'],name);self.assertNotIn(p,capture.getvalue());self.assertLess(len(capture.getvalue()),400)
            path.unlink()
            with contextlib.redirect_stdout(io.StringIO()),self.assertRaises(v.GateError):v.assert_closure(f['directory'],f['hashes'],'control')
            alternate=root/'alternate';alternate.write_text(name);path.symlink_to(alternate)
            with contextlib.redirect_stdout(io.StringIO()),self.assertRaises(v.GateError):v.assert_closure(f['directory'],f['hashes'],'control')

    def test_freeze_binds_tested_deps_generator_and_every_dependency_byte(self):
        with tempfile.TemporaryDirectory() as p:
            root=Path(p);binary=root/Path(v.unit.ASSEMBLY).parent;binary.mkdir(parents=True)
            for name in v.CLOSURE_NAMES:(binary/name).write_text(name)
            deps=binary/'SharpLink.UnitTests.deps.json';deps.write_text(json.dumps(closure_deps()))
            generator=root/'src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll';generator.parent.mkdir(parents=True);generator.write_text('own-generator')
            identity=v.snapshot_build_identity(root);f=v.freeze_closure(root,identity,root/'frozen')
            self.assertEqual(f['hashes'],{name:identity[name] for name in v.CLOSURE_NAMES})
            for path in (deps,generator,binary/'Microsoft.Extensions.Logging.Abstractions.dll'):
                original=path.read_bytes();path.write_bytes(original+b'changed')
                with contextlib.redirect_stdout(io.StringIO()),self.assertRaises(v.GateError):v.freeze_closure(root,identity,root/('bad-'+path.name))
                path.write_bytes(original)
            v.assert_closure(f['directory'],f['hashes'],'still-frozen')

    def test_fixture_compiler_refs_and_analyzer_must_be_exact_frozen_paths(self):
        with tempfile.TemporaryDirectory() as p:
            root=Path(p);f=fake_frozen(root);fixture=root/'fixture'
            for project in ('module','worker'):
                directory=fixture/project/'obj';directory.mkdir(parents=True)
                (directory/'references.txt').write_text('\n'.join(str(f['directory']/name) for name in sorted(v.CLOSURE_NAMES)))
            analyzer=fixture/'module/obj/analyzers.txt';analyzer.write_text(str(f['generator']))
            v.verify_fixture_references(fixture,f)
            refs=fixture/'worker/obj/references.txt';original=refs.read_text()
            foreign=root/'foreign';foreign.mkdir();(foreign/'SharpLink.Server.dll').write_text('SharpLink.Server.dll')
            for altered in (original.replace(str(f['directory']/'SharpLink.Server.dll'),str(foreign/'SharpLink.Server.dll')),original+'\n'+str(f['directory']/'SharpLink.Server.dll')):
                refs.write_text(altered)
                with self.assertRaises(v.GateError):v.verify_fixture_references(fixture,f)
            refs.write_text(original);analyzer.write_text(str(root/'other/SharpLink.Generator.dll'))
            with self.assertRaises(v.GateError):v.verify_fixture_references(fixture,f)

    def test_fixture_runtimeconfig_is_core_only_like_tested_unit(self):
        with tempfile.TemporaryDirectory() as p:
            path=Path(p)/'worker.runtimeconfig.json'
            good={'runtimeOptions':{'tfm':'net10.0','framework':{'name':'Microsoft.NETCore.App','version':'10.0.0'}}}
            path.write_text(json.dumps(good));v.verify_fixture_runtimeconfig(path)
            for mutate in (lambda o:o.update(frameworks=[{'name':'Microsoft.AspNetCore.App','version':'10.0.0'}]),lambda o:o['framework'].update(name='Microsoft.AspNetCore.App'),lambda o:o['framework'].update(version='10.0.14'),lambda o:o.update(includedFrameworks=[]),lambda o:o.update(additionalProbingPaths=['/foreign'])):
                bad=copy.deepcopy(good);mutate(bad['runtimeOptions']);path.write_text(json.dumps(bad))
                with self.assertRaises(v.GateError):v.verify_fixture_runtimeconfig(path)

    def test_fixture_deps_cannot_prune_duplicate_or_redirect_frozen_runtime_assets(self):
        with tempfile.TemporaryDirectory() as p:
            path=Path(p)/'fixture.deps.json';target='.NETCoreApp,Version=v10.0'
            graph={name[:-4]+'/1.0':{'runtime':{name:{}}} for name in v.CLOSURE_NAMES}
            good=dict(runtimeTarget={'name':target},targets={target:graph});path.write_text(json.dumps(good));v.verify_fixture_deps(path)
            for mutate in (lambda g:g.pop('SharpLink.Server/1.0'),lambda g:g.update(duplicate={'runtime':{'SharpLink.Server.dll':{}}}),lambda g:g['SharpLink.Server/1.0'].update(runtime={'lib/net10.0/SharpLink.Server.dll':{}}),lambda g:g['SharpLink.Server/1.0'].update(native={'native.so':{}})):
                bad=copy.deepcopy(good);mutate(bad['targets'][target]);path.write_text(json.dumps(bad))
                with self.assertRaises(v.GateError):v.verify_fixture_deps(path)

    def test_fixture_cannot_rebuild_products_or_swap_own_generator(self):
        for name in ('module/Module.csproj','worker/Worker.csproj'):
            root=ET.fromstring((HERE/'fixture'/name).read_text())
            project_refs=[e.attrib['Include'] for e in root.iter('ProjectReference')]
            self.assertEqual(project_refs,[] if name.startswith('module') else ['../module/Module.csproj'])
            text=(HERE/'fixture'/name).read_text()
            self.assertIn('$(FrozenProductBin)',text)
            self.assertNotIn('$(ProductRoot)',text)
            self.assertIn('references.txt',text)
        module=(HERE/'fixture/module/Module.csproj').read_text()
        self.assertIn('$(FrozenGenerator)',module)
        self.assertTrue(list(ET.fromstring(module).iter('Analyzer')))
        worker=(HERE/'fixture/worker/Worker.csproj').read_text()
        self.assertFalse(list(ET.fromstring(worker).iter('FrameworkReference')))
        self.assertNotIn('WebApplication',(HERE/'fixture/worker/Program.cs').read_text())

    def test_loaded_identity_requires_private_framework_and_own_arm_complete_closure(self):
        with tempfile.TemporaryDirectory() as path:
            base=Path(path);binary=base/'bin';binary.mkdir();runtime=base/'runtime';runtime.mkdir();host=runtime/'dotnet';host.write_text('host')
            core=runtime/'shared/Microsoft.NETCore.App/10.0.14';asp=runtime/'shared/Microsoft.AspNetCore.App/10.0.14';core.mkdir(parents=True);asp.mkdir(parents=True)
            (core/'System.Private.CoreLib.dll').write_text('core');(asp/'Microsoft.AspNetCore.dll').write_text('asp')
            names=['SharpLink.Runtime','SharpLink.Server','SharpLink.Abstractions','SharpLink.Benchmarks','PinStreamModule']
            for name in names:(binary/(name+'.dll')).write_text(name)
            hashes=v.hashes(binary);rows=[]
            for name in names:
                rows.append(dict(name=name,location=str(binary/(name+'.dll')),mvid='00000000-0000-0000-0000-000000000001',sha256=hashes[name+'.dll']))
            d=dict(binaryHashes=hashes,frameworkLocations=[str(core/'System.Private.CoreLib.dll')],loadedAssemblies=rows,loadedAssemblyCount=len(rows))
            v.validate_loaded_identity(d,binary,hashes,str(host))
            package='Microsoft.Extensions.Logging.Abstractions';package_file=core/(package+'.dll');package_file.write_text('package')
            extra=dict(name=package,location=str(package_file),mvid='00000000-0000-0000-0000-000000000001',sha256=v.file_sha(package_file))
            bad=copy.deepcopy(d);bad['loadedAssemblies'].append(extra);bad['loadedAssemblyCount']+=1
            with self.assertRaises(v.GateError):v.validate_loaded_identity(bad,binary,hashes,str(host),{package+'.dll':extra['sha256']})
            for modify in (lambda x:x['loadedAssemblies'].pop(),lambda x:x['frameworkLocations'].append(str(asp/'Microsoft.AspNetCore.dll')),lambda x:x['loadedAssemblies'][0].update(sha256='0'*64)):
                bad=copy.deepcopy(d);modify(bad)
                with self.assertRaises(v.GateError):v.validate_loaded_identity(bad,binary,hashes,str(host))

    def test_release_valid_local_and_published_ancestry(self):
        for base in v.BASE_PARENTS:
            with self.subTest(base=base),tempfile.TemporaryDirectory() as p:
                root=Path(p);here,r,release,replies=provenance_fixture(root,base)
                with patch.object(v,'HERE',here),patch.object(v,'git',side_effect=lambda root,*args:replies[args]):
                    self.assertEqual(v.verify_release(root,release,v.BRANCH),r)

    def test_release_rejects_all_ancestry_edges_and_dirty_state(self):
        with tempfile.TemporaryDirectory() as p:
            root=Path(p);here,r,release,valid=provenance_fixture(root)
            keys=[('rev-parse','HEAD'),('show','-s','--format=%P',r['baseline_sha']),('show','-s','--format=%P','HEAD'),('show','-s','--format=%P',r['tools_sha']),('show','-s','--format=%P',r['candidate_sha']),('status','--porcelain','--untracked-files=all')]
            for key in keys:
                with self.subTest(edge=key):
                    replies=valid.copy();replies[key]='a'*40
                    with patch.object(v,'HERE',here),patch.object(v,'git',side_effect=lambda root,*args:replies[args]):
                        with self.assertRaises(v.GateError):v.verify_release(root,release,v.BRANCH)
            with patch.object(v,'HERE',here),patch.object(v,'git',side_effect=lambda root,*args:valid[args]):
                with self.assertRaises(v.GateError):v.verify_release(root,release,'refs/heads/main')

    def test_release_rejects_source_tool_trees_and_every_diff_boundary(self):
        with tempfile.TemporaryDirectory() as p:
            root=Path(p);here,r,release,valid=provenance_fixture(root)
            keys=[('rev-parse',r['tools_sha']+'^{tree}'),('rev-parse',r['baseline_sha']+'^{tree}'),('rev-parse',r['candidate_sha']+'^{tree}'),('diff','--name-status','--no-renames',r['tools_sha'],'HEAD'),('diff','--name-status','--no-renames',r['baseline_sha'],r['candidate_sha']),('diff','--name-status','--no-renames',r['candidate_sha'],r['tools_sha'])]
            for key in keys:
                with self.subTest(boundary=key):
                    replies=valid.copy();replies[key]='a'*40
                    with patch.object(v,'HERE',here),patch.object(v,'git',side_effect=lambda root,*args:replies[args]):
                        with self.assertRaises(v.GateError):v.verify_release(root,release,v.BRANCH)

    def test_release_rejects_request_and_tool_manifest_substitution(self):
        with tempfile.TemporaryDirectory() as p:
            root=Path(p);here,r,release,replies=provenance_fixture(root)
            with patch.object(v,'HERE',here),patch.object(v,'git',side_effect=lambda root,*args:replies[args]):
                for field,value in [('schema_version',True),('baseline_sha','a'*40),('candidate_sha','not-a-sha'),('extra','unreviewed')]:
                    bad=r.copy();bad[field]=value;(root/v.REQUEST).write_text(json.dumps(bad))
                    with self.subTest(field=field),self.assertRaises(v.GateError):v.verify_release(root,release,v.BRANCH)
                (root/v.REQUEST).write_text(json.dumps(r))
                for manifest in ([],['../outside'],['/absolute'],['same','same'],['eng/extra-unreviewed.py']):
                    (here/'tool-files.json').write_text(json.dumps(manifest))
                    with self.subTest(manifest=manifest),self.assertRaises(v.GateError):v.verify_release(root,release,v.BRANCH)

    def test_each_arm_source_identity_tree_parent_and_cleanliness_fail_closed(self):
        with tempfile.TemporaryDirectory() as p:
            root=Path(p);here,r,release,_=provenance_fixture(root)
            for arm in ('baseline','candidate'):
                valid={('rev-parse','HEAD'):r[arm+'_sha'],('rev-parse','HEAD^{tree}'):v.BASE_TREE if arm=='baseline' else v.SOURCE_TREE,('show','-s','--format=%P','HEAD'):v.BASE_PARENTS[r['baseline_sha']] if arm=='baseline' else r['baseline_sha'],('status','--porcelain','--untracked-files=all'):''}
                with patch.object(v,'git',side_effect=lambda root,*args:valid[args]):v.verify_source(root,r,arm)
                for key in valid:
                    bad=valid.copy();bad[key]='a'*40
                    with self.subTest(arm=arm,field=key),patch.object(v,'git',side_effect=lambda root,*args:bad[args]):
                        with self.assertRaises(v.GateError):v.verify_source(root,r,arm)

    def test_bounded_measurement_read_preserves_failed_and_oversized_inputs(self):
        with tempfile.TemporaryDirectory() as p:
            path=Path(p)/'measurement.json';path.write_text(json.dumps(sample()))
            self.assertEqual(v.read_measurement(path)['status'],'pass')
            bad=sample();bad['status']='fail';path.write_text(json.dumps(bad))
            d=v.read_measurement(path);self.rejects(d);self.assertTrue(path.exists())
            path.write_bytes(b' ' * 131073)
            with self.assertRaises(v.GateError):v.read_measurement(path)
            self.assertEqual(path.stat().st_size,131073)
            missing=Path(p)/'missing'
            with self.assertRaises(v.GateError):v.read_measurement(missing)


if __name__=='__main__':unittest.main(verbosity=2)
