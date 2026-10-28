"""One fixed ARM failed-prepare recovery. Byte validation only: no process/network/build.
Original files are restored once and never rewritten; new evidence has new paths.
"""
import json,os,platform,shutil,sys,tarfile
from pathlib import Path

BUILD_RUN=40716132624
BUILD_COMMIT='2b59e359bff99ec8f7d5b1de158179d6eb9fa4f0'
FAILURE_ARTIFACT=12828441906
FAILURE_TAR_SHA='8fbd4fb3cf24c7859aa184fbd5c65718aa5bbf58c0f4f73f4d96db7f2dfffd70'
OLD_PLAN_SHA='00abc7e4eec6ff0d4c197e83a01c9bef49e69d794136e11ee8b15ec5634de0e4'
OLD_TOOLS_SHA='7610cc632c1def4ca8593b4cce9d199dc409cdb54438fb67087c620573104117'
OLD_DRIVER_SHA='d702b3314fb81958b9b880b0c4d0ad83653a62c06298f53c22b6c338ea76f7bb'
OLD_WORKFLOW_SHA='e7f5a1e5dba9666a9c5d0558c783543c07bfeef9f17ba2107efaafed95076300'

def check_original_tools(d,root):
    for path,wanted in [('tools/native/plan.json',OLD_PLAN_SHA),('tools/native/tool-lock.json',OLD_TOOLS_SHA),('tools/hosted/driver.py',OLD_DRIVER_SHA),('tools/workflow.yml',OLD_WORKFLOW_SHA)]:
        if d.digest(root/path)!=wanted:raise ValueError('Original prepared tool identity differs: '+path)

def verify_recorded_sdk(d,root,archive):
    # build-sdk was deliberately not shipped; verify recorded installed member
    # identities, not an invented claim to have re-inspected the old build machine.
    recorded=json.loads((root/'evidence/sdk-members.json').read_text());remaining=set(recorded)
    with tarfile.open(archive,'r:gz') as tar:
        seen=set()
        for member in tar:
            name=member.name.removeprefix('./')
            if name not in recorded:continue
            if name in seen or not member.isfile():raise ValueError('Ambiguous SDK member identity')
            seen.add(name)
            with tar.extractfile(member) as stream:
                import hashlib
                actual=hashlib.file_digest(stream,'sha256').hexdigest()
            if actual!=recorded[name]:raise ValueError('Recorded SDK member differs from official archive')
            remaining.remove(name)
    if not recorded or remaining:raise ValueError('Incomplete recorded SDK member closure')

def recover(d,root,r):
    from receipt import check_archives,verify_runtime_overlay
    d.stage_environment('revalidate') # Record/check inherited values, launch nothing.
    if d.RID!='osx-arm64' or (platform.system(),platform.machine())!=('Darwin','arm64'):
        raise ValueError('Fixed ARM revalidation requires standard native macOS ARM host')
    # restore_bundle checks the frozen input digest and rejects non-regular or
    # escaping members. File open xb rejects duplicate/aliased archive paths.
    d.restore_bundle(d.REPO/'artifacts/wakeup-input/failed-stage-files.tar.gz',root,FAILURE_TAR_SHA)
    check_original_tools(d,root)
    original=json.loads((root/'receipts/D.json').read_text())
    host=json.loads((root/'evidence/host.json').read_text())
    if host['workflowCommit']!=BUILD_COMMIT or int(host['runId'])!=BUILD_RUN or host['rid']!='osx-arm64':
        raise ValueError('Original build host/run binding differs')
    for name in ('build-D','build-C','build-decoder','source-clean-D','source-clean-C'):
        exit_record=json.loads((root/'evidence'/(name+'.exit.json')).read_text())
        if exit_record!={'exitCode':0,'timedOut':False}:raise ValueError('Original build/clean check did not succeed')
    plan=json.loads((d.NATIVE/'plan.json').read_text());lock=json.loads((d.NATIVE/'artifact-lock.json').read_text())
    archives=check_archives(original['archiveVerification'],lock,'osx-arm64')
    manifest=verify_runtime_overlay(archives,root/'dotnet')
    verify_recorded_sdk(d,root,archives['sdk'])
    # New validator material lives alongside, not over, original tools/receipts.
    evidence=root/'revalidation';evidence.mkdir(exist_ok=False)
    d.write(evidence/'runtime-final-members.json',manifest)
    for name in ('environment-original-revalidate.json','environment-policy-revalidate.json'):shutil.copy2(d.OUT/name,evidence/name)
    shutil.copytree(d.NATIVE,evidence/'tools/native',ignore=shutil.ignore_patterns('__pycache__','bin','obj'))
    shutil.copytree(d.HERE,evidence/'tools/hosted',ignore=shutil.ignore_patterns('__pycache__'))
    shutil.copy2(d.REPO/'.github/workflows/wakeup-native-hosted.yml',evidence/'tools/workflow.yml')
    provenance={'schema':1,'originalBuildRunId':BUILD_RUN,'originalBuildCommit':BUILD_COMMIT,'originalFailedArtifactId':FAILURE_ARTIFACT,'originalFailureTarSHA256':FAILURE_TAR_SHA,'originalReceiptSHA256':d.digest(root/'receipts/D.json'),'originalReceiptStatus':'FAILED; retained unchanged','revalidationRunId':int(os.environ['GITHUB_RUN_ID']),'validationToolCommit':os.environ['GITHUB_SHA'],'newPlanSHA256':d.PLAN_SHA,'newToolsSHA256':d.TOOLS_SHA,'originalPlanSHA256':OLD_PLAN_SHA,'originalToolsSHA256':OLD_TOOLS_SHA,'revalidationHost':{'uname':{key:getattr(os.uname(),key) for key in ('sysname','nodename','release','version','machine')},'python':sys.version,'imageVersion':os.environ.get('ImageVersion')},'buildExecuted':False,'clrExecuted':False,'binaryBytesChanged':False,'sdkIdentityLimit':'Recorded original build-sdk member hashes checked against official archive; original installed SDK directory was not retained'}
    d.write(evidence/'provenance.json',provenance)
    files={p for p in d.tree(root) if 'archives' not in p.relative_to(root).parts}
    receipts={};receipt_files={}
    for arm,revision in [('D',plan['baseline']),('C',plan['candidate'])]:
        source=root/('source-'+arm);probe=root/('build-'+arm)/'probe'
        receipt={**original,'arm':arm,'sourceCommit':revision,'sourceRoot':str(source),'probeDll':str(probe/'bin/Release/net10.0/WakeupNativeProbe.dll'),'generatedRoot':str(probe/'obj/Generated'),'buildLog':str(root/'evidence'/('build-'+arm+'.log')),'files':[{'path':str(p),'sha256':d.digest(p)} for p in sorted(files)]}
        name='receipts/revalidated-'+arm+'.json';path=root/name
        d.write(path,receipt);h=d.digest(path);d.validate_receipt(path,h,plan,lock);receipts[arm]=h;receipt_files[arm]=name
    index={'rid':'osx-arm64','runId':BUILD_RUN,'prepareCommit':BUILD_COMMIT,'planSha256':OLD_PLAN_SHA,'toolsSha256':OLD_TOOLS_SHA,'receipts':receipts,'receiptFiles':receipt_files,'revalidationRunId':provenance['revalidationRunId'],'validationToolCommit':provenance['validationToolCommit'],'revalidationPlanSha256':d.PLAN_SHA,'revalidationToolsSha256':d.TOOLS_SHA}
    d.write(root/'prepared-index.json',index)
    files|={root/path for path in receipt_files.values()}|{root/'prepared-index.json'}
    with tarfile.open(d.OUT/'prepared.tar.gz','w:gz') as tar:
        for p in sorted(files):tar.add(p,arcname=str(p.relative_to(root)),recursive=False)
    d.write(d.OUT/'review-summary.json',{**index,'bundleSha256':d.digest(d.OUT/'prepared.tar.gz'),'bundleBytes':(d.OUT/'prepared.tar.gz').stat().st_size,'noBuildOrClrExecuted':True,'originalFailureRetained':True,'independentReviewRequired':True})
