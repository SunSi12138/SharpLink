"""Only mutate a clean disposable exact-main checkout; no remote/git ref writes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

BASE='9d1635c8988870ee295098fe9e65cb49924013d2'
COORDINATOR='src/SharpLink.Server/ServerLifecycleCoordinator.cs'
FIXTURE='test/SharpLink.UnitTests/Server/ServerTerminalSignalObservationTests.cs'
PAYLOAD=Path(__file__).resolve().parent


def require(value,message):
    if not value:raise ValueError(message)


def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def blob(raw):return hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()


def git(root,*args,allowed=(0,)):
    result=subprocess.run(['git','-C',str(root),*args],stdin=subprocess.DEVNULL,capture_output=True,timeout=20,
                          env=dict(os.environ,GIT_NO_LAZY_FETCH='1',GIT_TERMINAL_PROMPT='0',GIT_ALLOW_PROTOCOL='file'))
    require(result.returncode in allowed,'Candidate local git operation failed')
    return result.stdout.decode('utf-8').strip()


def central_after(raw,source):
    require(blob(raw)==source['centralBeforeGitBlob'],'Wrong central package preimage')
    before=b'<PackageVersion Include="TUnit" Version="1.73.19" />'
    require(raw.count(before)==1,'Ambiguous exact central declaration')
    result=raw.replace(before,b'<PackageVersion Include="TUnit" Version="1.78.0" />')
    require(blob(result)==source['centralAfterGitBlob'],'Wrong PR971 central postimage')
    return result


def prepare(root,variant,source_sha):
    source= json.loads((PAYLOAD/'SOURCE.json').read_text())
    require(sha(PAYLOAD/'SOURCE.json')==source_sha,'Wrong candidate source manifest')
    require(variant in ('baseline','fixed'),'Unknown candidate variant')
    root=root.resolve(strict=True)
    require(Path(git(root,'rev-parse','--show-toplevel')).resolve()==root and git(root,'rev-parse','HEAD')==BASE,'Candidate is not exact disposable main')
    require(not git(root,'status','--porcelain','--untracked-files=all'),'Candidate checkout must be clean')
    require(not git(root,'ls-files','--others','--ignored','--exclude-standard'),'Ignored preexisting candidate inputs forbidden')
    for relative,pin in source['mainPins'].items():
        file=root/relative
        require(file.is_file() and not file.is_symlink() and sha(file)==pin['sha256'] and blob(file.read_bytes())==pin['gitBlob'],'Wrong exact-main source bytes; LF checkout required')
    target=root/FIXTURE
    require(not target.exists() and not target.is_symlink(),'Candidate regression path already exists')
    collision=git(root,'grep','-l','-E',r'\b(ServerTerminalSignalObservationTests|ServerTerminalSignalObservationPositiveControl|TerminalSignalObservation)\b','--','test/SharpLink.UnitTests',allowed=(0,1))
    require(not collision,'Candidate Unit declaration collision')
    fixture=PAYLOAD/'ServerTerminalSignalObservationTests.cs'
    require(sha(fixture)==source['fixtureSHA256'] and sha(PAYLOAD/'terminal-signal.patch')==source['patchSHA256'],'Wrong candidate fixture/patch bytes')
    if variant=='fixed':
        git(root,'apply','--check',str(PAYLOAD/'terminal-signal.patch'))
        git(root,'apply',str(PAYLOAD/'terminal-signal.patch'))
        require(sha(root/COORDINATOR)==source['coordinatorAfterSHA256'],'Wrong cold-path patch postimage')
    central=root/'Directory.Packages.props';central.write_bytes(central_after(central.read_bytes(),source))
    target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(fixture.read_bytes())
    require(sha(target)==source['fixtureSHA256'],'Wrong real Unit regression bytes')
    expected=['Directory.Packages.props']+([COORDINATOR] if variant=='fixed' else [])
    require(sorted(git(root,'diff','--name-only').splitlines())==sorted(expected),'Unexpected tracked candidate mutation')
    require(git(root,'ls-files','--others','--exclude-standard').splitlines()==[FIXTURE],'Unexpected untracked candidate mutation')
    return {'base':BASE,'variant':variant,'tunit':'1.78.0','fixture':FIXTURE,'fixtureSHA256':source['fixtureSHA256'],
            'regularCasesExpectedNotDiscovered':7,'explicitControlExpectedNotExecuted':1,
            'unitProjectUnchanged':True,'productionPatched':variant=='fixed','remoteWrites':False}


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--checkout',type=Path,required=True);parser.add_argument('--variant',choices=['baseline','fixed'],required=True);parser.add_argument('--source-sha256',required=True)
    args=parser.parse_args();print(json.dumps(prepare(args.checkout,args.variant,args.source_sha256)))
