#!/usr/bin/env python3
"""Hosted preparation / immutable restore only. No timing mode or automatic retries."""
import hashlib,json,os,platform,re,shutil,signal,subprocess,sys,tarfile,time,urllib.request
from pathlib import Path
HERE=Path(__file__).resolve().parent
REPO=HERE.parents[1]
NATIVE=REPO/'research/native'
sys.path.insert(0,str(NATIVE))
from receipt import digest,tree,validate_receipt,verify_installed
from collect import validate_environment
PLAN_SHA='00abc7e4eec6ff0d4c197e83a01c9bef49e69d794136e11ee8b15ec5634de0e4'
TOOLS_SHA='7610cc632c1def4ca8593b4cce9d199dc409cdb54438fb67087c620573104117'
RID=os.environ.get('EVIDENCE_RID','')
ROOTS={'linux-x64':Path('/tmp/sharplink-wakeup-native-v1'),'osx-arm64':Path('/private/tmp/sharplink-wakeup-native-v1')}
OUT=REPO/'artifacts/wakeup-output'

def write(path,value):
    path.parent.mkdir(parents=True,exist_ok=True)
    with path.open('x') as f:json.dump(value,f,indent=2)

def sha_valid(value):return type(value) is str and re.fullmatch('[0-9a-f]{64}',value) is not None

def request():
    if RID not in ROOTS:raise ValueError('Unapproved RID')
    if sys.version_info<(3,11):raise ValueError('Python3.11+ required for reviewed hash/tar APIs')
    r=json.loads((HERE/'REQUEST.json').read_text())
    if type(r.get('schema')) is not int or r.get('schema')!=1 or r.get('stage') not in ('prepare','collect'):raise ValueError('Invalid request')
    if os.environ.get('GITHUB_RUN_ATTEMPT')!='1':raise ValueError('Automatic/manual reruns prohibited; amend and review a new plan')
    if os.environ.get('GITHUB_REF_NAME')!='codex/wakeup-native-'+r['stage']+'-v1':raise ValueError('Wrong stage branch')
    if r['stage']=='prepare':
        if set(r)!={'schema','stage'}:raise ValueError('Unexpected prepare request key')
    else:
        if set(r)!={'schema','stage','prepareRunId','prepareCommit','architectures'}:raise ValueError('Invalid collect request keys')
        if type(r['prepareRunId']) is not int or r['prepareRunId']<=0 or not re.fullmatch('[0-9a-f]{40}',r['prepareCommit']):raise ValueError('Invalid prepared run identity')
        if set(r['architectures'])!=set(ROOTS):raise ValueError('Both frozen architectures required')
        for rid,a in r['architectures'].items():
            if set(a)!={'bundleSha256','DReceiptSha256','CReceiptSha256'} or not all(sha_valid(x) for x in a.values()):raise ValueError('Invalid reviewed digests')
    if digest(NATIVE/'plan.json')!=PLAN_SHA or digest(NATIVE/'tool-lock.json')!=TOOLS_SHA:raise ValueError('Native tools changed')
    for file,h in json.loads((NATIVE/'tool-lock.json').read_text()).items():
        if digest(NATIVE/file)!=h:raise ValueError('Native tool content mismatch')
    return r

def host(root):
    expected={'linux-x64':('Linux','x86_64'),'osx-arm64':('Darwin','arm64')}[RID]
    if (platform.system(),platform.machine())!=expected:raise ValueError('Actual host architecture/OS mismatch')
    if RID=='linux-x64':
        release=Path('/etc/os-release').read_text()
        if 'ID=ubuntu' not in release or 'VERSION_ID="24.04"' not in release:raise ValueError('Expected Ubuntu24.04, not a local substitute')
    else:
        release=subprocess.check_output(['sw_vers'],text=True)
        version=subprocess.check_output(['sw_vers','-productVersion'],text=True).strip()
        if version.split('.')[0]!='14':raise ValueError('Expected macOS14')
    cpu={'logicalProcessors':os.cpu_count()}
    if RID=='linux-x64':
        cpu['affinity']=sorted(os.sched_getaffinity(0))
        for name in ('cpuinfo','self/status'):
            cpu[name]=Path('/proc',name).read_text()
        for name in ('cpu.max','cpuset.cpus.effective'):
            path=Path('/sys/fs/cgroup')/name;cpu[name]=path.read_text() if path.exists() else 'unavailable'
    else:
        command=['sysctl','hw.model','hw.ncpu','hw.logicalcpu','hw.physicalcpu','machdep.cpu.brand_string']
        result=subprocess.run(command,capture_output=True,text=True);cpu['sysctl']={'command':command,'exitCode':result.returncode,'stdout':result.stdout,'stderr':result.stderr}
    write(root/'evidence/host.json',{'pythonVersion':sys.version,'pythonExecutable':sys.executable,'cpu':cpu,'rid':RID,'uname':platform.uname()._asdict(),'release':release,'imageVersion':os.environ.get('ImageVersion'),'imageOS':os.environ.get('ImageOS'),'runnerArch':os.environ.get('RUNNER_ARCH'),'workflowCommit':os.environ['GITHUB_SHA'],'runId':os.environ['GITHUB_RUN_ID'],'runAttempt':os.environ['GITHUB_RUN_ATTEMPT'],'kernelScheduling':'not collected'})

def download_archives(root):
    lock=json.loads((NATIVE/'artifact-lock.json').read_text());rows=[]
    for row in lock['artifacts']:
        if row['rid']!=RID:continue
        target=root/'archives'/row['name'];target.parent.mkdir(parents=True,exist_ok=True)
        if target.exists():raise ValueError('Archive path already exists')
        start=time.monotonic();total=0
        with urllib.request.urlopen(row['url'],timeout=15) as response,target.open('xb') as output:
            while chunk:=response.read(1024*1024):
                total+=len(chunk)
                if total>512*1024*1024 or time.monotonic()-start>120:raise TimeoutError('Fixed archive download budget exceeded; retain partial file')
                output.write(chunk)
        if digest(target,'sha512')!=row['hash']:raise ValueError('Official archive SHA512 mismatch')
        rows.append({'component':row['component'],'path':str(target),'sha512':row['hash'],'officialUrl':row['url']})
    return rows

def extract(archive,target):
    # Python data filter rejects traversal and unsafe special/link destinations.
    with tarfile.open(archive,'r:gz') as tar:tar.extractall(target,filter='data')

def run(command,log,env=None,cwd=None,timeout=300):
    write(log.with_suffix('.command.json'),command)
    timed_out=False
    with log.open('xb') as output:
        child=subprocess.Popen(command,stdout=output,stderr=subprocess.STDOUT,env=env,cwd=cwd,start_new_session=True)
        try:child.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out=True
            # SIGINT lets the reviewed Python collector run its finally block and
            # terminate its separately owned probe group before a hard fallback.
            try:os.killpg(child.pid,signal.SIGINT)
            except ProcessLookupError:pass
            try:child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                try:os.killpg(child.pid,signal.SIGKILL)
                except ProcessLookupError:pass
                child.wait()
    write(log.with_suffix('.exit.json'),{'exitCode':child.returncode,'timedOut':timed_out})
    if child.returncode or timed_out:raise RuntimeError('Command failed; raw evidence retained: '+str(log))

def check_runtime_info(log):
    text=log.read_text();arch='x64' if RID=='linux-x64' else 'arm64'
    if not re.search(r'Host:\s+Version:\s+10\.0\.14\s+Architecture:\s+'+arch+r'\b',text):raise ValueError('Actual dotnet host version/architecture mismatch')

def prepare(root):
    inherited_env=os.environ.copy();validate_environment(inherited_env)
    root.mkdir(exist_ok=False);host(root)
    plan=json.loads((NATIVE/'plan.json').read_text());lock=json.loads((NATIVE/'artifact-lock.json').read_text())
    archives=download_archives(root);by={row['component']:Path(row['path']) for row in archives}
    sdk=root/'build-sdk';runtime=root/'dotnet';sdk.mkdir();runtime.mkdir()
    for component in ('sdk','runtime','aspnetcore-runtime'):extract(by[component],sdk)
    for component in ('runtime','aspnetcore-runtime'):extract(by[component],runtime)
    sdk_files=tree(sdk/'sdk')|tree(sdk/'packs')|tree(sdk/'templates')
    verify_installed(by['sdk'],sdk,sdk_files)
    write(root/'evidence/sdk-members.json',{str(p.relative_to(sdk)):digest(p) for p in sorted(sdk_files)})
    shutil.copytree(NATIVE,root/'tools/native',ignore=shutil.ignore_patterns('__pycache__','bin','obj'))
    shutil.copytree(HERE,root/'tools/hosted',ignore=shutil.ignore_patterns('__pycache__'))
    shutil.copy2(REPO/'.github/workflows/wakeup-native-hosted.yml',root/'tools/workflow.yml')
    shutil.copy2(REPO/'global.json',root/'global.json')
    env=inherited_env.copy();env.update({'DOTNET_ROOT':str(sdk),'DOTNET_CLI_HOME':str(root/'cli-home'),'NUGET_PACKAGES':str(root/'packages'),'NUGET_HTTP_CACHE_PATH':str(root/'nuget-http'),'XDG_DATA_HOME':str(root/'xdg'),'XDG_RUNTIME_DIR':str(root/'runtime-dir'),'MSBUILDDISABLENODEREUSE':'1','UseSharedCompilation':'false'})
    (root/'runtime-dir').mkdir()
    write(root/'evidence/build-environment.json',{k:v for k,v in env.items() if k.startswith(('DOTNET_','NUGET_','MSBUILD')) or k in ('XDG_RUNTIME_DIR','XDG_DATA_HOME','UseSharedCompilation')})
    run([str(sdk/'dotnet'),'--info'],root/'evidence/dotnet-info.log',env,root)
    check_runtime_info(root/'evidence/dotnet-info.log')
    for arm,revision in [('D',plan['baseline']),('C',plan['candidate'])]:
        source=root/('source-'+arm);probe=root/('build-'+arm)/'probe'
        run(['git','clone','--local','--no-hardlinks','--no-checkout',str(REPO),str(source)],root/'evidence'/('clone-'+arm+'.log'))
        run(['git','-C',str(source),'checkout','--detach',revision],root/'evidence'/('checkout-'+arm+'.log'))
        shutil.copytree(NATIVE/'probe',probe)
        run([str(sdk/'dotnet'),'build',str(probe/'WakeupNativeProbe.csproj'),'-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false','-p:EvidenceSourceRoot='+str(source)],root/'evidence'/('build-'+arm+'.log'),env,root)
        run(['git','-C',str(source),'diff','--exit-code'],root/'evidence'/('source-clean-'+arm+'.log'))
    shutil.copytree(NATIVE/'decoder',root/'decoder')
    run([str(sdk/'dotnet'),'build',str(root/'decoder/TraceDecoder.csproj'),'-c','Release','-m:1','-nr:false','-p:UseSharedCompilation=false'],root/'evidence/build-decoder.log',env,root)
    # Build/package identities retained without transferring the SDK/package caches.
    write(root/'evidence/package-identities.json',{str(p.relative_to(root/'packages')):digest(p) for p in sorted((root/'packages').rglob('*')) if p.is_file() and p.name.endswith(('.nupkg','.nupkg.sha512','.nuspec'))})
    receipts={};allfiles=set();common=tree(runtime)|tree(root/'tools')|tree(root/'evidence')|tree(root/'decoder')|{root/'global.json'}
    for arm,revision in [('D',plan['baseline']),('C',plan['candidate'])]:
        source=root/('source-'+arm);probe=root/('build-'+arm)/'probe'
        files=common|tree(probe)|tree(source/'src',skip=('bin','obj'))
        files|={p for p in source.iterdir() if p.is_file() and (p.suffix in {'.props','.targets'} or p.name.lower() in {'global.json','nuget.config'})}
        files|={p for p in (source/'src').rglob('*') if p.is_file() and ('bin' in p.parts or 'obj' in p.parts) and p.suffix in {'.cs','.dll','.pdb','.json','.props','.targets'}}
        receipt={'schema':2,'arm':arm,'rid':RID,'sourceCommit':revision,'sdkVersion':plan['sdk'],'runtimeVersion':plan['runtime'],'dotnet':str(runtime/'dotnet'),'probeDll':str(probe/'bin/Release/net10.0/WakeupNativeProbe.dll'),'sourceRoot':str(source),'generatedRoot':str(probe/'obj/Generated'),'buildLog':str(root/'evidence'/('build-'+arm+'.log')),'buildSucceeded':True,'productDiffReviewed':True,'archiveVerification':archives,'files':[{'path':str(p),'sha256':digest(p)} for p in sorted(files)]}
        path=root/'receipts'/(arm+'.json');write(path,receipt);h=digest(path);validate_receipt(path,h,plan,lock);receipts[arm]=h;allfiles|=files|{path}
    index={'rid':RID,'runId':int(os.environ['GITHUB_RUN_ID']),'prepareCommit':os.environ['GITHUB_SHA'],'planSha256':PLAN_SHA,'toolsSha256':TOOLS_SHA,'receipts':receipts}
    write(root/'prepared-index.json',index);allfiles.add(root/'prepared-index.json')
    OUT.mkdir(parents=True,exist_ok=True)
    with tarfile.open(OUT/'prepared.tar.gz','w:gz') as tar:
        for path in sorted(allfiles):tar.add(path,arcname=str(path.relative_to(root)),recursive=False)
    write(OUT/'review-summary.json',{**index,'bundleSha256':digest(OUT/'prepared.tar.gz'),'bundleBytes':(OUT/'prepared.tar.gz').stat().st_size,'noProbeExecuted':True})

def restore_bundle(bundle,root,expected):
    if digest(bundle)!=expected:raise ValueError('Reviewed bundle digest mismatch')
    root.mkdir(exist_ok=False)
    with tarfile.open(bundle,'r:gz') as tar:
        for m in tar:
            rel=Path(m.name)
            if not m.isfile() or rel.is_absolute() or '..' in rel.parts:raise ValueError('Bundle must contain only regular relative files')
            target=root/rel;target.parent.mkdir(parents=True,exist_ok=True)
            with tar.extractfile(m) as source,target.open('xb') as out:shutil.copyfileobj(source,out)
            target.chmod(m.mode&0o777)

def collect(root,r):
    inherited_env=os.environ.copy();validate_environment(inherited_env)
    a=r['architectures'][RID];restore_bundle(REPO/'artifacts/wakeup-input/prepared.tar.gz',root,a['bundleSha256'])
    index=json.loads((root/'prepared-index.json').read_text())
    if index['runId']!=r['prepareRunId'] or index['prepareCommit']!=r['prepareCommit'] or index['rid']!=RID:raise ValueError('Wrong prepared run/binary identity')
    if digest(HERE/'driver.py')!=digest(root/'tools/hosted/driver.py') or digest(REPO/'.github/workflows/wakeup-native-hosted.yml')!=digest(root/'tools/workflow.yml'):raise ValueError('Collect wrapper/workflow differs from prepared reviewed version')
    host(root/'collect-host')
    download_archives(root) # Exact approved archive bytes; no SDK extraction/build here.
    plan=json.loads((NATIVE/'plan.json').read_text());lock=json.loads((NATIVE/'artifact-lock.json').read_text())
    for arm in ('D','C'):
        if index['receipts'][arm]!=a[arm+'ReceiptSha256']:raise ValueError('Receipt approval mismatch')
        validate_receipt(root/'receipts'/(arm+'.json'),a[arm+'ReceiptSha256'],plan,lock)
    run([str(root/'dotnet/dotnet'),'--info'],root/'collect-host/evidence/dotnet-info.log')
    check_runtime_info(root/'collect-host/evidence/dotnet-info.log')
    env=inherited_env
    for cell in plan['cells']:
        for arm in plan['armOrder']:
            result=root/'results'/(cell+'-'+arm)
            cmd=[sys.executable,str(root/'tools/native/collect.py'),'--reviewed-plan-sha256',PLAN_SHA,'--reviewed-tools-sha256',TOOLS_SHA,'--reviewed-receipt-sha256',a[arm+'ReceiptSha256'],'--receipt',str(root/'receipts'/(arm+'.json')),'--cell',cell,'--output',str(result)]
            # Nonzero aborts remaining cells; no retry/replacement, no adaptive warming.
            run(cmd,root/'collection-launches'/(cell+'-'+arm+'.log'),env,timeout=210)
            run([str(root/'dotnet/dotnet'),str(root/'decoder/bin/Release/net10.0/TraceDecoder.dll'),str(result/'trace.nettrace'),str(result/'decoded')],root/'collection-launches'/(cell+'-'+arm+'-decode.log'),env,timeout=30)

def main():
    OUT.mkdir(parents=True,exist_ok=True)
    try:
        r=request();mode=sys.argv[1]
        if mode=='request':
            with open(os.environ['GITHUB_OUTPUT'],'a') as f:
                f.write('stage='+r['stage']+'\n')
                if r['stage']=='collect':f.write('prepare_run_id='+str(r['prepareRunId'])+'\nprepare_artifact=wakeup-prepare-'+RID+'-'+str(r['prepareRunId'])+'\n')
        elif mode==r['stage']:
            root=ROOTS[RID]
            if root.exists():raise ValueError('Reserved restored root already exists; do not inspect or overwrite it')
            failed=True
            try:
                if mode=='prepare':prepare(root)
                else:collect(root,r)
                failed=False
            finally:
                if failed and root.exists():
                    # Keep partial prepared/generated/runtime/archive bytes, not just the failure message.
                    excluded={'build-sdk','packages','cli-home','nuget-http','xdg','runtime-dir','.git'}
                    with tarfile.open(OUT/'failed-stage-files.tar.gz','w:gz') as tar:
                        for path in sorted(root.rglob('*')):
                            if path.is_file() and not path.is_symlink() and not any(part in excluded for part in path.relative_to(root).parts):tar.add(path,arcname=str(path.relative_to(root)),recursive=False)
                for name in ('evidence','results','collection-launches','collect-host'):
                    if (root/name).exists():shutil.copytree(root/name,OUT/name)
        else:raise ValueError('Stage/mode mismatch')
    except BaseException as error:
        write(OUT/('failure-'+sys.argv[1]+'.json'),{'type':type(error).__name__,'message':str(error),'stage':sys.argv[1] if len(sys.argv)>1 else None,'nativePerformanceClaim':False})
        raise
    finally:
        write(OUT/('artifact-manifest-'+sys.argv[1]+'.json'),{str(p.relative_to(OUT)):digest(p) for p in sorted(OUT.rglob('*')) if p.is_file()})

if __name__=='__main__':main()
