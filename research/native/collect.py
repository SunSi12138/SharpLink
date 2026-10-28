#!/usr/bin/env python3
"""Reviewed, mechanism-only per-cell collector. Does not download or build anything.
An independently checked build receipt is required; run only after explicit plan review.
"""
import argparse, hashlib, json, os, platform, signal, subprocess, time
from pathlib import Path
from inventory import inventory
from receipt import validate_receipt

ROOT=Path(__file__).resolve().parent

def sha(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()

def validate_environment(env):
    allowed={'DOTNET_ROOT','DOTNET_CLI_HOME','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_SKIP_FIRST_TIME_EXPERIENCE'}
    rejected=[k for k in env if k.upper().startswith(('COMPLUS_', 'CORECLR_', 'COREHOST_', 'COR_', 'MONO_', 'DYLD_', 'LD_')) or (k.upper().startswith('DOTNET_') and k not in allowed)]
    if rejected:raise ValueError('Inherited runtime configuration rejected: '+','.join(sorted(rejected)))

def main():
    p=argparse.ArgumentParser();p.add_argument('--reviewed-plan-sha256',required=True);p.add_argument('--reviewed-tools-sha256',required=True);p.add_argument('--reviewed-receipt-sha256',required=True);p.add_argument('--receipt',type=Path,required=True)
    p.add_argument('--cell',required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
    if sha(ROOT/'plan.json')!=args.reviewed_plan_sha256:raise ValueError('Reviewed plan hash mismatch')
    if sha(ROOT/'tool-lock.json')!=args.reviewed_tools_sha256:raise ValueError('Reviewed tool lock mismatch')
    for name,digest in json.loads((ROOT/'tool-lock.json').read_text()).items():
        if sha(ROOT/name)!=digest:raise ValueError('Tool content changed: '+name)
    plan=json.loads((ROOT/'plan.json').read_text())
    if args.cell not in plan['cells']:raise ValueError('Unknown cell')
    validate_environment(os.environ)
    runtime_manifest={}
    r=validate_receipt(args.receipt,args.reviewed_receipt_sha256,plan,json.loads((ROOT/'artifact-lock.json').read_text()),runtime_manifest=runtime_manifest)
    rid=r['rid'];machine=plan['rids'][rid]['machine']
    if platform.machine()!=machine:raise ValueError('Host machine mismatch')
    if (rid=='linux-x64' and platform.system()!='Linux') or (rid=='osx-arm64' and platform.system()!='Darwin'):raise ValueError('Host OS mismatch')
    dotnet=Path(r['dotnet']);dll=Path(r['probeDll'])
    out=args.output.resolve();out.mkdir(parents=True,exist_ok=False)
    (out/'build-receipt.json').write_text(json.dumps(r,indent=2))
    (out/'runtime-final-members.json').write_text(json.dumps(runtime_manifest,indent=2))
    env=os.environ.copy();env.update({'DOTNET_ROOT':str(dotnet.parent),'DOTNET_JitDisasm':plan['nativeFilter'],'DOTNET_JitStdOutFile':str(out/'jit.txt'),
        'DOTNET_EnableEventPipe':'1','DOTNET_EventPipeOutputPath':str(out/'trace.nettrace'),
        'DOTNET_EventPipeConfig':plan['providers'],'DOTNET_EventPipeCircularMB':'64','DOTNET_EventPipeOutputStreaming':'1'})
    cmd=[str(dotnet),str(dll),args.cell,plan['rids'][rid]['architecture'],str(out/'phases.jsonl')]
    (out/'launch.json').write_text(json.dumps({'command':cmd,'planSha256':args.reviewed_plan_sha256,'toolLockSha256':args.reviewed_tools_sha256,'reviewedReceiptSha256':args.reviewed_receipt_sha256,'machine':platform.uname()._asdict(),
      'observationEnvironment':{k:v for k,v in env.items() if k.startswith(('DOTNET_','COMPlus_'))},'instrumentationNotTiming':True},indent=2))
    reason=None;code=None;child=None
    try:
        with (out/'stdout.jsonl').open('wb') as stdout,(out/'stderr.txt').open('wb') as stderr:
            child=subprocess.Popen(cmd,env=env,stdout=stdout,stderr=stderr,start_new_session=True)
            start=time.monotonic()
            while child.poll() is None:
                if time.monotonic()-start>plan['processTimeoutSeconds']:reason='fixed-timeout'
                if any(f.is_file() and f.stat().st_size>plan['perRawFileCapBytes'] for f in out.iterdir()):reason='fixed-file-cap'
                if reason:
                    os.killpg(child.pid,signal.SIGTERM)
                    try:child.wait(timeout=5)
                    except subprocess.TimeoutExpired:os.killpg(child.pid,signal.SIGKILL);child.wait()
                    break
                time.sleep(.1)
            code=child.returncode
        if (out/'jit.txt').exists():
            (out/'inventory.json').write_text(json.dumps(inventory((out/'jit.txt').read_text(errors='replace')),indent=2))
        if any(f.is_file() and f.stat().st_size>plan['perRawFileCapBytes'] for f in out.iterdir()):reason=reason or 'fixed-file-cap'
        if code==0:
            records=[json.loads(x) for x in (out/'stdout.jsonl').read_text().splitlines() if x.startswith('{')]
            if sum(x.get('complete') is True and x.get('completed')==40000 for x in records)!=1:reason=reason or 'missing-completion-ledger'
            if not (out/'jit.txt').is_file() or not (out/'trace.nettrace').is_file():reason=reason or 'missing-raw-evidence'
    except BaseException as ex:
        reason=reason or type(ex).__name__+': '+str(ex)
    finally:
        if child is not None and child.poll() is None:
            os.killpg(child.pid,signal.SIGKILL);child.wait()
        (out/'result.json').write_text(json.dumps({'exitCode':code,'failure':reason,'nativeAccepted':False,
            'status':'REVIEW_REQUIRED' if code==0 and reason is None else 'INCOMPLETE_KEEP_FAILED_SAMPLE'},indent=2))
        (out/'manifest.json').write_text(json.dumps({str(f.relative_to(out)):sha(f) for f in sorted(out.rglob('*')) if f.is_file() and f.name!='manifest.json'},indent=2))
    return 0 if code==0 and reason is None else 1

if __name__=='__main__':
    raise SystemExit(main())
