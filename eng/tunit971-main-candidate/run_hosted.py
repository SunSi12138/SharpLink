"""Two selected standard-hosted platforms; no descendant-join/full-SDK-seal claim.
Raw streams and complete error graphs stay private. No retry or test expansion.
"""
import sys
sys.dont_write_bytecode=True
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import stat
import tempfile
import time
import audit
import accept_results as accept
import candidate
import classify as c
import hosted_process as host
import report_checks as reports
import failure_diagnostics as diagnostics

PAYLOAD=Path(__file__).resolve().parent
FOCUSED='test/SharpLink.IntegrationTests'
FILTERS={'target':f'/*/{accept.NS}/{accept.CLASS}/{accept.TARGET}',
         'control':f'/*/{accept.NS}/{accept.CLASS}/{accept.CONTROL}'}
EXPECTED_STAGES=('prepare-baseline','prepare-fixed','sdk-version-baseline','sdk-version-fixed','sdk-info','runtime-inventory',
                 'restore-baseline','restore-fixed','build-baseline','build-fixed','list-baseline-target','list-fixed-target',
                 'list-fixed-control','baseline-original-accept','fixed-original-accept','fixed-neighbor-control')
MAX_COMMANDS=16
ACTIVE_SECONDS=3360
CLEANUP_SECONDS=16*16
TOTAL_SECONDS=3900
QUALIFICATION='Standard GitHub-hosted test/report qualification; no independent descendant join or full SDK/input seal'
# Only immutable catalog spellings can be printed; all other changed paths hash-only.
KNOWN_DIAGNOSTIC_PATHS=frozenset(audit.CATALOG['sourcePins'])|frozenset(c.read_json(PAYLOAD/'SOURCE.json')['mainPins'])|frozenset((candidate.FIXTURE,))


def sha(path):
    info=path.lstat()
    c.require(stat.S_ISREG(info.st_mode) and not getattr(info,'st_file_attributes',0)&0x400,'Hash input is not ordinary file')
    return hashlib.sha256(path.read_bytes()).hexdigest()
def identity(path):
    host.ordinary_directory(path)
    s=path.lstat();return s.st_dev,s.st_ino


def inventory(root,excluded=(),*,checkout_metadata=False):
    host.ordinary_directory(root);result={}
    c.require(type(checkout_metadata) is bool,'Invalid checkout metadata policy')
    # Only the caller's selected checkout root may omit its real metadata.
    # Validate before excluding; symlink/junction/file/special is never hidden.
    if checkout_metadata:host.ordinary_directory(root/'.git')
    for current,dirs,names in os.walk(root,followlinks=False):
        current=Path(current)
        dirs[:]=[n for n in dirs if not (checkout_metadata and current==root and n=='.git') and not any((current/n).is_relative_to(x) for x in excluded)]
        for name in dirs+names:
            path=current/name
            if any(path.is_relative_to(x) for x in excluded):continue
            info=path.lstat()
            c.require(not getattr(info,'st_file_attributes',0)&0x400 and
                      (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)), 'Special/reparse guarded input')
            c.require(name!='.git','Unselected or nested git metadata forbidden')
            if stat.S_ISREG(info.st_mode):result[path.relative_to(root).as_posix()]=sha(path)
    return result


def path_map(root):
    # A new requested policy, not proof of actual Csc output. Actual reported
    # fixture/stack paths must still equal the single exact /_/ source mapping.
    text=root.as_posix()
    c.require(root.is_absolute() and text.rstrip('/') and '..' not in root.parts and
              not any(x in text for x in ('=',',',';','\n','\r')), 'Unsupported selected PathMap root')
    return '-p:PathMap='+text.rstrip('/')+'/=/_/'


def environment(state):
    keep=('PATH','SystemRoot','WINDIR','COMSPEC','PATHEXT','SYSTEMDRIVE','PROCESSOR_ARCHITECTURE',
          'DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ROOT_ARM64','LANG','LC_ALL',
          'HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY','http_proxy','https_proxy','all_proxy','no_proxy',
          'SSL_CERT_FILE','SSL_CERT_DIR','REQUESTS_CA_BUNDLE','CURL_CA_BUNDLE')
    env={k:os.environ[k] for k in keep if k in os.environ}
    for name in ('home','cli','packages','http','plugins','tmp'):(state/name).mkdir(parents=True)
    env.update(HOME=str(state/'home'),USERPROFILE=str(state/'home'),DOTNET_CLI_HOME=str(state/'cli'),
               NUGET_PACKAGES=str(state/'packages'),NUGET_HTTP_CACHE_PATH=str(state/'http'),
               NUGET_PLUGINS_CACHE_PATH=str(state/'plugins'),TMPDIR=str(state/'tmp'),TMP=str(state/'tmp'),TEMP=str(state/'tmp'),
               DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_NOLOGO='1',DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               DOTNET_MULTILEVEL_LOOKUP='0',MSBUILDDISABLENODEREUSE='1',DOTNET_CLI_USE_MSBUILD_SERVER='0',
               CI='true',DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true',PYTHONDONTWRITEBYTECODE='1',
               GIT_CONFIG_GLOBAL=os.devnull,GIT_CONFIG_SYSTEM=os.devnull,GIT_TERMINAL_PROMPT='0',GIT_NO_LAZY_FETCH='1',GIT_ALLOW_PROTOCOL='file',
               GIT_CONFIG_COUNT='1',GIT_CONFIG_KEY_0='core.autocrlf',GIT_CONFIG_VALUE_0='false',
               TUNIT_ENABLE_JUNIT_REPORTER='1',TUNIT_DISABLE_ARTIFACT_UPLOAD='1',TUNIT_DISABLE_GITHUB_REPORTER='1',
               TUNIT_AGGREGATE_REPORTS='off',TUNIT_DISABLE_LOGO='1')
    return env


def clean_baseline_green(error):
    if isinstance(error,BaseExceptionGroup):
        if len(error.exceptions)!=1:return False
        return clean_baseline_green(error.exceptions[0])
    if not isinstance(error,host.HostedCommandFailure):return False
    row=error.outcome
    return (len(error.causes)==1 and type(error.causes[0]) is ValueError and error.causes[0].args==('Unexpected hosted command exit',)
            and type(row.get('exitCode')) is int and row['exitCode']==0 and row.get('expectedExit')==2
            and row.get('timedOut') is False and row.get('cancelled') is False and row.get('directProcessTerminal') is True
            and row.get('cleanupErrors')==[] and row.get('reportingErrors')==[])


class InconclusiveBaseline(ValueError):pass


class PrivateSink:
    """Pre-open original handle; never reopen a rejected output path."""
    def __init__(self,root):
        self.root=root;self.root_id=identity(root);self.path=root/'FINAL.json'
        self.file=self.path.open('x',encoding='utf-8');s=os.fstat(self.file.fileno());self.file_id=(s.st_dev,s.st_ino)
    def guard(self):
        c.require(identity(self.root)==self.root_id,'Private final root replaced')
        s=self.path.lstat()
        c.require(stat.S_ISREG(s.st_mode) and not getattr(s,'st_file_attributes',0)&0x400 and
                  (s.st_dev,s.st_ino)==self.file_id,'Private final sink replaced')
    def write(self,value):
        self.guard();self.file.write(json.dumps(value,indent=2)+'\n');self.file.flush()
    def close(self):self.file.close()


class Lane:
    def __init__(self,roots,manifest_sha):
        c.require(sys.platform in ('win32','darwin'),'Unselected hosted OS')
        c.require((sys.platform=='win32' and platform.machine().lower() in ('amd64','x86_64')) or
                  (sys.platform=='darwin' and platform.machine().lower() in ('arm64','aarch64')),'Unselected hosted architecture')
        self.roots=roots;self.manifest_sha=manifest_sha
        # Canonical OS temp path avoids macOS /var and /tmp symlink ancestors.
        temp=Path(tempfile.gettempdir()).resolve(strict=True)
        self.work=Path(tempfile.mkdtemp(prefix='sl971-',dir=temp)).resolve(strict=True)
        self.work_id=identity(self.work);self.sink=PrivateSink(self.work)
        self.inputs=inventory(PAYLOAD);self.current={v:inventory(p,checkout_metadata=True) for v,p in roots.items()}
        self.env={v:environment(self.work/v) for v in roots}
        self.cache={v:inventory(Path(e['NUGET_PACKAGES'])) for v,e in self.env.items()}
        self.dotnet=Path(shutil.which('dotnet') or '').resolve(strict=True)
        c.require(self.dotnet.is_file(),'Selected dotnet host missing');self.host_sha=sha(self.dotnet)
        self.pinned={};self.evidence={};self.pipes=[];self.results=[];self.commands_started=0;self.stage='preflight'
        self.deadline=time.monotonic()+TOTAL_SECONDS;self.runtimes=set();self.paired_runtime=None
        self.seen_publications=set();self.seen_engine_uids=set();self.all_discovery_uids=set();self.baseline_reproduced=False
        self.active_scope=None;self.active_uid=None
    def inputs_guard(self):
        c.require(identity(self.work)==self.work_id,'Private work root replaced');self.sink.guard()
        c.require(inventory(PAYLOAD)==self.inputs and sha(PAYLOAD/'CI.json')==self.manifest_sha,'Frozen payload changed')
        c.require(sha(self.dotnet)==self.host_sha,'Observed dotnet host changed')
        for path,digest in self.pinned.items():c.require(path.is_file() and not path.is_symlink() and sha(path)==digest,'Pinned config/restore input changed')
        for path,snapshot in self.evidence.items():c.require(inventory(path)==snapshot,'Prior private evidence changed')
    def current_guard(self):
        for v,root in self.roots.items():
            diagnostics.require_inventory(inventory(root,checkout_metadata=True),self.current[v],'Protected source/output changed',v,'current',KNOWN_DIAGNOSTIC_PATHS)
            c.require(inventory(Path(self.env[v]['NUGET_PACKAGES']))==self.cache[v],'Protected package cache changed')
    def command(self,label,variant,argv,seconds,phase='readonly',expected_exit=0,filtered=False):
        self.stage=label;self.inputs_guard();self.current_guard()
        c.require(time.monotonic()+seconds+16<=self.deadline,'Hosted finite budget exhausted')
        self.commands_started+=1;c.require(self.commands_started<=MAX_COMMANDS,'Extra hosted command forbidden')
        c.require(label==EXPECTED_STAGES[self.commands_started-1],'Unexpected hosted command order')
        excluded={v:[] for v in self.roots}
        root=self.roots[variant]
        if phase=='prepare':
            excluded[variant]=[root/'Directory.Packages.props',root/candidate.FIXTURE]
            if variant=='fixed':excluded[variant].append(root/candidate.COORDINATOR)
        if phase in ('restore','build'):
            for project in audit.PROJECTS.values():
                parent=root/Path(project).parent;excluded[variant].append(parent/'obj')
                if phase=='build':excluded[variant].append(parent/'bin')
        before={v:inventory(p,excluded[v],checkout_metadata=True) for v,p in self.roots.items()}
        caches={v:self.cache[v] for v in self.roots if not(v==variant and phase=='restore')}
        env=dict(self.env[variant]);pipe=None;pipe_id=None;errors=[];outcome=None;directory=self.work/label
        try:
            if filtered:
                # macOS uses short canonical /private/tmp, independent of long RUNNER_TEMP.
                parent=Path('/private/tmp') if sys.platform=='darwin' else self.work
                host.ordinary_directory(parent)
                pipe=Path(tempfile.mkdtemp(prefix='s971-',dir=parent)).resolve(strict=True)
                pipe_id=identity(pipe);self.pipes.append((pipe,pipe_id))
                if sys.platform=='darwin':c.require(len(str(pipe).encode())+1+32<=103,'MTP socket path over budget')
                env['TESTINGPLATFORM_PIPE_DIRECTORY']=str(pipe)
            outcome=host.run(argv,cwd=root,output=directory,label=label,timeout=seconds,expected_exit=expected_exit,env=env)
        except BaseException as error:errors.append(error)
        finally:
            checks=[self.inputs_guard]
            checks.extend(lambda v=v,p=p:diagnostics.require_inventory(inventory(p,excluded[v],checkout_metadata=True),before[v],'Source/protected output mutation',v,'phase',KNOWN_DIAGNOSTIC_PATHS) for v,p in self.roots.items())
            checks.extend(lambda v=v,s=s:c.require(inventory(Path(self.env[v]['NUGET_PACKAGES']))==s,'Protected cache mutation') for v,s in caches.items())
            if pipe is not None:checks.append(lambda:c.require(pipe_id is not None and identity(pipe)==pipe_id and not list(pipe.iterdir()),'Pipe missing/replaced/nonempty'))
            for check in checks:
                try:check()
                except BaseException as error:errors.append(error)
            try:
                if directory.exists():self.evidence[directory]=inventory(directory)
            except BaseException as error:errors.append(error)
        if errors:
            original=BaseExceptionGroup('Hosted command failed; original errors retained',errors)
            try:
                self.inputs_guard()
                diagnostics.attach(original,self.work,directory,label)
            except BaseException as diagnostic:
                if isinstance(diagnostic,BaseExceptionGroup) and diagnostic.exceptions[0] is original:raise
                raise BaseExceptionGroup('Original command failure and diagnostic guard failure',[original,diagnostic]) from None
            raise original
        self.current={v:inventory(p,checkout_metadata=True) for v,p in self.roots.items()}
        self.cache={v:inventory(Path(e['NUGET_PACKAGES'])) for v,e in self.env.items()}
        print(json.dumps({'stage':label,'exit':outcome['exitCode'],'seconds':outcome['seconds'],
                          'directProcessTerminal':outcome['directProcessTerminal'],'descendantJoinProven':False,
                          'expectedExitOnly':expected_exit,'classification':'HOSTED_COMMAND_NOT_TEST_QUALIFICATION'}),flush=True)
        return directory,(directory/(label+'.stdout'),directory/(label+'.stderr'))
    def final_guards(self):
        errors=[]
        checks=[self.inputs_guard,self.current_guard,lambda:c.require(time.monotonic()<=self.deadline,'Hosted total deadline exceeded')]
        checks.extend(lambda p=p,i=i:c.require(identity(p)==i and not list(p.iterdir()),'Final pipe guard failed') for p,i in self.pipes)
        for check in checks:
            try:check()
            except BaseException as error:errors.append(error)
        return errors
    def read_outcome(self,directory,ledger,scope,variant):
        native=c.read_json(directory/'reports/session.tunit-report.json')
        junit=(directory/'reports/junit.xml').read_text(encoding='utf-8-sig')
        c.check_optional_filter(native,FILTERS['control' if scope=='control' else 'target'])
        return accept.classify(native,junit,ledger,scope,self.roots[variant]/accept.FIXTURE,self.runtimes,
                               self.paired_runtime,self.seen_publications,self.seen_engine_uids,self.all_discovery_uids)
    def filtered(self,label,variant,key,listing=False,scope=None,ledger=None):
        self.active_scope=scope;self.active_uid=next(iter(ledger)) if ledger else None
        if not listing and scope!='baseline':c.require(self.baseline_reproduced,'Baseline event gate missing')
        directory=self.work/label;assembly=self.roots[variant]/FOCUSED/'bin/Release/net10.0/SharpLink.IntegrationTests.dll'
        argv=filtered_argv(self.dotnet,assembly,directory,key,listing)
        try:directory,output=self.command(label,variant,argv,120,expected_exit=2 if scope=='baseline' else 0,filtered=True)
        except BaseException as original:
            if not listing and scope=='baseline' and clean_baseline_green(original):
                try:
                    self.inputs_guard();self.current_guard()
                    result=self.read_outcome(directory,ledger,'baseline-green',variant)
                    reports.validate_stderr('baseline-green',(directory/(label+'.stderr')).read_text(encoding='utf-8',errors='replace'))
                    c.require(result['classification']=='INCONCLUSIVE_BASELINE_NO_EVENT','Wrong inconclusive classification')
                except BaseException as diagnostic:raise BaseExceptionGroup('Baseline original failure and qualifier failure',[original,diagnostic]) from None
                raise InconclusiveBaseline('BASELINE_NO_EVENT_INCONCLUSIVE') from original
            raise
        if listing:
            actual=reports.discover_with_path_receipt(c.read_json(output[0]),accept.CONTROL if key=='control' else accept.TARGET,self.roots[variant]/accept.FIXTURE)
            self.all_discovery_uids.update(actual)
            print(json.dumps({'stage':label,'classification':'ACTUAL_UID_DISCOVERY','count':len(actual),
                              'uidSetSHA256':hashlib.sha256(json.dumps(sorted(actual)).encode()).hexdigest(),'testsExecuted':False}),flush=True)
            return actual
        result=self.read_outcome(directory,ledger,scope,variant)
        reports.validate_stderr(scope,output[1].read_text(encoding='utf-8',errors='replace'),lambda:output[0].read_text(encoding='utf-8',errors='replace'))
        uid=result.pop('engineUID')
        if uid:self.seen_engine_uids.add(uid)
        self.seen_publications.add(result['publicationGeneration'])
        if scope=='baseline':self.paired_runtime=result['runtime'];self.baseline_reproduced=True
        result.update(stage=label,strictOption=True,outputPolicy='Detailed explicit',qualification=QUALIFICATION)
        self.results.append(result);print(json.dumps(result),flush=True)
        return result


def filtered_argv(dotnet,assembly,directory,key,listing):
    c.require(key in FILTERS and type(listing) is bool,'Unknown filtered command')
    argv=[str(dotnet),str(assembly),'--treenode-filter',FILTERS[key],'--maximum-parallel-tests','1','--no-progress',
          '--results-directory',str(directory/'reports')]
    if listing:argv+=['--list-tests','json']
    else:argv+=['--output','Detailed','--report-html','--report-html-filename',str(directory/'reports/session.html'),
                '--junit-output-path',str(directory/'reports/junit.xml'),'--fail-on-unobserved-task-exceptions']
    return argv


def execute(lane):
    for v,root in lane.roots.items():
        lane.command('prepare-'+v,v,[sys.executable,str(PAYLOAD/'candidate.py'),'--checkout',str(root),'--variant',v,
                     '--source-sha256',sha(PAYLOAD/'SOURCE.json')],60,phase='prepare');audit.static_graph(root)
    left=dict(lane.current['baseline']);right=dict(lane.current['fixed'])
    left.pop(candidate.COORDINATOR);right.pop(candidate.COORDINATOR)
    c.require(left==right,'Variants differ beyond cold coordinator patch')
    versions=[]
    for v in lane.roots:
        _,out=lane.command('sdk-version-'+v,v,[str(lane.dotnet),'--version'],30)
        version=out[0].read_text(encoding='utf-8-sig').strip()
        c.require(re.fullmatch(r'10\.0\.1\d\d',version) and int(version.split('.')[-1])>=102,'SDK outside selected main latestPatch band');versions.append(version)
    c.require(len(set(versions))==1,'Variants selected different SDK versions')
    _,info=lane.command('sdk-info','baseline',[str(lane.dotnet),'--info'],30)
    _,runtime=lane.command('runtime-inventory','baseline',[str(lane.dotnet),'--list-runtimes'],30)
    host_version=re.search(r'Host:\s*\n\s*Version:\s*(\d+\.\d+\.\d+)',info[0].read_text(encoding='utf-8-sig'))
    c.require(host_version is not None,'Actual host version absent')
    host_section=info[0].read_text(encoding='utf-8-sig').split('Host:',1)[-1].split('\n\n',1)[0]
    architecture=re.search(r'^\s*Architecture:\s*(x64|arm64)\s*$',host_section,re.M)
    c.require(architecture is not None and architecture.group(1)==('x64' if sys.platform=='win32' else 'arm64'),'Unexpected actual dotnet host architecture')
    lane.runtimes=set(re.findall(r'^Microsoft.NETCore.App (10\.0\.\d+) \[',runtime[0].read_text(encoding='utf-8-sig'),re.M))
    c.require(lane.runtimes,'Observed .NET10 runtime absent')
    print(json.dumps({'stage':'sdk-observed','actualSDK':versions[0],'actualHost':host_version.group(1),
                      'observedCoreRuntimes':sorted(lane.runtimes),'hostSHA256':lane.host_sha,'platform':sys.platform,
                      'architecture':platform.machine(),'actualHostArchitecture':architecture.group(1),'actualPython':platform.python_version(),'continuousSDKSeal':False,'qualification':QUALIFICATION}),flush=True)
    config=lane.work/'NuGet.Config'
    config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping><auditSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></auditSources></configuration>\n',encoding='utf-8')
    lane.pinned[config]=sha(config)
    restored={}
    for v,root in lane.roots.items():
        project=root/audit.PROJECTS[audit.ENTRY]
        lane.command('restore-'+v,v,[str(lane.dotnet),'restore',str(project),'--configfile',str(config),'--disable-parallel','--force','--verbosity','minimal'],600,phase='restore')
        restored[v]=audit.restored(root,Path(lane.env[v]['NUGET_PACKAGES']))
        for path in root.rglob('*'):
            if path.is_file() and 'obj' in path.relative_to(root).parts and (path.name.startswith('project.') or '.nuget.' in path.name):lane.pinned[path]=sha(path)
        print(json.dumps({'stage':'restore-audit-'+v,'officialArchiveCount':len(restored[v]['cache']),'projectCount':19,'activePackages':restored[v]['packages']}),flush=True)
    c.require(restored['baseline']==restored['fixed'],'Variants restored different official packages/graph')
    for v,root in lane.roots.items():
        lane.command('build-'+v,v,[str(lane.dotnet),'build',str(root/audit.PROJECTS[audit.ENTRY]),'-c','Release','--no-restore','-m:1',
                     '-nodeReuse:false','-p:UseSharedCompilation=false',path_map(root),'--verbosity','minimal'],600,phase='build')
        result=audit.built(root,Path(lane.env[v]['NUGET_PACKAGES']),restored[v]['packages'])
        print(json.dumps({'stage':'build-audit-'+v,**result}),flush=True)
    baseline=lane.filtered('list-baseline-target','baseline','target',listing=True)
    fixed=lane.filtered('list-fixed-target','fixed','target',listing=True)
    control=lane.filtered('list-fixed-control','fixed','control',listing=True)
    c.require(baseline==fixed,'Variants discovered different original target identity')
    lane.filtered('baseline-original-accept','baseline','target',scope='baseline',ledger=baseline)
    lane.filtered('fixed-original-accept','fixed','target',scope='fixed',ledger=fixed)
    lane.filtered('fixed-neighbor-control','fixed','control',scope='control',ledger=control)
    c.require([x['classification'] for x in lane.results]==['ORIGINAL_ACCEPT_EVENT_REPRODUCED','ORIGINAL_ACCEPT_FIXED_PASS','NEIGHBOR_CONTROL_PASS']
              and lane.commands_started==MAX_COMMANDS,'Incomplete hosted three-body protocol')


def safe_leaves(errors):
    rows=[];count=0
    def visit(e):
        nonlocal count
        if isinstance(e,BaseExceptionGroup):
            for child in e.exceptions:visit(child)
            return
        count+=1
        if len(rows)>=8:return
        row={'type':type(e).__name__}
        reason=diagnostics.known_reason(e)
        if reason is not None:row['reason']=reason
        if isinstance(e,host.HostedCommandFailure):
            o=e.outcome;row.update(exit=o.get('exitCode'),timedOut=o.get('timedOut'),cancelled=o.get('cancelled'),
                                  directProcessTerminal=o.get('directProcessTerminal'),cleanupErrors=len(o.get('cleanupErrors',[])),
                                  reportingErrors=len(o.get('reportingErrors',[])),descendantJoinProven=False)
        rows.append(row)
    for error in errors:visit(error)
    return {'leaves':rows,'leafCount':count,'truncated':count>len(rows)}


def finish(lane,errors):
    try:errors.extend(lane.final_guards())
    except BaseException as error:errors.append(error)
    # Diagnostics contain only reviewed counts/hashes/known paths, never report text.
    def diagnostics(error):
        if isinstance(error,BaseExceptionGroup):
            for child in error.exceptions:diagnostics(child)
        for name in ('raw_events','discovery_paths','project_edges','rid_asset','command_codes','protected_inventory'):
            value=getattr(error,name,None)
            if value is not None:print(json.dumps({'stage':lane.stage,'diagnostic':name,'data':value.public()}),flush=True)
    if errors:
        try:
            for error in list(errors):diagnostics(error)
            print(json.dumps({'classification':'HOSTED_PIPELINE_FAILED','stage':lane.stage,'qualifiedScopes':len(lane.results),**safe_leaves(errors)}),flush=True)
        except BaseException as error:errors.append(error)
    formatting=[]
    try:
        graphs=[host.private_graph(e,formatting_errors=formatting) for e in errors]
        errors.extend(formatting)
        lane.sink.write({'receiptRole':'Private pre-close snapshot; not standalone final qualification','qualification':QUALIFICATION,'stage':lane.stage,'results':lane.results,'errors':graphs,
                         'formatterErrors':[host.private_graph(e) for e in formatting],'commandsStarted':lane.commands_started})
    except BaseException as error:errors.append(error)
    try:lane.sink.close()
    except BaseException as error:errors.append(error)
    if errors:raise BaseExceptionGroup('Hosted pipeline failed; complete original graph retained',errors)
    print(json.dumps({'classification':'HOSTED_STANDARD_FINITE_TEST_PASS','baselineEventScopes':1,'fixedTargetScopes':1,'neighborScopes':1,
                      'commands':lane.commands_started,'descendantJoinProven':False,'continuousSDKSeal':False,'qualification':QUALIFICATION}),flush=True)


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--baseline',type=Path,required=True);parser.add_argument('--fixed',type=Path,required=True);parser.add_argument('--ci-manifest-sha256',required=True)
    args=parser.parse_args();c.require(sha(PAYLOAD/'CI.json')==args.ci_manifest_sha256,'Wrong hosted CI manifest')
    c.require(all(p.is_file() and not p.is_symlink() for p in PAYLOAD.iterdir()),'Unpinned payload directory/symlink')
    pins=c.read_json(PAYLOAD/'CI.json')['code']
    c.require(set(pins)=={p.name for p in PAYLOAD.iterdir() if p.is_file() and p.name!='CI.json'},'Unpinned payload file')
    for name,value in pins.items():c.require(sha(PAYLOAD/name)==value,'Hosted payload pin mismatch')
    roots={'baseline':args.baseline.resolve(strict=True),'fixed':args.fixed.resolve(strict=True)}
    c.require(not roots['baseline'].is_relative_to(roots['fixed']) and not roots['fixed'].is_relative_to(roots['baseline']) and
              not any(PAYLOAD.is_relative_to(p) or p.is_relative_to(PAYLOAD) for p in roots.values()),'Candidate roots overlap')
    lane=Lane.__new__(Lane);errors=[]
    try:
        Lane.__init__(lane,roots,args.ci_manifest_sha256)
        execute(lane)
    except BaseException as error:errors.append(error)
    if not hasattr(lane,'sink'):
        raise BaseExceptionGroup('Hosted initialization failed before trusted sink creation',errors)
    if not hasattr(lane,'stage'):lane.stage='initialization'
    if not hasattr(lane,'results'):lane.results=[]
    if not hasattr(lane,'commands_started'):lane.commands_started=0
    finish(lane,errors)


if __name__=='__main__':
    try:main()
    except BaseException as error:
        # Never print raw exception text/argv/env/stack. Original graph remains
        # in the private trusted sink when writable; reporting failures fail too.
        try:print(json.dumps({'classification':'HOSTED_PIPELINE_FAILED','type':type(error).__name__}),flush=True)
        except BaseException:pass
        sys.exit(1)
