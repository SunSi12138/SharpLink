"""Strict full ordinary result reconciliation. Raw reports/UIDs remain local."""
from collections import Counter
import importlib.util
import math
from pathlib import Path
import re
import xml.etree.ElementTree as ET

_spec=importlib.util.spec_from_file_location('execution_census',Path(__file__).with_name('unit-census-results.py'))
census=importlib.util.module_from_spec(_spec);_spec.loader.exec_module(census)
require=census.require
CATALOG_SHA256='36749a0d854f5445c500523bba58a36827c83bd97ab65fe4d1a9dbdb567a9e20'
CODES=frozenset(('UE_CATALOG','UE_LEDGER','UE_ARGV','UE_REPORT_DIRECTORY','UE_REPORT_MEMBERS','UE_REPORT_LIMIT','UE_NATIVE','UE_UID','UE_IDENTITY','UE_STATUS','UE_SUMMARY','UE_JUNIT','UE_RESULTS','UE_PREPARATION','UE_BUDGET','UE_COMMANDS','UE_OWNER','UE_REPORT_SETTINGS','UE_SDK_METADATA'))
SCENARIOS=frozenset(('context-before','context-after','late-backlog','raw-retained','raw-rearmed','fault-rearmed','scheduler-before','scheduler-after','post-before','post-after','bounded-reentrancy','caller-scheduled-long-chain'))
PARENT_REPORTS=frozenset(('session.html','session.tunit-report.json','junit.xml'))
STATUS=frozenset(('passed','failed','error','skipped','cancelled','timedOut'))
SUMMARY_KEYS=('total','passed','failed','skipped','cancelled','timedOut','flaky')


def expected_ordinary(first,second):
    require(type(first) is dict and type(second) is dict and first.get('identity')==second.get('identity') and first.get('actual')==second.get('actual'),'UE_LEDGER')
    rows=first.get('actual');require(type(rows) is list and len(rows)==2884,'UE_LEDGER')
    all_uids=set();ordinary={};explicit=set()
    for item in rows:
        require(type(item) is dict and type(item.get('native')) is dict,'UE_LEDGER')
        row=item['native'];uid=row.get('uid')
        require(type(uid) is str and uid and uid not in all_uids,'UE_LEDGER');all_uids.add(uid)
        mode=item.get('initialEligibility');require(mode in ('ordinary','explicit'),'UE_LEDGER')
        if mode=='ordinary':ordinary[uid]=row
        else:explicit.add(uid)
    require(len(ordinary)==2879 and len(explicit)==5,'UE_LEDGER')
    return ordinary,explicit


def body_argv(dotnet,dll,report):
    return [str(x) for x in (dotnet,dll,'--maximum-parallel-tests','8','--timeout','1500s',
        '--results-directory',report,'--report-html','--report-html-filename',report/'session.html','--junit-output-path',report/'junit.xml')]


def verify_body_inputs(argv,dotnet,dll,report,environment):
    require(argv==body_argv(dotnet,dll,report) and all(type(x) is str and not x.startswith('@') for x in argv),'UE_ARGV')
    census.discovery_inputs([str(dotnet),str(dll),'--list-tests','json'],environment)
    require(all(environment.get(k)==v for k,v in {'TUNIT_ENABLE_JUNIT_REPORTER':'1','TUNIT_DISABLE_ARTIFACT_UPLOAD':'1','TUNIT_DISABLE_GITHUB_REPORTER':'1','TUNIT_AGGREGATE_REPORTS':'off'}.items()),'UE_REPORT_SETTINGS')
    require(not any(key.upper() in ('TUNIT_DISABLE_HTML_REPORTER','TUNIT_DISABLE_JSON_REPORT','TUNIT_DISABLE_JUNIT_REPORTER') for key in environment),'UE_REPORT_SETTINGS')


def owner_closed(state,label,argv):
    if type(state) is not dict or state.get('ownerProvenance') not in ('validated-original-success','original-owner-failure'):return False
    receipt=state.get('receipt')
    if type(receipt) is not dict:return False
    return (receipt.get('stage')==label and receipt.get('argv')==argv and
        type(receipt.get('exitCode')) is int and receipt.get('timedOut') is False and
        type(receipt.get('timeoutSeconds')) in (int,float) and receipt['timeoutSeconds']==state.get('seconds') and
        receipt['timeoutSeconds']>0 and math.isfinite(receipt['timeoutSeconds']) and
        type(receipt.get('remainingOwned')) is dict and receipt['remainingOwned']=={} and
        all(type(receipt.get(k)) is list and receipt[k]==[] for k in ('errors','cleanupErrors','reportingErrors')))


def report_snapshot(report,identity):
    census.legacy.verify_directory(report,identity)
    before=census.enumeration(report)
    require(len(before)<=1024 and all(len(Path(name).parts)<=4 for name in before),'UE_REPORT_LIMIT')
    sizes=[info[4] for info in before.values() if not census.stat.S_ISDIR(info[2])]
    require(all(size<=16*1024*1024 for size in sizes) and sum(sizes)<=128*1024*1024,'UE_REPORT_LIMIT')
    state=census.snapshot(report)
    require(census.enumeration(report)==before,'UC_SCAN_CHANGED')
    require(len(state)<=1024,'UE_REPORT_LIMIT')
    total=0;scenario_dirs=set()
    for name,item in state.items():
        parts=Path(name).parts
        require(len(parts)<=4,'UE_REPORT_LIMIT')
        if name=='.':continue
        if len(parts)==1:
            require(name in PARENT_REPORTS and item[0]=='file' or name=='dispatch-probes' and item[0]=='directory','UE_REPORT_MEMBERS')
        else:
            require(parts[0]=='dispatch-probes','UE_REPORT_MEMBERS')
            match=re.fullmatch(r'(.+)-([0-9a-f]{32})',parts[1])
            require(match is not None and match[1] in SCENARIOS,'UE_REPORT_MEMBERS')
            scenario_dirs.add(parts[1]);require(len(scenario_dirs)<=12,'UE_REPORT_MEMBERS')
            if len(parts)==2:require(item[0]=='directory','UE_REPORT_MEMBERS')
        if item[0]=='file':
            total+=item[4]
            if name in ('session.tunit-report.json','junit.xml'):require(item[4]<=16*1024*1024,'UE_REPORT_LIMIT')
    require(total<=128*1024*1024,'UE_REPORT_LIMIT')
    return state


def xml_name(value):
    # Exact pinned JUnitXmlWriter.cs sanitizer iterates UTF-16 chars, including
    # each surrogate separately. It is not a UID mapping or fuzzy comparison.
    raw=value.encode('utf-16-le',errors='surrogatepass');output=[]
    for i in range(0,len(raw),2):
        code=int.from_bytes(raw[i:i+2],'little')
        output.append(chr(code) if code in (9,10,13) or 0x20<=code<=0xD7FF or 0xE000<=code<=0xFFFD else '[0x'+format(code,'X')+']')
    return ''.join(output)


def summary(tests):
    result=dict.fromkeys(SUMMARY_KEYS,0)
    for test in tests:
        status=test['status'];result['total']+=1
        result['failed' if status=='error' else status]+=1
        attempt=test.get('retryAttempt',0)
        require(type(attempt) is int and attempt>=0,'UE_STATUS')
        if status=='passed' and attempt>0:result['flaky']+=1
    return result


def exact_summary(actual,expected):
    require(type(actual) is dict and set(actual)==set(SUMMARY_KEYS) and
            all(type(v) is int and v>=0 for v in actual.values()) and actual==expected,'UE_SUMMARY')


def classify(native,junit,ordinary,explicit,facts,installed_runtimes):
    require(type(native) is dict and type(native.get('schemaVersion')) is int and native['schemaVersion']==1,'UE_NATIVE')
    require(native.get('assemblyName')=='SharpLink.UnitTests' and native.get('tunitVersion')=='1.73.19.0','UE_IDENTITY')
    runtime=native.get('runtimeVersion');generation=native.get('publicationGeneration')
    require(type(runtime) is str and re.fullmatch(r'\.NET 10\.0\.\d+',runtime) and
            type(generation) is str and re.fullmatch('[0-9a-f]{32}',generation),'UE_IDENTITY')
    require(runtime.removeprefix('.NET ') in {entry['version'] for entry in installed_runtimes if entry['framework']=='Microsoft.NETCore.App'},'UE_IDENTITY')
    groups=native.get('groups');require(type(groups) is list and groups,'UE_NATIVE')
    seen=set();group_keys=set();all_tests=[];expected_junit=Counter();public_rows=[];retries=False
    for group in groups:
        require(type(group) is dict and type(group.get('namespace')) is str and type(group.get('className')) is str,'UE_NATIVE')
        group_key=(group['namespace'],group['className']);require(group_key not in group_keys,'UE_IDENTITY');group_keys.add(group_key)
        tests=group.get('tests');require(type(tests) is list and tests,'UE_NATIVE')
        for test in tests:
            require(type(test) is dict,'UE_NATIVE');uid=test.get('id')
            require(type(uid) is str and uid not in seen and uid in ordinary and uid not in explicit,'UE_UID');seen.add(uid)
            row=ordinary[uid];meta=row['type'];loc=row['location'];status=test.get('status')
            require(status in STATUS,'UE_STATUS')
            require(test.get('displayName')==row['displayName'] and test.get('methodName')==meta['methodName'] and
                    test.get('className')==group['className']==meta['typeName'] and group['namespace']==meta['namespace'] and
                    test.get('filePath')==loc['file'] and type(test.get('lineNumber')) is int and test['lineNumber']==loc['lineStart'] and
                    type(test.get('endLineNumber')) is int and test['endLineNumber']==loc['lineEnd'],'UE_IDENTITY')
            duration=test.get('durationMs');require(type(duration) in (int,float) and math.isfinite(duration) and duration>=0,'UE_NATIVE')
            retry_fields=bool({'retryAttempt','attempts'}.intersection(test));retries|=retry_fields
            if status=='passed':require(not {'exception','skipReason'}.intersection(test),'UE_STATUS')
            kind={'passed':'passed','failed':'failure','error':'error','timedOut':'error','cancelled':'error','skipped':'skipped'}[status]
            expected_junit[(xml_name(row['displayName']),xml_name(meta['typeName']),kind)]+=1
            public_rows.append({'source':loc['file'][3:],'namespace':meta['namespace'],'className':meta['typeName'],'methodName':meta['methodName'],'status':status})
            all_tests.append(test)
        exact_summary(group.get('summary'),summary(tests))
    totals=summary(all_tests);facts.update(observed=len(seen),expected=len(ordinary),statusCounts=totals,
        missingCount=len(set(ordinary)-seen),runtime=runtime,nativeReportSHA256=census.digest(native))
    require(seen==set(ordinary),'UE_UID');exact_summary(native.get('summary'),totals)
    require(type(junit) is str and len(junit.encode())<=16*1024*1024 and '<!DOCTYPE' not in junit and '<!ENTITY' not in junit,'UE_JUNIT')
    root=ET.fromstring(junit);require(root.tag=='testsuites' and len(root)==1 and root[0].tag=='testsuite','UE_JUNIT');suite=root[0]
    require(all(x.tag in ('properties','testcase') for x in suite) and len(suite.findall('properties'))<=1,'UE_JUNIT')
    actual_junit=Counter()
    for case in suite.findall('testcase'):
        require(all(x.tag in ('failure','error','skipped') for x in case) and len(case)<=1,'UE_JUNIT')
        kind=case[0].tag if len(case) else 'passed';actual_junit[(case.get('name'),case.get('classname'),kind)]+=1
    require(actual_junit==expected_junit,'UE_JUNIT')
    junit_totals={'name':'SharpLink.UnitTests','tests':str(len(seen)),
        'failures':str(sum(v for k,v in actual_junit.items() if k[2]=='failure')),
        'errors':str(sum(v for k,v in actual_junit.items() if k[2]=='error')),
        'skipped':str(sum(v for k,v in actual_junit.items() if k[2]=='skipped'))}
    require(all(all(element.get(k)==v for k,v in junit_totals.items()) for element in (root,suite)),'UE_JUNIT')
    counts=Counter(tuple(row[k] for k in ('source','namespace','className','methodName','status')) for row in public_rows)
    facts.update(methodStatusCounts=[dict(zip(('source','namespace','className','methodName','status'),key),count=count) for key,count in sorted(counts.items())],
        resultUIDSetSHA256=census.digest(sorted(seen)),resultRowsSHA256=census.digest(sorted(public_rows,key=lambda x:tuple(x.values()))),
        retriesObserved=retries,executionClosureObserved=True)
    qualified=not retries and totals==dict(total=len(ordinary),passed=len(ordinary),failed=0,skipped=0,cancelled=0,timedOut=0,flaky=0)
    facts['allOrdinaryPassed']=qualified
    return qualified
