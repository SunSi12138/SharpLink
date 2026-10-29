#!/usr/bin/env python3
"""Only finite allowlisted diagnostic aggregates; no raw trace, stack, payload or report dump."""
import argparse
import math
from pathlib import Path
from common import CONTRACT, SOURCE, strict_json

CATEGORIES = ('sharplink-stream','sharplink-dispatch','sharplink-transport','sharplink-other',
              'serialization','task-scheduling','runtime-wait','runtime-other','other-managed','unknown')
FAILURES = {'invalid-pid','lost-or-truncated','marker-cardinality','marker-identity','window-boundary',
    'malformed-or-reversed-event','malformed-event','malformed-category','sample-error',
    'malformed-allocation','malformed-generation','unmatched-events','missing-samples','parse-failure',
    'unknown-coverage','marker-measurement'}
NORMALIZED_KINDS = ('work-start','work-stop','managed-sample','external-sample','sample-error',
    'allocation','gc-start','gc-stop','pause-start','pause-stop','contention-start','contention-stop',
    'wait-start','wait-stop','malformed','other')
HOST_REASONS = {'EARLY_EXIT','READY_TIMEOUT','READY_SCHEMA','READY_IDENTITY','TARGET_PROCESS_GROUP',
    'TARGET_AFFINITY','PROCESS_TIMEOUT','TRACE_SIZE','FINAL_TRACE_SIZE','PROCESS_EXIT','DESCENDANT_LEAK'}
PHASES = {'SETUP','RUNTIME','TOOLS','HOST','CONTROL','TRACE','ANALYSIS','MARKER'}

def number(v, integer=False):
    if type(v) not in ((int,) if integer else (int,float)) or not math.isfinite(v) or v<0 or v>9223372036854775807:
        raise ValueError('SUMMARY_NUMBER')
    return v

def render(analysis):
    if type(analysis) is not dict or type(analysis.get('schema')) is not int or analysis.get('schema')!=1 or analysis.get('status') not in ('Complete','Incomplete'):
        raise ValueError('SUMMARY_SCHEMA')
    failures=analysis.get('failures')
    if not isinstance(failures,list) or any(f not in FAILURES for f in failures): raise ValueError('SUMMARY_REASON')
    if analysis['status']=='Complete' and failures: raise ValueError('SUMMARY_COMPLETENESS')
    status_line='DIAGNOSTIC_ANALYSIS status='+analysis['status']+' reasons='+(','.join(sorted(set(failures))) or 'none')
    if 'parse-failure' in failures:
        return [status_line]  # Decoder failure has no observations, not measured zero cost.
    from marker_gate import CENSUS, EXPECTED_CENSUS
    census=analysis['markerCensus']; counts=analysis['normalizedEventCounts']
    if type(census) is not dict or set(census)!=set(CENSUS) or type(counts) is not dict or set(counts)!=set(NORMALIZED_KINDS):
        raise ValueError('SUMMARY_CENSUS')
    if analysis['status']=='Complete' and (census!=EXPECTED_CENSUS or counts['work-start']!=1 or counts['work-stop']!=1):
        raise ValueError('SUMMARY_COMPLETENESS')
    observations=['MARKER_CENSUS provider=matched-expected ID1=start ID2=stop '+
        ' '.join(k+'='+str(number(census[k],True)) for k in CENSUS),
        'NORMALIZED_TRACE_EVENTS '+' '.join(k+'='+str(number(counts[k],True)) for k in NORMALIZED_KINDS)]
    invalid_window={'invalid-pid','marker-cardinality','marker-identity','marker-measurement',
                    'window-boundary','malformed-or-reversed-event'}
    if invalid_window.intersection(failures):
        return [status_line,*observations,'DIAGNOSTIC_WINDOW unavailable; no window aggregates accepted',
            'TRACE_DIAGNOSTIC lostEvents='+str(number(analysis['lostEvents'],True))+
            ' unmatchedEvents='+str(number(analysis['unmatchedEvents'],True))]
    if analysis.get('sourceRevision')!=SOURCE or analysis.get('scenario')!='baseline-static-n100-work20s':
        raise ValueError('SUMMARY_IDENTITY')
    if analysis.get('coverage') not in ('Partial','Classified'): raise ValueError('SUMMARY_COVERAGE')
    if analysis['status']=='Complete' and (failures or any(analysis.get(k)!=0 for k in
        ('lostEvents','unknownStacks','unknownEvents','unmatchedEvents','sampleErrors'))):
        raise ValueError('SUMMARY_COMPLETENESS')
    counts=('lostEvents','unknownStacks','unmatchedEvents','outsideWindowEvents','otherProcessEvents',
            'unknownEvents','externalThreadSamples','sampleErrors','allocationTickSamples','allocationTickReportedBytes')
    lines=['DIAGNOSTIC_ANALYSIS status='+analysis['status']+' coverage='+analysis['coverage']+
        ' reasons='+(','.join(sorted(set(failures))) or 'none')]
    lines += observations
    lines += ['DIAGNOSTIC_COUNTS '+' '.join(k+'='+str(number(analysis[k],True)) for k in counts)]
    values=analysis['managedThreadTimeSamples']
    if set(values)!=set(CATEGORIES): raise ValueError('SUMMARY_CATEGORIES')
    lines += ['MANAGED_THREAD_TIME_SAMPLES '+' '.join(k+'='+str(number(values[k],True)) for k in CATEGORIES)]
    for label,key in (('GC_PAUSE','gcPauses'),('MONITOR_CONTENTION','contention'),('WAITHANDLE_WAIT','waitHandle')):
        v=analysis[key]
        if set(v)!={'count','durationMilliseconds'}: raise ValueError('SUMMARY_INTERVAL')
        lines.append(f'{label} completeCount={number(v["count"],True)} durationMs={number(v["durationMilliseconds"]):.6f}')
    v=analysis['censoredIntervals']
    if set(v)!={'gc','pause','contention','wait'}: raise ValueError('SUMMARY_CENSOR')
    lines.append('BOUNDARY_CENSORED '+' '.join(k+'='+str(number(v[k],True)) for k in ('gc','pause','contention','wait')))
    v=analysis['gcStartsByGeneration']
    if set(v)!={'gen0','gen1','gen2'}: raise ValueError('SUMMARY_GC')
    lines.append('GC_STARTS '+' '.join(k+'='+str(number(v[k],True)) for k in ('gen0','gen1','gen2')))
    return lines

def main():
    p=argparse.ArgumentParser();p.add_argument('--input',type=Path,required=True);a=p.parse_args()
    incomplete=False
    print('BASELINE_DIAGNOSTIC DiagnosticOnly source='+SOURCE+' runtime=10.0.14 mode=static-intercepted n=100')
    from marker_gate import render_marker
    for slot in ('marker','control','trace'):
        try:
            host=strict_json((a.input/slot/'host.json').read_text())
            if host.get('status')=='failed':
                reason=host.get('detail')
                print('PROCESS_FAILURE slot='+slot+' reason='+(reason if type(reason) is str and reason in HOST_REASONS else 'HOST_OR_OS_FAILURE'))
        except (OSError,ValueError,TypeError,KeyError): pass
    try:
        for line in render_marker(strict_json((a.input/'marker/analysis.json').read_text())): print(line)
    except (OSError,ValueError,TypeError,KeyError):
        print('MARKER_ROUNDTRIP status=unavailable-or-invalid');incomplete=True
    for variant in CONTRACT['variants']:
        try:
            host=strict_json((a.input/variant/'host.json').read_text())
            v=strict_json((a.input/variant/'result.json').read_text())
            valid=strict_json((a.input/variant/'validated.json').read_text())
            if valid.get('status')!='VALIDATED' or valid.get('pid')!=host['targetPid']: raise ValueError('VALIDATION')
            if host['status']!='success' or host['processGroupQuiescent'] is not True: raise ValueError('HOST')
            ops=number(v['operations'],True);cpu=number(v['cpuTicks'],True)/10_000_000
            allocation=number(v['allocatedBytes'],True)
            if ops==0: raise ValueError('EMPTY')
            print(f'PROCESS variant={variant} normalCompletion=PASS operations={ops} items={ops*100} cpuSeconds={cpu:.6f} managedAllocatedBytes={allocation}')
        except (OSError,ValueError,TypeError,KeyError):
            print('PROCESS variant='+variant+' status=unavailable-or-failed');incomplete=True
    try:
        analysis=strict_json((a.input/'analysis.json').read_text())
        for line in render(analysis): print(line)
        incomplete |= analysis['status']!='Complete'
    except (OSError,ValueError,TypeError,KeyError):
        print('DIAGNOSTIC_ANALYSIS status=unavailable-or-invalid');incomplete=True
    try:
        done=strict_json((a.input/'complete.json').read_text())
        if done.get('status')!='DiagnosticOnly' or done.get('variants')!=CONTRACT['variants']: incomplete=True
    except (OSError,ValueError,TypeError,KeyError): incomplete=True
    if (a.input/'failure.json').exists():
        incomplete=True
        try:
            phase=strict_json((a.input/'failure.json').read_text()).get('phase')
            print('DIAGNOSTIC_FAILURE reason='+(phase if phase in PHASES else 'UNKNOWN'))
        except (OSError,ValueError,TypeError,KeyError): print('DIAGNOSTIC_FAILURE reason=UNKNOWN')
    print('LIMITATION sampled-thread-time is not CPU attribution; traced CPU includes instrumentation; waits overlap; no performance target or collector-overhead estimate.')
    print('DIAGNOSTIC_RESULT '+('INCOMPLETE' if incomplete else 'COMPLETE'))
    return int(incomplete)

if __name__=='__main__': raise SystemExit(main())
