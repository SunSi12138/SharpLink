"""Exact-main Linux THREE-case diagnosis. Not a soak or a production fix."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import sys
import tempfile
import time
import traceback
import prepare
import process_owner as owner
import results as r
from error_graph import exception_graph

PROJECT='test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj'
METHOD_FILTER='/*/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests/'+r.METHOD
LIMITS=(('git-head',10),('git-tree',10),('sdk-version',30),('restore',600),('build',600),('discovery',120),
        ('consumer-break',150),('normal',150),('cancellation-mid-stream',150))


FIXED_REASONS=frozenset(('Ambiguous instrumentation anchor', 'Checkout has untracked source', 'Command directory replaced', 'Command output replaced', 'Discovery FQN/signature', 'Discovery five-mode bijection', 'Discovery parameter identity', 'Discovery schema', 'Discovery source span', 'Duplicate JSON key', 'Empty Git tree', 'Evidence changed while reading', 'Evidence root identity changed', 'Exact main preimage mismatch', 'Expected exact five source arguments', 'Final receipt identity changed', 'Finite budget drift', 'Git executable flag mismatch', 'Git source bytes mismatch', 'Insufficient owned command budget', 'Invalid pidfd readiness', 'JUnit counts', 'JUnit identity/result', 'JUnit metadata', 'JUnit singleton', 'JUnit suites', 'Linux pidfd support required', 'Linux-only diagnostic', 'Malformed stage record', 'Missing Git source', 'Missing selected SDK host', 'Missing selected cases', 'Missing/duplicate actual UID', 'Native actual UID identity', 'Native assembly/version', 'Native class', 'Native duration', 'Native generation', 'Native groups', 'Native non-PASS or retry', 'Native runtime identity', 'Native schema', 'Native singleton', 'Native source span', 'New stage source already exists', 'Nonordinary Git source entry', 'Nonordinary directory', 'Nonordinary evidence file', 'Nonordinary evidence root', 'Nonordinary file', 'Nonordinary inherited stage descriptor', 'Nonordinary original stage receipt', 'Nonordinary/oversized evidence', 'Nonpositive deadline', 'Not singleton PASS', 'Original last-stage receipt replaced', 'Original stage receipt replaced', 'Output directory is not ordinary', 'Oversized stage log', 'Payload changed', 'Payload pin mismatch', 'Pipe path budget', 'Pipe retained entries', 'Prior evidence changed', 'Prior stage evidence changed', 'Readonly cache changed', 'Readonly checkout/output changed', 'Receipt write made no progress', 'Relative selected root', 'SDK host changed', 'Stage GC/terminal sequence', 'Stage ownership prefix', 'Supervisor already has unrelated children', 'Symlink/special root ancestor', 'Total diagnostic deadline', 'Tracked source changed', 'Truncated complete stage record', 'Unexpected SDK band', 'Unexpected actual package identities', 'Unexpected checkout output', 'Unexpected child signal disposition', 'Unexpected instrumentation postimage', 'Unexpected owner error collection', 'Unexpected owner exit type', 'Unexpected owner failure flags', 'Unexpected owner failure stage', 'Unexpected owner failure type', 'Unexpected owner remaining state', 'Unexpected report publication set', 'Unexpected session errors', 'Unknown stage or time regression', 'Unqualified command receipt', 'Unsafe JUnit', 'Unsafe receipt name', 'Wrong main HEAD'))


def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()


def directory_identity(path, expected=None):
    info=path.lstat()
    r.require(stat.S_ISDIR(info.st_mode),'Nonordinary evidence root')
    identity=(info.st_dev,info.st_ino,info.st_mode&0o777)
    r.require(expected is None or identity==expected,'Evidence root identity changed')
    return identity


def file_identity(path):
    info=path.lstat()
    r.require(stat.S_ISREG(info.st_mode) and info.st_nlink==1,'Nonordinary evidence file')
    return (info.st_dev,info.st_ino,info.st_mode&0o777)


def snapshot(root, *, checkout=False):
    identity=directory_identity(root)
    found={'.':('dir',*identity)}
    for parent,dirs,files in os.walk(root,followlinks=False):
        directory=Path(parent)
        for name in dirs[:]:
            path=directory/name;info=path.lstat()
            r.require(stat.S_ISDIR(info.st_mode),'Nonordinary directory')
            if checkout and directory==root and name=='.git':dirs.remove(name);continue
            found[str(path.relative_to(root))]=('dir',info.st_dev,info.st_ino)
        for name in files:
            path=directory/name;info=path.lstat()
            r.require(stat.S_ISREG(info.st_mode) and info.st_nlink==1,'Nonordinary file')
            found[str(path.relative_to(root))]=('file',hashlib.sha256(read_regular(path,256*1024*1024)).hexdigest(),info.st_mode&0o777)
    directory_identity(root,identity)
    return found


def trusted_path(path):
    r.require(path.is_absolute(),'Relative selected root')
    for p in (path,*path.parents):r.require(stat.S_ISDIR(p.lstat().st_mode),'Symlink/special root ancestor')
    return path


def read_regular(path,limit=16*1024*1024):
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
    errors=[];data=None
    try:
        st=os.fstat(fd);r.require(stat.S_ISREG(st.st_mode) and st.st_nlink==1 and st.st_size<=limit,'Nonordinary/oversized evidence')
        data=b''
        while len(data)<=limit:
            part=os.read(fd,min(65536,limit+1-len(data)))
            if not part:break
            data+=part
        r.require(len(data)<=limit and len(data)==st.st_size,'Evidence changed while reading')
    except BaseException as exc:errors.append(exc)
    try:os.close(fd)
    except BaseException as exc:errors.append(exc)
    if errors:raise BaseExceptionGroup('Evidence read/close failure',errors)
    return data


def child_environment(work,dotnet):
    allowed=('PATH','LD_LIBRARY_PATH','LANG','LC_ALL','SSL_CERT_FILE','SSL_CERT_DIR','REQUESTS_CA_BUNDLE',
             'CURL_CA_BUNDLE','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY',
             'http_proxy','https_proxy','all_proxy','no_proxy')
    env={k:os.environ[k] for k in allowed if k in os.environ}
    home=work/'home';home.mkdir();tmp=work/'tmp';tmp.mkdir();cache=work/'packages';cache.mkdir()
    env.update(HOME=str(home),DOTNET_CLI_HOME=str(home),TMPDIR=str(tmp),NUGET_PACKAGES=str(cache),
        DOTNET_ROOT=str(dotnet.parent),DOTNET_NOLOGO='1',DOTNET_CLI_TELEMETRY_OPTOUT='1',
        TESTINGPLATFORM_TELEMETRY_OPTOUT='1',DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
        MSBUILDDISABLENODEREUSE='1',DOTNET_CLI_USE_MSBUILD_SERVER='0',
        TUNIT_ENABLE_JUNIT_REPORTER='1',TUNIT_DISABLE_ARTIFACT_UPLOAD='1',TUNIT_DISABLE_GITHUB_REPORTER='1',
        TUNIT_AGGREGATE_REPORTS='off',TUNIT_DISABLE_LOGO='1')
    return env


def verify_tree(root,raw):
    paths={}
    for record in raw.split(b'\0'):
        if not record:continue
        header,name=record.split(b'\t',1);mode,kind,blob=header.decode().split();relative=name.decode()
        r.require(mode in ('100644','100755') and kind=='blob','Nonordinary Git source entry')
        path=root/relative;r.require(not path.is_symlink() and path.is_file(),'Missing Git source')
        b=path.read_bytes();actual=hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()
        r.require(actual==blob,'Git source bytes mismatch')
        r.require(bool(path.stat().st_mode & stat.S_IXUSR)==(mode=='100755'),'Git executable flag mismatch');paths[relative]=sha(path)
    r.require(paths,'Empty Git tree');return paths


def report_args(dotnet,dll,uid,report):
    return [dotnet,dll,'--filter-uid',uid,'--maximum-parallel-tests','1','--timeout','120s',
            '--output','Detailed','--results-directory',report,'--report-html',
            '--report-html-filename',report/'session.html','--junit-output-path',report/'junit.xml']


def stage_identity_at(dfd,name):
    info=os.stat(name,dir_fd=dfd,follow_symlinks=False)
    r.require(stat.S_ISREG(info.st_mode) and info.st_nlink==1,'Nonordinary original stage receipt')
    return (info.st_dev,info.st_ino,info.st_mode&0o777)


def execute_body_with_stage(dfd, stages, report, roots, operation, stage_sources=None):
    stagefd=None;identity=None;errors=[]
    if stage_sources is None:stage_sources={}
    try:
        report.mkdir();roots[report]=directory_identity(report)
        stagefd=owner.open_exclusive(dfd,stages.name)
        info=os.fstat(stagefd)
        r.require(stat.S_ISREG(info.st_mode) and info.st_nlink==1,'Nonordinary inherited stage descriptor')
        identity=(info.st_dev,info.st_ino,info.st_mode&0o777);stage_sources[stages]=identity
        operation(stagefd)
    except BaseException as exc:errors.append(exc)
    if stagefd is not None:
        if identity is not None:
            try:r.require(stage_identity_at(dfd,stages.name)==identity,'Original stage receipt replaced')
            except BaseException as exc:errors.append(exc)
        try:os.close(stagefd)
        except BaseException as exc:errors.append(exc)
    if errors:raise BaseExceptionGroup('Body and stage descriptor close',errors)


def owner_failure_summary(failure,label):
    r.require(type(failure) is owner.CommandFailure,'Unexpected owner failure type')
    row=failure.outcome
    r.require(type(row) is dict and row.get('stage')==label and label in dict(LIMITS),'Unexpected owner failure stage')
    exit_code=row.get('exitCode');r.require(exit_code is None or type(exit_code) is int,'Unexpected owner exit type')
    r.require(type(row.get('timedOut')) is bool and type(row.get('cancelled')) is bool,'Unexpected owner failure flags')
    counts={}
    for key in ('errors','cleanupErrors','reportingErrors'):
        r.require(type(row.get(key)) is list,'Unexpected owner error collection');counts[key]=len(row[key])
    remaining=row.get('remainingOwned')
    r.require(remaining is None or type(remaining) is dict,'Unexpected owner remaining state')
    return {'classification':'OWNED_COMMAND_FAILED','stage':label,'exit':exit_code,
            'timedOut':row['timedOut'],'cancelled':row['cancelled'],
            'errorCount':counts['errors'],'cleanupErrorCount':counts['cleanupErrors'],
            'reportingErrorCount':counts['reportingErrors'],
            'remainingOwnedEmpty':None if remaining is None else not bool(remaining)}


def report_owner_failure(original,label):
    try:print(json.dumps(owner_failure_summary(original,label)),flush=True)
    except BaseException as reporting:raise BaseExceptionGroup('Owner failure and safe summary failure',[original,reporting])
    raise original


def require_snapshot(actual,expected,reason,role,evidence):
    try:r.require(actual==expected,reason)
    except BaseException as original:
        try:evidence.append((role,actual,expected))
        except BaseException as capture:raise BaseExceptionGroup('Guard and diagnostic capture failure',[original,capture])
        raise


def safe_snapshot_diff(role,actual,expected,known):
    r.require(role in ('checkout','cache','prior-evidence'),'Unknown diagnostic snapshot role')
    missing=sorted(set(expected)-set(actual));added=sorted(set(actual)-set(expected))
    changed=sorted(k for k in set(actual)&set(expected) if actual[k]!=expected[k])
    rows=[]
    for kind,names in (('removed',missing),('added',added),('changed',changed)):
        for name in names:
            if len(rows)>=8:break
            item={'kind':kind,'pathSHA256':hashlib.sha256(name.encode()).hexdigest()}
            if role=='checkout' and name in known and len(name)<=240:item['knownRelative']=name
            for side,tree in (('expected',expected),('actual',actual)):
                if name not in tree:continue
                value=tree[name];r.require(type(value) is tuple and value and value[0] in ('file','dir'),'Unknown diagnostic snapshot entry')
                item[side+'Kind']=value[0]
                if value[0]=='file':
                    r.require(type(value[1]) is str and re.fullmatch('[0-9a-f]{64}',value[1]),'Invalid diagnostic content hash')
                    item[side+'SHA256']=value[1]
            rows.append(item)
    total=len(missing)+len(added)+len(changed)
    return {'role':role,'expectedCount':len(expected),'actualCount':len(actual),'removed':len(missing),'added':len(added),
            'changed':len(changed),'entries':rows,'truncated':total>len(rows)}


def failure_diagnostic(errors,context,snapshots,known):
    phases={'preflight','pre-guard','owner-prepare','owner-invoked','owner-returned','receipt-guard','pipe-guard','post-guard','command-qualified','restore-assets','seal-built','discovery-schema','body-schema','final-guard'}
    r.require(context['phase'] in phases and (context['stage'] is None or context['stage'] in dict(LIMITS)),'Unknown diagnostic phase')
    r.require(type(context['ownerInvoked']) is bool,'Invalid diagnostic owner state')
    queue=list(errors);seen=set();leaves=[];truncated=False
    while queue:
        error=queue.pop(0)
        if id(error) in seen:continue
        seen.add(id(error))
        if len(seen)>64:truncated=True;break
        if isinstance(error,BaseExceptionGroup):queue[0:0]=list(error.exceptions);continue
        if len(leaves)>=8:truncated=True;continue
        name=type(error).__name__
        item={'type':name if name in {'ValueError','OSError','PermissionError','FileNotFoundError','TimeoutError','InterruptedError','JSONDecodeError','UnicodeDecodeError','CommandFailure'} else 'OtherException'}
        if type(error) is ValueError and len(error.args)==1 and type(error.args[0]) is str and error.args[0] in FIXED_REASONS:item['fixedReason']=error.args[0]
        leaves.append(item)
    terminal=None
    row=context['terminal']
    if row is not None:
        r.require(type(row) is dict and row.get('stage')==context['stage'],'Invalid diagnostic terminal binding')
        exit_code=row.get('exitCode');r.require(exit_code is None or type(exit_code) is int,'Invalid diagnostic terminal exit')
        r.require(type(row.get('timedOut')) is bool and type(row.get('cancelled')) is bool,'Invalid diagnostic terminal flags')
        r.require(all(type(row.get(k)) is list for k in ('errors','cleanupErrors','reportingErrors')),'Invalid diagnostic terminal counts')
        remain=row.get('remainingOwned');r.require(remain is None or type(remain) is dict,'Invalid diagnostic remaining state')
        terminal={'exit':exit_code,'timedOut':row['timedOut'],'cancelled':row['cancelled'],'errorCount':len(row['errors']),
            'cleanupErrorCount':len(row['cleanupErrors']),'reportingErrorCount':len(row['reportingErrors']),
            'remainingOwnedEmpty':None if remain is None else not bool(remain)}
    return {'classification':'SHORT_FAILURE_DIAGNOSTIC','stage':context['stage'],'phase':context['phase'],
            'ownerInvoked':context['ownerInvoked'],'terminal':terminal,'leaves':leaves,'leavesTruncated':truncated,
            'snapshotFailureCount':len(snapshots),'snapshotDifferences':[safe_snapshot_diff(*x,known) for x in snapshots[:2]],'snapshotDifferencesTruncated':len(snapshots)>2}


def emit_failure_diagnostic(errors,context,snapshots,known):
    try:print(json.dumps(failure_diagnostic(errors,context,snapshots,known)),flush=True)
    except BaseException as reporting:errors.append(reporting)


def main(root):
    r.require(sys.platform=='linux','Linux-only diagnostic')
    kit=Path(__file__).absolute().parent;root=trusted_path(root.absolute())
    ci=r.read_json(kit/'CI.json')
    for name,digest in ci['code'].items():r.require(sha(kit/name)==digest,'Payload pin mismatch')
    r.require(sum(s for _,s in LIMITS)==1820 and len(LIMITS)==9 and 1820+9*16<2100<45*60,'Finite budget drift')
    owner.enable_subreaper();os.umask(0o077)
    work=Path(tempfile.mkdtemp(prefix='sld-'));trusted_path(work)
    dfd=None;final=None;final_identity=None
    errors=[];qualified=[];completed=[];stage_seals={};stage_sources={};current='preflight';stages=None;source_seal=None;cache_seal=None;binary=None
    started=time.monotonic();dotnet=None;host_hash=None;env={}
    roots={p:directory_identity(p) for p in (work,root)}
    expected_sources={};seen=set();runtime=None;initial_dirs=set();mutation_phase='none'
    context={'stage':None,'phase':'preflight','ownerInvoked':False,'terminal':None};failure_context=None;snapshot_failures=[]
    project_paths=r.read_json(kit/'SOURCE.json')['projectPaths']
    def guard():
        for path,identity in roots.items():directory_identity(path,identity)
        r.require(time.monotonic()-started<2100,'Total diagnostic deadline')
        if host_hash is not None:r.require(sha(dotnet)==host_hash,'SDK host changed')
        for name,digest in ci['code'].items():r.require(sha(kit/name)==digest,'Payload changed')
        for name,digest in expected_sources.items():r.require(sha(root/name)==digest,'Tracked source changed')
        if expected_sources:
            current_tree=snapshot(root,checkout=True)
            allowed=[]
            if source_seal is not None:
                require_snapshot(current_tree,source_seal,'Readonly checkout/output changed','checkout',snapshot_failures)
                allowed=[str(Path(p).parent/sub) for p in project_paths for sub in ('bin','obj')]
            if mutation_phase in ('restore','build'):
                allowed=[str(Path(p).parent/sub) for p in project_paths for sub in (('obj',) if mutation_phase=='restore' else ('bin','obj'))]
            for relative,entry in current_tree.items():
                if relative in expected_sources or (entry[0]=='dir' and relative in initial_dirs):continue
                r.require(any(relative==prefix or relative.startswith(prefix+'/') for prefix in allowed),'Unexpected checkout output')
        if source_seal is not None:require_snapshot(snapshot(root,checkout=True),source_seal,'Readonly checkout/output changed','checkout',snapshot_failures)
        if cache_seal is not None:require_snapshot(snapshot(work/'packages'),cache_seal,'Readonly cache changed','cache',snapshot_failures)
        for path,sealed in completed:require_snapshot(snapshot(path),sealed,'Prior evidence changed','prior-evidence',snapshot_failures)
        for path,(identity,digest) in stage_seals.items():
            r.require(file_identity(path)==identity and hashlib.sha256(read_regular(path,16384)).hexdigest()==digest,'Prior stage evidence changed')
    def run(label,argv,seconds,extra=None,fd=()):
        nonlocal current,mutation_phase
        context.update(stage=label,phase='pre-guard',ownerInvoked=False,terminal=None)
        current=label;mutation_phase=label if label in ('restore','build') else ('build' if source_seal is not None else 'none');guard();r.require(time.monotonic()-started+seconds+16<2100,'Insufficient owned command budget')
        context['phase']='owner-prepare'
        logs=work/(label+'-command');logs.mkdir();roots[logs]=directory_identity(logs)
        pipe=work/(label+'-pipe');pipe.mkdir();roots[pipe]=directory_identity(pipe);r.require(len(str(pipe).encode())+40<103,'Pipe path budget')
        child=dict(env,TESTINGPLATFORM_PIPE_DIRECTORY=str(pipe));child.update(extra or {})
        context.update(phase='owner-invoked',ownerInvoked=True)
        try:result=owner.run_owned(label,argv,root,child,logs,seconds,pass_fds=fd)
        except owner.CommandFailure as original:
            context['terminal']=original.outcome
            report_owner_failure(original,label)
        context.update(phase='receipt-guard',terminal=result)
        r.require(result['stage']==label and result['argv']==[str(a) for a in argv] and result['timeoutSeconds']==seconds and
            result['exitCode']==0 and result['timedOut'] is False and result['cancelled'] is False and
            result['errors']==result['cleanupErrors']==result['reportingErrors']==[] and result['remainingOwned']=={},'Unqualified command receipt')
        context['phase']='pipe-guard'
        r.require(not any(pipe.iterdir()),'Pipe retained entries')
        context['phase']='post-guard'
        guard();completed.append((logs,snapshot(logs)))
        context['phase']='command-qualified'
        print(json.dumps({'stage':label,'exit':0,'seconds':result['elapsedSeconds'],'remainingOwned':0}),flush=True)
        return logs/'stdout'
    try:
        dfd=owner.directory_fd(work)
        final=owner.open_exclusive(dfd,'FINAL.json');final_identity=file_identity(work/'FINAL.json')
        dotnet=Path(shutil.which('dotnet') or '')
        r.require(dotnet.is_absolute(),'Missing selected SDK host');dotnet=dotnet.resolve();host_hash=sha(dotnet)
        env=child_environment(work,dotnet)
        roots.update({p:directory_identity(p) for p in (work/'home',work/'tmp',work/'packages')})
        p=run('git-head',['git','rev-parse','HEAD'],10);r.require(read_regular(p).decode().strip()==prepare.BASE,'Wrong main HEAD')
        p=run('git-tree',['git','ls-tree','-rz','--full-tree','HEAD'],10);expected_sources=verify_tree(root,read_regular(p))
        initial=snapshot(root,checkout=True);initial_dirs={k for k,v in initial.items() if v[0]=='dir'}
        r.require({k for k,v in initial.items() if v[0]=='file'}==set(expected_sources),'Checkout has untracked source')
        prepare.prepare(root,kit)
        for name in r.read_json(kit/'SOURCE.json')['instrumented']:
            expected_sources[prepare.REL+name]=sha(root/prepare.REL/name)
        expected_sources[prepare.REL+'RuntimeAssemblyIntegrationTests.StreamExitStage.cs']=sha(root/prepare.REL/'RuntimeAssemblyIntegrationTests.StreamExitStage.cs')
        p=run('sdk-version',[dotnet,'--version'],30);sdk=read_regular(p).decode().strip();r.require(re.fullmatch(r'10\.0\.1\d\d',sdk),'Unexpected SDK band')
        config=work/'NuGet.config';config.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>')
        project=root/PROJECT;policy=['-p:DisableImplicitLibraryPacksFolder=true','-p:ContinuousIntegrationBuild=true','-p:PathMap='+str(root).rstrip('/')+'/=/_/']
        run('restore',[dotnet,'restore',project,'--configfile',config,*policy],600)
        context['phase']='restore-assets'
        assets=r.read_json(project.parent/'obj/project.assets.json')
        r.require(set(assets['project']['restore']['sources'])=={'https://api.nuget.org/v3/index.json'} and
                  'TUnit/1.73.19' in assets['libraries'] and 'Microsoft.Testing.Platform/2.5.1' in assets['libraries'],'Unexpected actual package identities')
        run('build',[dotnet,'build',project,'--no-restore','-c','Release','-m:1','-p:UseSharedCompilation=false','-nodeReuse:false',*policy],600)
        dll=project.parent/'bin/Release/net10.0/SharpLink.IntegrationTests.dll';binary=sha(dll)
        context['phase']='seal-built'
        source_seal=snapshot(root,checkout=True);cache_seal=snapshot(work/'packages')
        p=run('discovery',[dotnet,dll,'--list-tests','json','--treenode-filter',METHOD_FILTER],120)
        context['phase']='discovery-schema'
        ledger=r.discovery(r.parse_json(read_regular(p).decode('utf-8-sig')),r.read_json(kit/'SOURCE.json')['span'])
        print(json.dumps({'stage':'identity','base':prepare.BASE,'sdk':sdk,'binarySHA256':binary,'discovered':5,'selected':3,'ledgerSHA256':r.digest(ledger)}),flush=True)
        for mode in r.MODES:
            stages=work/(mode+'-stages');report=work/(mode+'-report')
            execute_body_with_stage(dfd,stages,report,roots,lambda stagefd:
                run(mode,report_args(dotnet,dll,ledger[mode]['uid'],report),150,
                    {'SHARPLINK_STREAM_EXIT_STAGE_FD':str(stagefd)},(stagefd,)),stage_sources)
            context['phase']='body-schema'
            guard()
            r.require(file_identity(stages)==stage_sources[stages],'Original stage receipt replaced')
            stage_bytes=read_regular(stages,16384);r.stage_records(stage_bytes,complete=True,mode=mode)
            stage_seals[stages]=(stage_sources[stages],hashlib.sha256(stage_bytes).hexdigest())
            r.report_inventory(snapshot(report))
            native=report/'session.tunit-report.json';junit=report/'junit.xml'
            verdict=r.passed(r.parse_json(read_regular(native).decode('utf-8-sig')),read_regular(junit).decode(),ledger[mode],seen,runtime)
            seen.add(verdict['generation']);runtime=verdict['runtime'];qualified.append({'mode':mode,**verdict})
            completed.append((report,snapshot(report)));guard()
            print(json.dumps({'stage':mode,'classification':'SHORT_CASE_PASS','uidSHA256':verdict['uidSHA256']}),flush=True)
        r.require(len(qualified)==3,'Missing selected cases')
    except BaseException as exc:
        errors.append(exc);failure_context=dict(context)
    finally:
        if failure_context is None:failure_context=dict(context,phase='final-guard')
        try:guard()
        except BaseException as exc:errors.append(exc)
        if stages is not None:
            try:
                directory_identity(work,roots[work])
                r.require(stages in stage_sources and file_identity(stages)==stage_sources[stages],'Original last-stage receipt replaced')
                raw=read_regular(stages,16384);records=r.stage_records(raw)
                print(json.dumps({'stage':'last-stage','recordCount':len(records),'lastStage':records[-1][0] if records else None,'sha256':hashlib.sha256(raw).hexdigest()}),flush=True)
            except BaseException as exc:errors.append(exc)
        if errors:emit_failure_diagnostic(errors,failure_context,snapshot_failures,set(expected_sources)|{'.'})
        if final is not None:
            try:
                directory_identity(work,roots[work])
                r.require(file_identity(work/'FINAL.json')==final_identity,'Final receipt identity changed')
                owner.write_json_fd(final,{'classification':'SHORT_DIAGNOSTIC_FAILED' if errors else 'SHORT_CASES_CLASSIFIED_PENDING_REPORT_CLOSE',
                    'base':prepare.BASE,'stage':current,'qualified':qualified,'binarySHA256':binary,'errors':exception_graph(errors)})
            except BaseException as exc:errors.append(exc)
        for fd in (final,dfd):
            if fd is not None:
                try:os.close(fd)
                except BaseException as exc:errors.append(exc)
    if errors:
        # Never expose exception messages, raw reports, argv, paths or stack frames.
        print(json.dumps({'classification':'SHORT_DIAGNOSTIC_FAILED','stage':current,'qualified':len(qualified),
                          'errorTypes':[type(e).__name__ for e in errors[:8]],'errorsTruncated':len(errors)>8}),flush=True)
        raise SystemExit(1)
    print(json.dumps({'classification':'SHORT_THREE_CASE_PASS_NOT_SOAK','cases':3,'commands':9,'ownedJoin':True}),flush=True)


if __name__=='__main__':
    try:main(Path(sys.argv[1]))
    except SystemExit:raise
    except BaseException as exc:
        try:print(json.dumps({'classification':'SHORT_PREFLIGHT_FAILED','errorType':type(exc).__name__}),flush=True)
        except BaseException:pass
        raise SystemExit(1)
