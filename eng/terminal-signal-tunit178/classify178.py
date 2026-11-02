"""NEW1.78 strict session-node oracle. Never relabel/project it through1.73.
Exact engine source: d43977cafa903cf2ace3010af7878a7ec54ce857.
"""
import hashlib
import math
import re
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
import classify as old
import source178

require=old.require


def normalized(value):
    require(isinstance(value,str),'Missing exception text')
    return value.replace('\r\n','\n')


def exception_text(failure):
    return failure['type']+': '+failure['message']+ ('\n'+failure['stackTrace'] if 'stackTrace' in failure else '')


def exact_error(case, failure):
    require(len(case)==1 and case[0].tag=='error','Expected exactly one JUnit error')
    error=case[0]
    require(not list(error) and error.attrib=={'type':failure['type'],'message':failure['message']},'Wrong178 JUnit exception fields')
    require(normalized(error.text or '')==normalized(exception_text(failure)),'Wrong178 full native/JUnit exception graph')


def counter_error(failure, source, reported, method):
    require(isinstance(failure,dict) and set(failure)=={'type','message','stackTrace'},'Wrong178 counter exception shape')
    require(failure['type']=='System.Exception' and failure['message']==old.COUNTER,'Wrong178 target counter failure')
    trace=failure['stackTrace'];require(isinstance(trace,str),'Missing178 counter stack')
    frames=[line.strip() for line in trace.splitlines() if line.strip().startswith('at ')]
    numbers=old.source_lines(source)
    names=[old.NS+'.TerminalSignalObservation.RequireCount(Int32 expected)',
           old.NS+'.'+old.REGULAR+'.RequireNoUnobservedSignal(Boolean failDuringStart)',
           old.NS+'.'+old.REGULAR+'.'+method+'()']
    lines=[numbers[0],numbers[1],numbers[2 if method==old.START else 3]]
    require(len(frames)>=3 and frames[:3]==[f'at {name} in {reported}:line {line}' for name,line in zip(names,lines)],
            'Wrong178 counter/GC control-flow stack')
    require('INCONCLUSIVE' not in trace and '--->' not in trace,'Wrong178 counter topology')


def event_exception(failure, scope, source, reported, markers):
    require(isinstance(failure,dict),'Missing178 event exception')
    keys={'type','message'} if scope=='positive' else {'type','message','stackTrace'}
    require(set(failure)==keys and failure['type']=='System.IO.IOException','Wrong178 event type/topology/stack presence')
    prefix='terminal-positive-' if scope=='positive' else 'terminal-owned-'
    marker=failure['message']
    require(isinstance(marker,str) and re.fullmatch(prefix+'[0-9a-f]{32}',marker) is not None and marker not in markers,
            'Wrong178 event marker or repeated publication event')
    if scope!='positive':
        trace=failure['stackTrace'];require(isinstance(trace,str) and trace and '--->' not in trace,'Missing178 propagated event stack')
        source_lines=Path(source).read_text().splitlines()
        anchors=[i+1 for i,line in enumerate(source_lines) if 'try { task.GetAwaiter().GetResult(); return new(null, task.Exception); }' in line]
        require(len(anchors)==1,'Ambiguous178 event source anchor')
        anchor=f'at {old.NS}.{old.REGULAR}.CaptureFailure(Task task) in {reported}:line {anchors[0]}'
        frames=[line.strip() for line in trace.splitlines() if line.strip().startswith('at ')]
        require(anchor in frames,'178 event lacks actual pinned propagation origin')
    return marker


def classify(native, junit, ledger, scope, source, runtime, reported, generations=(), markers=()):
    require(scope in ('baseline-start','baseline-stop','positive','fixed'),'Wrong178 scope')
    source178.validate_ledger(ledger,source)
    baseline=scope.startswith('baseline-'); affected=scope!='fixed'
    cls=old.POSITIVE if scope=='positive' else old.REGULAR
    require(len(ledger)==(7 if scope=='fixed' else 1),'Wrong178 ledger size')
    methods=[row['type']['methodName'] for row in ledger.values()]
    expected_methods={old.POS_METHOD:1} if scope=='positive' else ({old.START if scope=='baseline-start' else old.STOP:1} if baseline else old.EXPECTED)
    require({m:methods.count(m) for m in methods}==expected_methods,'Wrong178 selected semantic controls')
    require(type(native.get('schemaVersion')) is int and native['schemaVersion']==1,'Wrong178 native schema')
    require(native.get('assemblyName')=='SharpLink.UnitTests' and native.get('tunitVersion')=='1.78.0.0','Wrong178 actual assembly/package report')
    actual_runtime=native.get('runtimeVersion')
    require(isinstance(actual_runtime,str) and re.fullmatch(r'\.NET 10\.0\.\d+',actual_runtime) is not None and
            actual_runtime==runtime,'Wrong178 paired actual runtime')
    old.check_publication(native.get('publicationGeneration'),generations)
    real_failures=1 if baseline else 0
    total=len(ledger)+int(affected); failed=real_failures+int(affected)
    old.exact_summary(native.get('summary'),old.summary(total,failed))
    groups=native.get('groups')
    require(isinstance(groups,list) and len(groups)==(2 if affected else 1),'Wrong178 exact group count')
    by_class={group.get('className'):group for group in groups if isinstance(group,dict)}
    require(len(by_class)==len(groups) and set(by_class)==({cls,'UnknownClass'} if affected else {cls}),
            'Unknown178 group or duplicate class')
    real=by_class[cls];require(real.get('namespace')==old.NS,'Wrong178 real namespace')
    old.exact_summary(real.get('summary'),old.summary(len(ledger),real_failures))
    tests=real.get('tests');require(isinstance(tests,list) and len(tests)==len(ledger),'Wrong178 real case count')
    require(all(isinstance(t,dict) for t in tests) and {t.get('id') for t in tests}==set(ledger),
            'Wrong178 real UID bijection')
    require(isinstance(junit,str) and len(junit)<=16*1024*1024 and '<!DOCTYPE' not in junit and '<!ENTITY' not in junit,
            'Unsafe178 JUnit document')
    root=ET.fromstring(junit)
    require(root.tag=='testsuites' and len(root)==1 and root[0].tag=='testsuite','Wrong178 JUnit root')
    suite=root[0]
    allowed=('properties','testcase','system-err') if affected else ('properties','testcase')
    require(all(child.tag in allowed for child in suite) and len(suite.findall('properties'))<=1,'Unknown178 suite metadata')
    cases=suite.findall('testcase');require(len(cases)==total,'Wrong178 JUnit case count')
    by_display={case.get('name'):case for case in cases};require(len(by_display)==len(cases),'Duplicate178 JUnit case name')
    for test in tests:
        row=ledger[test['id']];meta=row['type'];case=by_display.get(row['displayName'])
        require(test.get('displayName')==row['displayName'] and test.get('methodName')==meta['methodName'] and
                test.get('className')==meta['typeName']==cls,'Wrong178 real test identity')
        require(test.get('filePath')==str(reported) and test.get('lineNumber')==row['location']['lineStart'],
                'Wrong178 real source identity')
        source178.native_span(test,row)
        require(not {'skipReason','retryAttempt','attempts'}.intersection(test),'Forbidden178 real skip/retry field')
        require(type(test.get('durationMs')) in (int,float) and math.isfinite(test['durationMs']) and test['durationMs']>=0,
                'Invalid178 real duration')
        require(case is not None and case.get('classname')==cls,'Wrong178 real JUnit identity')
        if baseline:
            require(test.get('status')=='error','178 baseline is not target error')
            counter_error(test.get('exception'),source,reported,meta['methodName']);exact_error(case,test['exception'])
        else:
            require(test.get('status')=='passed' and 'exception' not in test and len(case)==0,'178 real control did not pass cleanly')
    marker=None;engine_uid=None
    if affected:
        entries=native.get('unobservedTaskExceptions')
        require(isinstance(entries,list) and len(entries)==1,'Wrong178 session event count')
        failure=entries[0];marker=event_exception(failure,scope,source,reported,markers)
        group=by_class['UnknownClass'];require(group.get('namespace')=='','Wrong178 engine group namespace')
        old.exact_summary(group.get('summary'),old.summary(1,1))
        engine=group.get('tests');require(isinstance(engine,list) and len(engine)==1 and isinstance(engine[0],dict),'Wrong178 engine node count')
        node=engine[0];engine_uid=node.get('id')
        require(isinstance(engine_uid,str) and re.fullmatch(r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}',engine_uid) is not None,
                'Wrong178 synthetic GUID syntax')
        require(str(uuid.UUID(engine_uid))==engine_uid and engine_uid not in ledger,'178 engine UID collides with discovery')
        name='Unobserved task exception - IOException: '+marker
        require(set(node)=={'id','displayName','methodName','className','status','durationMs','exception'} and
                node['displayName']==node['methodName']==name and node['className']=='UnknownClass' and node['status']=='error',
                'Unknown178 synthetic node origin/shape')
        require(type(node['durationMs']) in (int,float) and math.isfinite(node['durationMs']) and node['durationMs']==0,
                'Wrong178 synthetic timing')
        require(node['exception']==failure,'178 synthetic/session complete exception graphs differ')
        case=by_display.get(name)
        require(case is not None and case.get('classname')=='UnknownClass','Wrong178 engine JUnit identity')
        exact_error(case,failure)
        outputs=suite.findall('system-err')
        require(len(outputs)==1 and not outputs[0].attrib and not list(outputs[0]),'Wrong178 suite event output shape')
        require(normalized(outputs[0].text or '')==normalized('Unobserved task exception: '+exception_text(failure)+'\n'),
                'Wrong178 prefixed suite event graph/newline')
    else:
        require('unobservedTaskExceptions' not in native and not suite.findall('system-err'),'Fixed178 has unobserved session data')
    for element in (root,suite):
        require(all(element.get(k)==v for k,v in {'name':'SharpLink.UnitTests','tests':str(total),'failures':'0',
                'errors':str(failed),'skipped':'0'}.items()),'Wrong178 JUnit exact counts')
    return {'scope':scope,'classification':'EXPECTED_178_BASELINE_FAILURE' if baseline else
            ('EXPECTED_STRICT_CALIBRATION_FAILURE' if affected else 'FIXED_178_PASS'),
            'realTests':len(ledger),'engineErrors':int(affected),'eventCount':1 if affected else 0,
            'runtime':actual_runtime,'publicationGeneration':native['publicationGeneration'],
            'engineUID':engine_uid,'marker':marker,
            'collectionEvidence':'counter-GC-control-flow+matched-session-event' if baseline else
            ('unchanged-positive-body-success+matched-session-event-no-stack' if affected else 'unchanged-fixed-seven-success-path')}
