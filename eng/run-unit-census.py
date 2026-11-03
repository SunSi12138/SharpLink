#!/usr/bin/env python3
"""One original Release build; two metadata-only discovery subprocesses.

No raw logs/ledger uploads. Guarded bootstrap includes imports. The subprocess
owner is the unchanged reviewed Linux helper, not a timeout-only wrapper.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import time

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def module(name, path):
    spec=importlib.util.spec_from_file_location(name,path)
    obj=importlib.util.module_from_spec(spec); spec.loader.exec_module(obj); return obj


def exception_graph(errors):
    """Independent bootstrap graph; preserve identity and occurrence separately."""
    nodes=[]; refs={}; queue=[]; formatting=set()
    def ref(error):
        if id(error) not in refs:
            refs[id(error)]=len(nodes);nodes.append(None);queue.append(error)
        return refs[id(error)]
    roots=[ref(e) for e in errors]
    index=0
    while index<len(queue):
        error=queue[index];index+=1; frames=[];tb=error.__traceback__
        while tb is not None:
            frames.append({'file':tb.tb_frame.f_code.co_filename,'function':tb.tb_frame.f_code.co_name,'line':tb.tb_lineno,'instruction':tb.tb_lasti});tb=tb.tb_next
        node={'type':type(error).__module__+'.'+type(error).__qualname__, 'traceback':frames,
              'cause':ref(error.__cause__) if error.__cause__ is not None else None,
              'context':ref(error.__context__) if error.__context__ is not None else None,
              'suppressContext':error.__suppress_context__}
        if id(error) not in formatting:
            try:node['message']=str(error)
            except BaseException as failure:
                formatting.add(id(failure));errors.append(failure);node['messageFailure']=ref(failure)
        else:node['messageUnavailable']='formatter failure not recursively formatted'
        node['args']=[x if type(x) in (str,int,float,bool,type(None)) else {'unrenderedType':type(x).__qualname__} for x in error.args]
        if isinstance(error,BaseExceptionGroup):node['members']=[ref(e) for e in error.exceptions]
        if hasattr(error,'causes'):node['customCauses']=[ref(e) for e in error.causes]
        if hasattr(error,'outcome'):node['originalOwnerReceipt']=error.outcome
        nodes[refs[id(error)]]=node
    return {'orderedRootOccurrences':[ref(e) for e in errors],'nodes':nodes}


def persist(directory, errors, serial):
    # Two finite attempts; diagnostics failures never overwrite the original root.
    for attempt in range(2):
        try:
            payload=json.dumps(exception_graph(errors),ensure_ascii=True,allow_nan=False,indent=2).encode()
            fd=os.open(directory,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
            try:
                out=os.open('failure-%d-%d.json'%(serial,attempt),os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600,dir_fd=fd)
                with os.fdopen(out,'wb') as stream:
                    stream.write(payload);stream.flush();os.fsync(stream.fileno())
                os.fsync(fd)
            finally:os.close(fd)
            return True
        except BaseException as error:errors.append(error)
    return False


def environment(work):
    keep=('PATH','DOTNET_ROOT','DOTNET_ROOT_X64','LANG','LC_ALL','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY','http_proxy','https_proxy','all_proxy','no_proxy','SSL_CERT_FILE','SSL_CERT_DIR')
    env={k:os.environ[k] for k in keep if k in os.environ}
    for name in ('home','cli','packages','http','plugins','tmp'):(work/name).mkdir()
    env.update(HOME=str(work/'home'),DOTNET_CLI_HOME=str(work/'cli'),NUGET_PACKAGES=str(work/'packages'),NUGET_HTTP_CACHE_PATH=str(work/'http'),NUGET_PLUGINS_CACHE_PATH=str(work/'plugins'),TMPDIR=str(work/'tmp'),
               DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_NOLOGO='1',DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',DOTNET_MULTILEVEL_LOOKUP='0',MSBUILDDISABLENODEREUSE='1',DOTNET_CLI_USE_MSBUILD_SERVER='0',CI='true',
               GIT_CONFIG_GLOBAL='/dev/null',GIT_CONFIG_SYSTEM='/dev/null',GIT_ALLOW_PROTOCOL='file',GIT_NO_LAZY_FETCH='1',GIT_TERMINAL_PROMPT='0',TUNIT_DISABLE_ARTIFACT_UPLOAD='1',TUNIT_DISABLE_GITHUB_REPORTER='1',TUNIT_AGGREGATE_REPORTS='off',TUNIT_DISABLE_LOGO='1')
    return env


class Lane:
    def __init__(self, work, results, owner):
        self.work=work; self.r=results; self.owner=owner
        self.logs=work/'logs';self.logs.mkdir();self.evidence=work/'evidence';self.evidence.mkdir()
        self.logseal=results.snapshot(self.logs);self.seals={};self.evidenceseal=results.snapshot(self.evidence)
        self.work_id=results.legacy.directory_identity_strict(work)
        self.env=environment(work);self.stop=time.monotonic()+3600;self.sequence=0
        self.source=None;self.original={};self.inputs={};self.trees={};self.commands={}
        self.observations=[];self.errors=[];self.stage='bootstrap';self.durable=True;self.validation_phase='not-metadata';self.metadata_input=None;self.metadata_project=None;self.metadata_catalog=None
        self.runtime_inventory={};self.identity={};self.unfiltered=False;self.stable=False;self.source_members=None

    def record(self, error):
        self.errors.append(error)
        self.durable=persist(self.work,self.errors,len(self.errors)) and self.durable
        for path in self.work.glob('failure-*.json'):
            if path not in self.inputs:
                try:self.inputs[path]=self.r.file_identity(path)
                except BaseException as diagnostic:self.errors.append(diagnostic);self.durable=False

    def source_files(self):
        return {p.relative_to(self.source).as_posix() for p in self.source.rglob('*')
                if not any(part in ('.git','obj','bin') for part in p.relative_to(self.source).parts) and not p.is_dir()}

    def check(self):
        failures=[]
        actions=[lambda:self.r.require(time.monotonic()<self.stop,'UC_TOTAL_BUDGET'),
                 lambda:self.r.legacy.verify_directory(self.work,self.work_id),
                 lambda:self.r.verify_snapshot(self.logs,self.logseal),
                 lambda:self.r.verify_snapshot(self.evidence,self.evidenceseal)]
        if self.source_members is not None:
            actions.append(lambda:self.r.require(self.source_files()==self.source_members,'UC_SOURCE_MEMBERS'))
        actions += [lambda p=p,v=v:self.r.verify_snapshot(p,v) for p,v in list(self.seals.items())+list(self.trees.items())]
        actions += [lambda p=p,v=v:self.r.require(self.r.file_identity(p)==v,'UC_INPUT_CHANGED') for p,v in list(self.original.items())+list(self.inputs.items())]
        for action in actions:
            try:action()
            except BaseException as error:failures.append(error)
        try:self.r.require(time.monotonic()<self.stop,'UC_FINAL_BUDGET')
        except BaseException as error:failures.append(error)
        if failures:raise BaseExceptionGroup('independent census guards',failures)

    def extend_evidence(self,expected_new):
        current=self.r.snapshot(self.evidence)
        self.r.require(set(current)==set(self.evidenceseal)|set(expected_new) and
            all(current[k]==v for k,v in self.evidenceseal.items()),'UC_EVIDENCE_MEMBERS')
        self.evidenceseal=current

    def run(self,label,argv,cwd=None,seconds=20):
        self.stage=label;self.check()
        actual=self.r.budget(self.stop-time.monotonic(),seconds,self.sequence);self.sequence+=1
        pipe=self.work/('p'+str(self.sequence));pipe.mkdir();pipeid=self.r.legacy.directory_identity_strict(pipe)
        child=dict(self.env,TESTINGPLATFORM_PIPE_DIRECTORY=str(pipe));args=[str(x) for x in argv]
        if label in ('discovery1','discovery2'):
            self.r.require(self.unfiltered,'UC_PROVIDER_UNPROVEN')
            self.r.discovery_inputs(args,child)
        state=self.commands[label]={'argv':args,'seconds':actual,'receipt':None,'ownerProvenance':None}
        failures=[];output=None
        try:
            output=self.owner.run_owned(label,args,cwd or ROOT,child,self.logs,actual)
            receipt=self.r.read_json(self.logs/(label+'.json'))
            state.update(receipt=receipt,ownerProvenance='validated-original-success')
            self.r.legacy.verify_receipt(receipt,label,args,actual)
        except BaseException as error:
            failures.append(error);self.record(error)
            if isinstance(error,self.owner.CommandFailure):state.update(receipt=error.outcome,ownerProvenance='original-owner-failure')
        try:
            self.r.legacy.verify_directory(pipe,pipeid,empty=True)
            current=self.r.snapshot(self.logs)
            expected=set(self.logseal)|{label+'.stdout',label+'.stderr',label+'.json'}
            self.r.require(set(current)==expected and all(current[k]==v for k,v in self.logseal.items()),'UC_COMPLETED_LOG_MUTATION')
            self.logseal=current
        except BaseException as error:failures.append(error);self.record(error)
        try:self.check()
        except BaseException as error:failures.append(error);self.record(error)
        if failures:raise BaseExceptionGroup('owned command and independent guards',failures)
        print(json.dumps({'stage':label,'exit':0,'owned':'joined'}),flush=True)
        return output[0]

    def git(self,label,*args,cwd=None):
        return self.r.stable_read(self.run(label,['git','-c','core.hooksPath=/dev/null',*args],cwd,30))[0].decode().strip()

    def ledger(self,number,raw,catalog):
        self.check()
        result=self.r.classify(self.r.read_json(raw),catalog)
        self.check()
        directory=self.evidence/('ledger'+str(number));directory.mkdir()
        self.r.write_json(directory,'ledger.json',{'identity':self.identity,'rawJSONSHA256':self.r.sha(raw),'actual':result['rows']})
        self.seals[directory]=self.r.snapshot(directory)
        self.extend_evidence(['ledger'+str(number),'ledger'+str(number)+'/ledger.json'])
        self.observations.append(result['summary'])
        self.check()
        return result


def execute(lane):
    r=lane.r;catalog=r.read_json(ROOT/'eng/unit-census.json')
    r.require(catalog.get('packageCompileProof')==r.package_compile_proof(),'UC_PACKAGE_COMPILE_BYTES')
    os_release=dict(line.split('=',1) for line in r.stable_read(Path('/etc/os-release').resolve())[0].decode().splitlines() if '=' in line and not line.startswith('#'))
    r.require(sys.platform=='linux' and os_release.get('ID','').strip('\"')=='ubuntu' and os_release.get('VERSION_ID','').strip('\"')=='24.04','UC_OS')
    r.require(sys.argv[1:]==['--configuration','Release'],'UC_ARGUMENTS')
    head=lane.git('head','rev-parse','HEAD');tree=lane.git('tree','rev-parse','HEAD^{tree}')
    r.require(re.fullmatch('[0-9a-f]{40}',head) and re.fullmatch('[0-9a-f]{40}',tree),'UC_GIT_ID')
    r.require(lane.git('clean','status','--porcelain','--untracked-files=all')=='','UC_DIRTY')
    tracked=r.stable_read(lane.run('tracked',['git','ls-files','-z'],seconds=30))[0].decode().split('\0')[:-1]
    r.require(tracked and len(tracked)==len(set(tracked)),'UC_TRACKED')
    lane.original={ROOT/p:r.file_identity(ROOT/p) for p in tracked}
    source=lane.work/'src';lane.source=source
    lane.run('clone',['git','-c','core.hooksPath=/dev/null','clone','--no-local','--depth=1','--no-checkout',ROOT.as_uri(),source],seconds=120)
    lane.git('checkout','checkout','--detach',head,cwd=source)
    r.require(lane.git('clone-head','rev-parse','HEAD',cwd=source)==head,'UC_CLONE_HEAD')
    for name in tracked:
        r.require(r.sha(source/name)==lane.original[ROOT/name][-1],'UC_CLONE_BYTES')
        lane.original[source/name]=r.file_identity(source/name)
    lane.source_members=set(tracked)
    # clone has .git directory; all other nongenerated members are exact tracked input.
    lane.check()
    for ancestor in source.parents:
        for name in ('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.Config','nuget.config','global.json'):
            r.require(not (ancestor/name).exists(),'UC_ANCESTOR_OVERRIDE')
    r.verify_catalog(source,catalog)
    lane.run('python-controls',[sys.executable,'-B',source/'eng/tests/test_unit_census.py'],source,30)
    dotnet=Path(shutil.which('dotnet',path=lane.env['PATH']) or '').resolve()
    r.require(dotnet.is_file(),'UC_SDK_MISSING')
    sdkroot=dotnet.parent
    lane.inputs[dotnet]=r.file_identity(dotnet)
    lane.trees[sdkroot]=r.snapshot(sdkroot)
    sdk=r.stable_read(lane.run('sdk-version',[dotnet,'--version'],source))[0].decode().strip()
    r.require(re.fullmatch(r'10\.0\.1\d\d',sdk) and int(sdk.rsplit('.',1)[1])>=102,'UC_SDK_VERSION')
    lane.run('sdk-info',[dotnet,'--info'],source)
    runtime_text=r.stable_read(lane.run('sdk-runtimes',[dotnet,'--list-runtimes'],source))[0].decode()
    r.require(re.search(r'^Microsoft.NETCore.App 10\.0\.\d+ ',runtime_text,re.M),'UC_RUNTIME_VERSION')
    runtime_rows=[]
    for line in runtime_text.splitlines():
        match=re.fullmatch(r'(Microsoft\.NETCore\.App|Microsoft\.AspNetCore\.App) (\d+\.\d+\.\d+) \[(.+)\]',line)
        r.require(match is not None and Path(match[3])==sdkroot/'shared'/match[1],'UC_RUNTIME_LOCATION')
        runtime_rows.append({'framework':match[1],'version':match[2]})
    # Initial SDK/executable seal was established before the first SDK command.
    config=lane.work/'NuGet.Config'
    config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>\n')
    lane.inputs[config]=r.file_identity(config)
    project=source/catalog['project'];packages=lane.work/'packages'
    package_identity=r.legacy.directory_identity_strict(packages)
    lane.run('restore',[dotnet,'restore',project,'--configfile',config,'--disable-parallel','-p:Configuration=Release','-p:RestoreIgnoreFailedSources=false','-m:1','-nr:false'],source,600)
    assets=r.dependencies(source,packages,catalog,package_identity)
    lane.trees[packages]=r.snapshot(packages)
    for p in source.rglob('project.assets.json'):lane.inputs[p]=r.file_identity(p)
    metadata={};package_sources={}
    for index,name in enumerate(catalog['projects']):
        lane.validation_phase='metadata-command';lane.metadata_input=None;lane.metadata_project=name;lane.metadata_catalog=catalog
        raw=lane.run('metadata'+str(index),[dotnet,'msbuild',source/name,'-p:Configuration=Release','-getItem:Compile,ProjectReference,TestingPlatformBuilderHook,TestingPlatformCommandLineOptionDefault','-getProperty:MSBuildProjectFullPath,Configuration,PublishAot,TargetFramework,AssemblyName,DefineConstants,MSBuildAllProjects,TargetPath,RootNamespace,TestingPlatformHostFactory','-m:1','-nr:false'],source,20)
        lane.validation_phase='metadata-json'
        value=r.read_json(raw);metadata[name]=value
        lane.metadata_input=value;lane.metadata_project=name;lane.metadata_catalog=catalog
        lane.validation_phase='project-properties'
        r.project_metadata(source,name,value,catalog)
        lane.validation_phase='compile-set'
        compile_paths={Path(item['FullPath']) for item in value['Items']['Compile']}
        if name==catalog['project']:
            ordinary_compile={p.relative_to(source).as_posix() for p in compile_paths if p.is_relative_to(source) and 'obj' not in p.relative_to(source).parts}
            r.require(ordinary_compile==set(catalog['unitSources'])|set(catalog['linkedSources']),'UC_COMPILE_SET')
        lane.validation_phase='project-reference-membership'
        for item in value['Items'].get('ProjectReference',[]):
            r.require(Path(item['FullPath']) in {source/p for p in catalog['projects']},'UC_PROJECT_REFERENCE')
        if name==catalog['project']:
            lane.validation_phase='package-compile-input'
            package_sources=r.package_compile_inputs(source,packages,value)
        lane.validation_phase='compile-paths'
        for item in value['Items']['Compile']:
            p=Path(item['FullPath'])
            r.require(p.is_absolute() and '..' not in p.parts and p.resolve()==p,'UC_COMPILE_PATH')
            r.require(p in package_sources and name==catalog['project'] or p.is_relative_to(source) and (p in lane.original or 'obj' in p.relative_to(source).parts),'UC_COMPILE_FOREIGN')
        lane.validation_phase='import-paths'
        for imported in value['Properties']['MSBuildAllProjects'].split(';'):
            if imported:
                p=Path(imported)
                r.require(p.is_absolute() and '..' not in p.parts and p.resolve()==p,'UC_IMPORT_PATH')
                r.require(p in lane.original or p.is_relative_to(packages) or p.is_relative_to(sdkroot) or p.is_relative_to(source) and 'obj' in p.parts,'UC_IMPORT_FOREIGN')
                lane.inputs[p]=r.file_identity(p)
    lane.validation_phase='metadata-complete'
    expanded_dir=lane.work/'imports-pending';expanded_dir.mkdir();expanded=expanded_dir/'unit.xml'
    lane.run('expanded-imports',[dotnet,'msbuild',project,'-p:Configuration=Release','-p:UseSharedCompilation=false',
        '-p:PathMap='+r.legacy.compile_path_map(source),'-preprocess:'+str(expanded),'-m:1','-nr:false'],source,20)
    known=dict(lane.original)
    for container in (source,packages,sdkroot):
        for path in container.rglob('*'):
            if '.git' in path.relative_to(container).parts:continue
            if path.is_dir():
                r.legacy.directory_identity_strict(path);continue
            known[path]=r.file_identity(path)
    actual_imports=r.import_origins(expanded,source,packages,sdkroot,known)
    r.package_compile_inputs(source,packages,metadata[catalog['project']],actual_imports)
    lane.inputs.update(actual_imports)
    r.verify_snapshot(lane.evidence,lane.evidenceseal)
    expanded_dir.rename(lane.evidence/'imports');expanded_dir=lane.evidence/'imports'
    lane.seals[expanded_dir]=r.snapshot(expanded_dir);lane.extend_evidence(['imports','imports/unit.xml'])
    built_metadata=lane.run('build',[dotnet,'build',project,'-c','Release','--no-restore','-v:minimal','-target:Build',
        '-getItem:Compile,Using,AssemblyMetadata,ProjectReference,TestingPlatformBuilderHook,TestingPlatformCommandLineOptionDefault',
        '-getProperty:MSBuildProjectFullPath,Configuration,PublishAot,TargetFramework,AssemblyName,DefineConstants,TargetPath,RootNamespace,TestingPlatformHostFactory,GenerateTestingPlatformEntryPoint,GenerateTestingPlatformApplicationHelper,GenerateSelfRegisteredExtensions,Language',
        '-p:UseSharedCompilation=false','-p:PathMap='+r.legacy.compile_path_map(source),'-m:1','-nr:false'],source,600)
    lane.validation_phase='post-build-json';lane.metadata_input=None
    post_build=r.read_json(built_metadata)
    lane.metadata_input=post_build;lane.validation_phase='post-build-properties'
    r.project_metadata(source,catalog['project'],post_build,catalog)
    lane.validation_phase='post-build-package-compile'
    post_package_sources=r.package_compile_inputs(source,packages,post_build,actual_imports)
    r.require(post_package_sources==package_sources,'UC_PACKAGE_COMPILE_SET')
    lane.validation_phase='post-build-assembly-metadata'
    r.assembly_metadata_qualification(packages,sdkroot,post_build,actual_imports,catalog)
    lane.validation_phase='post-build-compile-set'
    compile_ledger=r.compiled_inputs(source,post_build,catalog,post_package_sources)
    lane.validation_phase='post-build-global-usings'
    r.global_using_qualification(source,packages,sdkroot,post_build,actual_imports,catalog)
    r.write_json(lane.evidence,'compiled-inputs.json',compile_ledger);lane.extend_evidence(['compiled-inputs.json'])
    lane.validation_phase='runtime-identity'
    output=r.runtime_identity(source,packages,catalog,assets,lane.runtime_inventory)
    # Seal every original graph output and generated input after the single build.
    for name in catalog['projects']:
        for sub in ('obj','bin'):
            p=(source/name).parent/sub
            if p.exists():lane.trees[p]=r.snapshot(p)
    proof=r.filter_qualification(source,packages,post_build,catalog,lane.env,actual_imports)
    lane.unfiltered=proof['unfilteredQualified']
    dll=output/'SharpLink.UnitTests.dll'
    lane.identity={'head':head,'tree':tree,'configuration':'Release','os':'ubuntu-24.04','sdk':sdk,'installedRuntimes':runtime_rows,'runtimeRequest':'Microsoft.NETCore.App/10.0.0','tunit':'1.73.19','mtp':'2.5.1',
                   'binarySHA256':r.sha(dll),'sourceManifestSHA256':r.digest({str(p.relative_to(source)):v[-1] for p,v in lane.original.items() if p.is_relative_to(source)}),
                   'sdkManifestSHA256':r.digest(lane.trees[sdkroot]),'packageManifestSHA256':r.digest(lane.trees[packages]),'providerProofSHA256':proof['proofSHA256']}
    first=lane.ledger(1,lane.run('discovery1',[dotnet,dll,'--list-tests','json'],source,180),catalog)
    second=lane.ledger(2,lane.run('discovery2',[dotnet,dll,'--list-tests','json'],source,180),catalog)
    lane.stable=r.stable(first,second)


def guarded_main():
    work=None;errors=[];lane=None;durable=False
    try:
        work=Path(tempfile.mkdtemp(prefix='sluc-',dir='/tmp'));os.chmod(work,0o700)
        r=module('unit_census_results',ROOT/'eng/unit-census-results.py')
        owner=module('unit_census_owner',ROOT/'eng/package-smoke-process.py')
        owner.enable_subreaper()
        lane=Lane(work,r,owner)
        try:execute(lane)
        except BaseException as error:lane.record(error)
        try:lane.check()
        except BaseException as error:lane.record(error)
        errors=lane.errors
        qualified=not errors and lane.stable and lane.unfiltered and lane.durable and time.monotonic()<lane.stop
        metadata_facts=None
        if lane.metadata_input is not None:
            try:metadata_facts=r.metadata_facts(lane.source,lane.metadata_project,lane.metadata_input,lane.metadata_catalog)
            except BaseException as diagnostic:
                lane.record(diagnostic);qualified=False
        summary={'schemaVersion':1,'identity':lane.identity,'censusQualified':qualified,'stableQualified':qualified,
                 'unfilteredQualified':lane.unfiltered and not errors,'evidenceDurable':lane.durable,'stage':lane.stage,
                 'errorOccurrences':len(errors),'rejections':r.public_rejections(errors),'validationPhase':lane.validation_phase,'metadataFacts':metadata_facts,'runtimeInventory':lane.runtime_inventory,'rejectCode':None if qualified else 'UC_FAILED','observations':lane.observations,'exit':0 if qualified else 1,
                 'qualification':'Metadata only; no test bodies/results. Digests cannot reconstruct private ledgers.'}
        # Summary serialization/write/render are independent failure points.
        try:
            r.write_json(lane.evidence,'summary.json',summary)
            lane.extend_evidence(['summary.json'])
            lane.check()
            rendered=json.dumps(summary,sort_keys=True,allow_nan=False)
            r.require(time.monotonic()<lane.stop,'UC_SUMMARY_BUDGET')
            print(rendered,flush=True)
            r.require(time.monotonic()<lane.stop,'UC_EMIT_BUDGET')
        except BaseException as error:
            lane.record(error)
            os.write(2,b'UNIT_CENSUS_FAILED_SUMMARY\n')
            return 1
        return 0 if qualified else 1
    except BaseException as error:
        errors.append(error);guarded_main.last_errors=errors
        durable=persist(work,errors,999) if work is not None else False
        try:os.write(2,b'UNIT_CENSUS_FAILED_BOOTSTRAP\n')
        except BaseException as secondary:
            errors.append(secondary)
            if work is not None:persist(work,errors,1000)
        return 1


if __name__=='__main__':
    try:
        code=guarded_main()
    except BaseException as error:
        guarded_main.last_errors=[error]
        try:os.write(2,b'UNIT_CENSUS_FAILED_OUTERMOST\n')
        except BaseException as secondary:guarded_main.last_errors.append(secondary)
        code=1
    raise SystemExit(code)
