#!/usr/bin/env python3
"""Bounded diagnostic only: no clean run can establish release eligibility."""
import json, os, pathlib, re, signal, subprocess, sys, time
source, evidence, collector, variant = map(pathlib.Path, sys.argv[1:5])
evidence.mkdir(parents=True, exist_ok=True)
dll = source / 'test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll'
results = []

def save(status):
    (evidence/'diagnostic-summary.json').write_text(json.dumps({
        'SourceSha': os.environ['SOURCE_SHA'], 'ToolingSha': os.environ['GITHUB_SHA'],
        'Variant': str(variant), 'Status': status, 'ReleaseEligible': False,
        'ProcessDeadlineSeconds':150, 'CollectorDeadlineSeconds':60,
        'AnalysisDeadlineSeconds':60, 'MaximumRounds':10, 'StartBudgetSeconds':600,
        'Processes': results}, indent=2))

def kill_owned(process):
    # Every child starts a dedicated session/group. Also retire its descendants
    # if the group leader exited first; tolerate concurrent natural process exit.
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    if process.poll() is None:
        process.wait(timeout=10)

def bounded(command, path, seconds, env=None):
    with path.open('wb') as out:
        process = subprocess.Popen(command, cwd=source, stdout=out, stderr=subprocess.STDOUT,
                                   env=env, start_new_session=True)
        try:
            return process.wait(timeout=seconds)
        except subprocess.TimeoutExpired:
            kill_owned(process)
            return 124
        finally:
            kill_owned(process)

if '--self-test' in sys.argv:
    assert bounded([sys.executable,'-c','import time; time.sleep(30)'],evidence/'timeout.log',0.1)==124
    assert bounded([sys.executable,'-c','raise SystemExit(0)'],evidence/'success.log',10)==0
    assert bounded([sys.executable,'-c','raise SystemExit(7)'],evidence/'failure.log',10)==7
    child_log=evidence/'child.log'
    code="import subprocess,sys; p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(30)']); print(p.pid,flush=True)"
    assert bounded([sys.executable,'-c',code],child_log,10)==0
    child_pid=int(child_log.read_text().strip())
    state=pathlib.Path(f'/proc/{child_pid}/stat')
    deadline=time.monotonic()+2
    while state.exists() and state.read_text().split()[2]!='Z' and time.monotonic()<deadline:
        time.sleep(0.01)
    assert not state.exists() or state.read_text().split()[2]=='Z', 'Owned descendant survived cleanup'
    print('Owned-process timeout, success, failure and descendant cleanup self-tests passed.',flush=True)
    sys.exit(0)

# Listing is also bounded, unlike the original diagnostic gap.
discovery = evidence/'tests.json'
if bounded(['dotnet',str(dll),'--list-tests','json'], discovery,150) != 0:
    save('discovery-failed-or-timed-out'); sys.exit(1)
all_tests = json.loads(discovery.read_text())['tests']
def find(method):
    matches = [t for t in all_tests if t['type']['methodName']==method]
    if len(matches) != 1: raise RuntimeError(f'{method}: expected one UID, got {len(matches)}')
    return matches[0]
streams = [t for t in all_tests if t['type']['methodName']=='Api4DynamicStreamExitShouldReleaseItsCollectibleContext']
assert {t['displayName'] for t in streams} == {f'Api4DynamicStreamExitShouldReleaseItsCollectibleContext({m})' for m in ['normal','cancellation-before-first','cancellation-mid-stream','consumer-break','service-exception']}
ordered = [find('OneHundredClientReplacementsShouldLeaveOneReusableRegistration'),
           find('MultiClusterReplacementShouldPublishCoordinatorRoutesBeforeOldDrainAndAfterCallerCancellation'),
           *streams,find('RejectedApi4DynamicRegistrationShouldReleaseItsCollectibleContext'),
           find('CollectibleContextShouldUnloadAfterFrameworkReferencesAreReleased')]
started=time.monotonic()
for round_number in range(1,11):
    if time.monotonic()-started >= 600: break
    for test in ordered:
        if time.monotonic()-started >= 600:
            save("start-budget-exhausted-inconclusive")
            print("No reproduction before process-start budget; partial round is not release evidence.",flush=True)
            sys.exit(0)
        ordinal=len(results)+1
        label=f'{ordinal:03d}-round-{round_number}'
        env=dict(os.environ)
        env['SHARPLINK_ABI_STAGE_FILE']=str(evidence/label)
        for suffix in ['.test.log','.service.log']:
            (evidence/(label+suffix)).write_text('')  # Preflight marker I/O outside the test.
        if str(variant)=='marked': env['SHARPLINK_ABI_DIAGNOSTIC']='1'
        else: env.pop('SHARPLINK_ABI_DIAGNOSTIC',None)
        result={'Round':round_number,'Ordinal':ordinal,'Uid':test['uid'], 'Name':test['displayName']}
        results.append(result); save('running')
        command=['dotnet',str(dll),'--maximum-parallel-tests','1','--timeout','120s','--filter-uid',test['uid']]
        print(f"START {label} {test['displayName']}",flush=True)
        begin=time.monotonic()
        with (evidence/f'{label}.stdout.log').open('wb') as out:
            process=subprocess.Popen(command,cwd=source,env=env,stdout=out,stderr=subprocess.STDOUT,start_new_session=True)
            result['Pid']=process.pid
            try:
                result['ExitCode']=process.wait(timeout=150)
            except subprocess.TimeoutExpired:
                result['Hung']=True; save('hang-detected')
                dump=evidence/f'{label}.dmp'
                result['DumpExitCode']=bounded([str(collector),'collect','--process-id',str(process.pid),'--type','Heap','--output',str(dump)],evidence/f'{label}.collect.log',60)
                kill_owned(process)
                if dump.exists():
                    result['AnalysisExitCode']=bounded([str(collector),'analyze',str(dump),'--command','threads','--command','clrstack -all','--command','syncblk','--command','dumpasync','--command','exit'],evidence/f'{label}.stacks.log',60)
            finally:
                kill_owned(process)
                result['ElapsedSeconds']=time.monotonic()-begin
                save('running')
        if not result.get('Hung') and result.get('ExitCode') == 0:
            console=re.sub(r'\x1b\[[0-9;]*m','',(evidence/f'{label}.stdout.log').read_text(errors='replace'))
            counts={key:[int(v) for v in re.findall(r'(?m)^\s*'+key+r':\s*(\d+)\s*$',console)] for key in ['total','failed','succeeded','skipped']}
            result['ConsoleCounts']=counts
            if str(variant)=='marked' and test['type']['methodName']=='Api4DynamicStreamExitShouldReleaseItsCollectibleContext':
                stages=(evidence/f'{label}.test.log').read_text()
                if ': helper-before' not in stages or ': unload-assert-before' not in stages:
                    result['EvidenceError']='Lifecycle diagnostic markers missing despite a completed test'
            if counts != {'total':[1],'failed':[0],'succeeded':[1],'skipped':[0]}:
                result['EvidenceError']='Expected exactly one passed, zero skipped test in this owned process'
        if result.get('Hung') or result.get('ExitCode') != 0 or result.get('EvidenceError'):
            save('hang-reproduced' if result.get('Hung') else 'test-failed')
            print(json.dumps(result),flush=True); sys.exit(1)
        print(f"PASS {label} {result['ElapsedSeconds']:.3f}s",flush=True)
save('no-reproduction-inconclusive')
print('No reproduction within bounded diagnostic; not a release pass.',flush=True)
