"""NEW short Integration lane. UID argv/discovery/native are authorities; filter is diagnostic only."""
import hashlib
import json
import math
import re
import xml.etree.ElementTree as ET

METHOD='Api4DynamicStreamExitShouldReleaseItsCollectibleContext'
CLASS='RuntimeAssemblyIntegrationTests'
NS='SharpLink.IntegrationTests'
ASSEMBLY='SharpLink.IntegrationTests'
REL='test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.RegistrationAndReplacement.cs'
MODES=('consumer-break','normal','cancellation-mid-stream')
ALL_MODES=('normal','cancellation-before-first','cancellation-mid-stream','consumer-break','service-exception')
REPORTED='/_/'+REL


def require(ok,reason):
    if not ok:raise ValueError(reason)


def pairs(items):
    result={}
    for k,v in items:
        require(k not in result,'Duplicate JSON key');result[k]=v
    return result


def read_json(path):
    return parse_json(path.read_text(encoding='utf-8-sig'))


def parse_json(text):
    return json.loads(text,object_pairs_hook=pairs,
                      parse_constant=lambda _:(_ for _ in ()).throw(ValueError('Nonfinite JSON')))


def digest(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':'),allow_nan=False).encode()).hexdigest()


def discovery(document,span):
    require(type(document.get('schemaVersion')) is int and document['schemaVersion']==1,'Discovery schema')
    rows=document.get('tests');require(type(rows) is list and len(rows)==5,'Expected exact five source arguments')
    result={};uids=set()
    for r in rows:
        typ=r.get('type',{});loc=r.get('location',{});uid=r.get('uid')
        require(type(uid) is str and 0<len(uid)<=8192 and not uid.startswith('-') and not any(c in uid for c in '\0\r\n') and uid not in uids,'Missing/duplicate actual UID');uids.add(uid)
        require(typ.get('namespace')==NS and typ.get('typeName')==CLASS and typ.get('methodName')==METHOD and
                typ.get('parameterTypeFullNames')==['System.String'] and typ.get('returnTypeFullName')=='System.Threading.Tasks.Task' and
                type(typ.get('methodArity')) is int and typ['methodArity']==0 and
                typ.get('assemblyFullName')==ASSEMBLY+', Version=2.0.4.0, Culture=neutral, PublicKeyToken=null','Discovery FQN/signature')
        require(loc.get('file')==REPORTED and type(loc.get('lineStart')) is int and type(loc.get('lineEnd')) is int and
                (loc['lineStart'],loc['lineEnd'])==tuple(span),'Discovery source span')
        candidates=[m for m in ALL_MODES if r.get('displayName')==METHOD+'('+m+')']
        require(len(candidates)==1 and candidates[0] not in result,'Discovery parameter identity')
        result[candidates[0]]=r
    require(set(result)==set(ALL_MODES),'Discovery five-mode bijection')
    return {m:result[m] for m in MODES}


def exact_summary(summary):
    expected=dict(total=1,passed=1,failed=0,skipped=0,cancelled=0,timedOut=0,flaky=0)
    require(type(summary) is dict and set(summary)==set(expected) and all(type(v) is int for v in summary.values()) and summary==expected,'Not singleton PASS')


def passed(native,junit,row,seen,runtime):
    require(type(native.get('schemaVersion')) is int and native['schemaVersion']==1,'Native schema')
    require(native.get('assemblyName')==ASSEMBLY and native.get('tunitVersion')=='1.73.19.0','Native assembly/version')
    actual=native.get('runtimeVersion');require(type(actual) is str and re.fullmatch(r'\.NET 10\.0\.\d+',actual) and
        (runtime is None or actual==runtime),'Native runtime identity')
    generation=native.get('publicationGeneration');require(type(generation) is str and re.fullmatch('[0-9a-f]{32}',generation) and generation not in seen,'Native generation')
    require('unobservedTaskExceptions' not in native,'Unexpected session errors')
    exact_summary(native.get('summary'));groups=native.get('groups')
    require(type(groups) is list and len(groups)==1,'Native groups');g=groups[0];exact_summary(g.get('summary'))
    require(g.get('className')==CLASS and g.get('namespace')==NS,'Native class')
    tests=g.get('tests');require(type(tests) is list and len(tests)==1,'Native singleton');t=tests[0]
    require(t.get('id')==row['uid'] and t.get('displayName')==row['displayName'] and t.get('methodName')==METHOD and t.get('className')==CLASS,'Native actual UID identity')
    require(t.get('filePath')==REPORTED and type(t.get('lineNumber')) is int and type(t.get('endLineNumber')) is int and
            (t['lineNumber'],t['endLineNumber'])==(row['location']['lineStart'],row['location']['lineEnd']),'Native source span')
    require(t.get('status')=='passed' and not {'exception','skipReason','attempts','retryAttempt'}.intersection(t),'Native non-PASS or retry')
    require(type(t.get('durationMs')) in (int,float) and math.isfinite(t['durationMs']) and t['durationMs']>=0,'Native duration')
    require(len(junit)<=16*1024*1024 and '<!DOCTYPE' not in junit and '<!ENTITY' not in junit,'Unsafe JUnit')
    root=ET.fromstring(junit);require(root.tag=='testsuites' and len(root)==1 and root[0].tag=='testsuite','JUnit suites')
    suite=root[0];require(all(x.tag in ('properties','testcase') for x in suite) and len(suite.findall('properties'))<=1,'JUnit metadata')
    cases=suite.findall('testcase');require(len(cases)==1,'JUnit singleton');case=cases[0]
    require(case.get('name')==row['displayName'] and case.get('classname')==CLASS and len(case)==0,'JUnit identity/result')
    for e in (root,suite):
        require(all(e.get(k)==v for k,v in dict(name=ASSEMBLY,tests='1',errors='0',failures='0',skipped='0').items()),'JUnit counts')
    return {'runtime':actual,'generation':generation,'uidSHA256':digest(row['uid']),
            'filterDiagnostic':{'present':'filter' in native,'sha256':digest(native['filter']) if 'filter' in native else None}}


STAGES=frozenset((10,11,12,20,21,30,31,50,51,60,61,70,71,72,73,79,90,91,92,93,94,95,100,101,102,103,104))


def stage_records(raw,complete=False,mode=None):
    require(len(raw)<=16384,'Oversized stage log')
    records=[];last=-1
    for line in raw.splitlines():
        match=re.fullmatch(rb'([0-9]{1,3}) ([0-9]{1,20})',line);require(match is not None,'Malformed stage record')
        stage,ticks=map(int,match.groups());require(stage in STAGES and ticks>=last,'Unknown stage or time regression');last=ticks
        records.append((stage,ticks))
    if complete:
        require(raw.endswith(b'\n'),'Truncated complete stage record')
        base=[10,11,12]+([20,21] if mode=='normal' else [30,31,50])+[51,60,61,70,71,72,73,79,90,91,92,93,94,95,100]
        ids=[s for s,_ in records];require(ids[:len(base)]==base,'Stage ownership prefix')
        tail=ids[len(base):];require(len(tail)>=2 and tail[-2:]==[103,104] and len(tail[:-2])%2==0 and len(tail[:-2])<=40 and
            all(tail[i:i+2]==[101,102] for i in range(0,len(tail)-2,2)),'Stage GC/terminal sequence')
    return records


def report_inventory(entries):
    # This reporter version emits the fixed HTML auxiliary and two authoritative
    # reports. No extra native/JUnit publication can hide beside the selected pair.
    require({name for name,value in entries.items() if value[0]=='file'}==
        {'session.html','session.tunit-report.json','junit.xml'} and
        {name for name,value in entries.items() if value[0]=='dir'}=={'.'},'Unexpected report publication set')
