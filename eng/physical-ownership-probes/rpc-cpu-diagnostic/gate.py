#!/usr/bin/env python3
"""Hosted-only one-shot capability gate. No installation or permission modification."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from verify_request import main as verify_request

def main():
    verify_request()
    output = Path(sys.argv[1]).resolve(); output.mkdir(parents=True, exist_ok=False)
    os.environ['LC_ALL']='C'
    perf = shutil.which('perf')
    metadata = {'perf': perf, 'uid': os.geteuid(), 'uname': list(os.uname()),
                'paranoid': Path('/proc/sys/kernel/perf_event_paranoid').read_text().strip(),
                'status': [x for x in Path('/proc/self/status').read_text().splitlines() if x.startswith(('Cap','Seccomp'))],
                'image': {k: os.environ.get(k) for k in ('ImageOS','ImageVersion','RUNNER_OS','RUNNER_ARCH','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT')}}
    (output/'host.json').write_text(json.dumps(metadata,indent=2)+'\n')
    effective=[x for x in metadata['status'] if x.startswith('CapEff:')]
    if metadata['uid']==0 or len(effective)!=1 or int(effective[0].split()[1],16)!=0:
        raise RuntimeError('Unprivileged uid and zero effective capabilities required; STOP')
    if not perf: raise RuntimeError('Existing perf missing; STOP, no install or other host')
    with (output/'version.txt').open('w') as log:
        subprocess.run([perf,'--version'],stdout=log,stderr=subprocess.STDOUT,check=True,timeout=15)
    command=[perf,'stat','-e','task-clock:u','--','/usr/bin/true']
    with (output/'capability.txt').open('w') as log:
        result=subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,timeout=15)
    text=(output/'capability.txt').read_text()
    passed=result.returncode==0 and not any(s in text.lower() for s in ('not supported','not counted','permission','access denied'))
    (output/'gate.json').write_text(json.dumps({'passed':passed,'command':command,'exitCode':result.returncode,'perf':perf})+'\n')
    if not passed: raise RuntimeError('One user-only task-clock gate failed; STOP without retry/alternative')
if __name__=='__main__': main()
