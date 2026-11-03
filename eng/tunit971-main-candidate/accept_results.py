"""NEW original Integration accept oracle. No focused counter/weakref claims."""
import hashlib
import math
from pathlib import Path
import re
import uuid
import xml.etree.ElementTree as ET
from classify import require,read_json,exact_summary,summary,check_publication,check_optional_filter

NS='SharpLink.IntegrationTests'
CLASS='TransportConnectionIntegrationTests'
ASSEMBLY='SharpLink.IntegrationTests'
TARGET='ServerStartShouldSurfaceTransportAcceptException'
CONTROL='NamedPipeServerStartShouldStopNormallyWhenCancellationIsRequested'
FIXTURE='test/SharpLink.IntegrationTests/TransportConnectionIntegrationTests.Lifecycle.cs'
HELPER='test/SharpLink.IntegrationTests/TransportConnectionIntegrationTests.cs'
REPORTED='/_/'+FIXTURE
CATALOG=read_json(Path(__file__).with_name('ACCEPT-SOURCE.json'))


def source_spans(source):
    raw=Path(source).read_bytes()
    require(hashlib.sha256(raw).hexdigest()==CATALOG['sourcePins'][FIXTURE]['sha256'],'Originalaccept fixture bytes changed')
    text=raw.decode('utf-8');result={}
    for method in (TARGET,CONTROL):
        pattern=r'^    \[Test\]\n    public async Task '+re.escape(method)+r'\(\)\n    \{\n[\s\S]*?^    \}'
        matches=list(re.finditer(pattern,text,re.MULTILINE));require(len(matches)==1,'Ambiguous originalaccept source span')
        match=matches[0];result[method]=(text.count('\n',0,match.start())+1,text.count('\n',0,match.end())+1)
    return result


def discovery(document,method,source):
    require(method in (TARGET,CONTROL),'Unselected originalaccept method')
    require(type(document.get('schemaVersion')) is int and document['schemaVersion']==1,'Wrong originalaccept discovery schema')
    tests=document.get('tests');require(isinstance(tests,list) and len(tests)==1 and isinstance(tests[0],dict),'Originalaccept discovery not singleton')
    row=tests[0];uid=row.get('uid');require(isinstance(uid,str) and uid,'Missing originalaccept actual UID')
    meta=row.get('type',{});location=row.get('location',{})
    require(meta.get('namespace')==NS and meta.get('typeName')==CLASS and meta.get('methodName')==method and
            type(meta.get('methodArity')) is int and meta['methodArity']==0 and meta.get('parameterTypeFullNames')==[] and
            meta.get('returnTypeFullName')=='System.Threading.Tasks.Task' and
            meta.get('assemblyFullName')==ASSEMBLY+', Version=2.0.4.0, Culture=neutral, PublicKeyToken=null','Wrong originalaccept discovered semantic identity')
    require(row.get('displayName') in (method,CLASS+'.'+method,NS+'.'+CLASS+'.'+method),'Wrong originalaccept discovered display')
    require(location.get('file')==REPORTED,'Wrong originalaccept discovered source path')
    require(type(location.get('lineStart')) is int and type(location.get('lineEnd')) is int and
            (location['lineStart'],location['lineEnd'])==source_spans(source)[method],'Wrong originalaccept discovered exact span')
    return {uid:row}


def normalized(value):
    require(isinstance(value,str),'Missing originalaccept exception text')
    return value.replace('\r\n','\n')


def exception_text(error):return error['type']+': '+error['message']+'\n'+error['stackTrace']


def event_graph(error):
    require(isinstance(error,dict) and set(error)=={'type','message','stackTrace'},'Wrong originalaccept event full graph shape')
    require(error['type']=='System.IO.IOException' and error['message']=='accept failed','Wrong originalaccept event cause')
    trace=error['stackTrace'];require(isinstance(trace,str) and '--->' not in trace,'Wrong originalaccept event topology')
    frames=[line.strip() for line in trace.splitlines() if line.strip().startswith('at ')]
    # Independently observed in all three exact PR logs and bound to exact main
    # production/helper bytes; not attributed using interleaved passed test names.
    expected=[
        'at SharpLink.Server.SharpLinkServer.RunAcceptLoopAsync(TaskCompletionSource`1 acceptStarted, TaskCompletionSource`1 running, CancellationToken acceptToken) in /_/src/SharpLink.Server/SharpLinkServer.RunLoop.cs:line 34',
        'at SharpLink.Server.SharpLinkServer.RunAcceptLoopAsync(TaskCompletionSource`1 acceptStarted, TaskCompletionSource`1 running, CancellationToken acceptToken) in /_/src/SharpLink.Server/SharpLinkServer.RunLoop.cs:line 91',
        'at SharpLink.Server.SharpLinkServer.ServerLifecycleCoordinator.WaitForAcceptRuntimeShutdownAsync() in /_/src/SharpLink.Server/ServerLifecycleCoordinator.cs:line 323',
        'at SharpLink.Server.SharpLinkServer.ServerLifecycleCoordinator.CompleteFaultedStopAsync() in /_/src/SharpLink.Server/ServerLifecycleCoordinator.cs:line 306',
        'at SharpLink.IntegrationTests.TransportConnectionIntegrationTests.EnsureThrows[TException](Task task, String name) in /_/test/SharpLink.IntegrationTests/TransportConnectionIntegrationTests.cs:line 66']
    require(len(frames)>=5 and frames[:5]==expected,'Wrong originalaccept actual propagation origin')


def classify(native,junit,ledger,scope,source,runtimes,paired_runtime=None,seen=(),engine_uids=(),all_uids=()):
    require(scope in ('baseline','baseline-green','fixed','control'),'Unknown originalaccept scope')
    method=CONTROL if scope=='control' else TARGET
    require(len(ledger)==1,'Wrong originalaccept ledger size')
    discovery({'schemaVersion':1,'tests':list(ledger.values())},method,source)
    require(type(native.get('schemaVersion')) is int and native['schemaVersion']==1,'Wrong originalaccept native schema')
    require(native.get('assemblyName')==ASSEMBLY and native.get('tunitVersion')=='1.78.0.0','Wrong originalaccept actual engine/assembly')
    runtime=native.get('runtimeVersion')
    require(isinstance(runtime,str) and re.fullmatch(r'\.NET 10\.0\.\d+',runtime) is not None and runtime.removeprefix('.NET ') in runtimes,
            'Originalaccept reported runtime outside observed installed candidates')
    require(paired_runtime is None or runtime==paired_runtime,'Originalaccept variants used different actual runtime')
    check_publication(native.get('publicationGeneration'),seen)
    event=scope=='baseline';total=2 if event else 1
    exact_summary(native.get('summary'),summary(total,int(event)))
    groups=native.get('groups');require(isinstance(groups,list) and len(groups)==(2 if event else 1),'Wrong originalaccept exact group count')
    by_class={x.get('className'):x for x in groups if isinstance(x,dict)}
    require(len(by_class)==len(groups) and set(by_class)==({CLASS,'UnknownClass'} if event else {CLASS}),'Unknown originalaccept group')
    group=by_class[CLASS];require(group.get('namespace')==NS,'Wrong originalaccept real namespace')
    exact_summary(group.get('summary'),summary(1,0))
    tests=group.get('tests');require(isinstance(tests,list) and len(tests)==1 and isinstance(tests[0],dict),'Originalaccept real body not singleton')
    test=tests[0];uid=next(iter(ledger));row=ledger[uid]
    require(test.get('id')==uid and test.get('displayName')==row['displayName'] and test.get('methodName')==method and test.get('className')==CLASS,'Wrong originalaccept native identity')
    require(test.get('filePath')==REPORTED and type(test.get('lineNumber')) is int and type(test.get('endLineNumber')) is int and
            (test['lineNumber'],test['endLineNumber'])==source_spans(source)[method],'Wrong originalaccept native source span')
    require(test.get('status')=='passed' and not {'exception','skipReason','retryAttempt','attempts'}.intersection(test),'Originalaccept target body not clean passed')
    require(type(test.get('durationMs')) in (int,float) and math.isfinite(test['durationMs']) and test['durationMs']>=0,'Wrong originalaccept real timing')
    require(isinstance(junit,str) and len(junit)<=16*1024*1024 and '<!DOCTYPE' not in junit and '<!ENTITY' not in junit,'Unsafe originalaccept JUnit')
    root=ET.fromstring(junit);require(root.tag=='testsuites' and len(root)==1 and root[0].tag=='testsuite','Wrong originalaccept JUnit root')
    suite=root[0];allowed=('properties','testcase','system-err') if event else ('properties','testcase')
    require(all(x.tag in allowed for x in suite) and len(suite.findall('properties'))<=1,'Unknown originalaccept JUnit metadata')
    cases=suite.findall('testcase');by_name={x.get('name'):x for x in cases}
    require(len(cases)==total and len(by_name)==total,'Wrong originalaccept JUnit bijection')
    case=by_name.get(row['displayName']);require(case is not None and case.get('classname')==CLASS and len(case)==0,'Originalaccept real JUnit body not clean passed')
    engine_uid=None
    if event:
        entries=native.get('unobservedTaskExceptions');require(isinstance(entries,list) and len(entries)==1,'Wrong originalaccept session event count')
        failure=entries[0];event_graph(failure)
        engine=by_class['UnknownClass'];require(engine.get('namespace')=='','Wrong originalaccept engine namespace')
        exact_summary(engine.get('summary'),summary(1,1))
        nodes=engine.get('tests');require(isinstance(nodes,list) and len(nodes)==1 and isinstance(nodes[0],dict),'Wrong originalaccept synthetic node count')
        node=nodes[0];engine_uid=node.get('id')
        require(isinstance(engine_uid,str) and re.fullmatch(r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}',engine_uid) is not None and str(uuid.UUID(engine_uid))==engine_uid,'Wrong originalaccept synthetic GUID')
        require(engine_uid not in ledger and engine_uid not in all_uids and engine_uid not in engine_uids,'Originalaccept synthetic UID collision')
        name='Unobserved task exception - IOException: accept failed'
        require(set(node)=={'id','displayName','methodName','className','status','durationMs','exception'} and node['displayName']==node['methodName']==name and
                node['className']=='UnknownClass' and node['status']=='error' and node['exception']==failure,'Unbound originalaccept engine cause/full graph')
        require(type(node['durationMs']) in (int,float) and math.isfinite(node['durationMs']) and node['durationMs']==0,'Wrong originalaccept synthetic timing')
        case=by_name.get(name);require(case is not None and case.get('classname')=='UnknownClass' and len(case)==1 and case[0].tag=='error','Wrong originalaccept engine JUnit mapping')
        error=case[0];require(not list(error) and error.attrib=={'type':failure['type'],'message':failure['message']} and normalized(error.text or '')==normalized(exception_text(failure)),
                              'Originalaccept native/JUnit full cause differs')
        stderr=suite.findall('system-err')
        require(len(stderr)==1 and not stderr[0].attrib and not list(stderr[0]) and normalized(stderr[0].text or '')==normalized('Unobserved task exception: '+exception_text(failure)+'\n'),
                'Wrong originalaccept suite event graph')
    else:
        require('unobservedTaskExceptions' not in native and not suite.findall('system-err'),'Unexpected originalaccept event field')
    for element in (root,suite):
        require(all(element.get(k)==v for k,v in {'name':ASSEMBLY,'tests':str(total),'errors':str(int(event)),'failures':'0','skipped':'0'}.items()),'Wrong originalaccept JUnit counts')
    return {'scope':scope,'classification':'ORIGINAL_ACCEPT_EVENT_REPRODUCED' if event else ('INCONCLUSIVE_BASELINE_NO_EVENT' if scope=='baseline-green' else 'ORIGINAL_ACCEPT_FIXED_PASS' if scope=='fixed' else 'NEIGHBOR_CONTROL_PASS'),
            'runtime':runtime,'realTests':1,'engineErrors':int(event),'eventCount':int(event),'publicationGeneration':native['publicationGeneration'],
            'engineUIDSHA256':hashlib.sha256(engine_uid.encode()).hexdigest() if engine_uid else None,
            'engineUID':engine_uid,'collectionEvidence':'no fixture weakref/counter inference; actual engine event only' if event else 'no per-object collection claim'}
