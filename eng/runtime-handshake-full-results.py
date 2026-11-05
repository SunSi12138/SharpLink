"""Pure four-case runtime-handshake projection from original full ordinary reports.

Call only after owner closure, report sealing and COMPLETE global native/JUnit/
discovery reconciliation. No commands, report synthesis, file writes or imports
of runners/catalogs. Return all four rows atomically, or preserve rejection next
to unchanged global facts. Caught invalid-control errors are not serialized.
"""
from collections import Counter
import hashlib
import math
from pathlib import Path
import re
import xml.etree.ElementTree as ET

CODES=frozenset(('HP_SOURCE','HP_GATE','HP_SELECTION','HP_IDENTITY','HP_SHAPE',
                 'HP_LIMIT','HP_EXCEPTION','HP_RETRY','HP_JUNIT'))
FULL_CASE_COUNT=2879
SOURCE_PINS = {'src/SharpLink.Abstractions/ProtocolV2.cs': '1e0b64dcba691ed274e66984fb8b566a10c68e57a7bfc2a5eefb5a506db994a2',
 'src/SharpLink.Runtime/NegotiatedSessionOptions.cs': '42e605d69ba9d4735fa09aa8da82e02d3584cbc140f01c2a518c82a753253c0e',
 'src/SharpLink.Runtime/Protocol/Protocol3BindingLimitsCodec.cs': '2888b3474567a683c578972e29b0dbe703ca9ccde51f8cb15683edcaf92e1832',
 'src/SharpLink.Runtime/ProtocolV2/ProtocolV2Negotiator.cs': 'db95fa43e92596680d5b026ba8981f648770587e437d0addfe880ff28347fb74',
 'src/SharpLink.Runtime/ProtocolViolationReason.cs': 'be29a593585933e97a233b9a5500affdee02a7faf27d2bedd8682b5c61630bc1',
 'src/SharpLink.Runtime/RpcSession.Negotiation.cs': 'd1619e232f6a52c9381e2a87abb8febfe43bedc838d2055d3974925f85182a92',
 'src/SharpLink.Runtime/RpcSession.cs': '5d8652b8d6b36daf12fa53a4da522fecab5e297a7919262b8811304b2120e927',
 'src/SharpLink.Runtime/SharpLinkProtocolOptions.cs': 'bd6b8a97873caaef8c3b5093bc65d91a37d52186f12bf48d448f215685271256',
 'src/SharpLink.Runtime/SharpLinkRuntimeOptions.cs': 'f800fd9fb3003f051e9d25cfa743f3ea14f7e4b8dccca03d65eedf676a4b980f',
 'test/SharpLink.UnitTests/RpcSessionTestFixture.cs': 'eb2105841c2e684ec174a97b72eef69aafa4d221204195d3e798ed114a0eea6d',
 'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs': '16d8a6ddf73b868c011ed391c4f29d519621e770679c03913219ac037a4bc554'}

CASES = (('test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  'SharpLink.UnitTests.Runtime',
  'NegotiatedSessionOptionsTests',
  'HandshakeCompletionShouldPublishOneCompleteImmutableSnapshot',
  12,
  62),
 ('test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  'SharpLink.UnitTests.Runtime',
  'NegotiatedSessionOptionsTests',
  'ConcurrentHandshakeCompletionShouldHaveOneWinnerAndOneSnapshot',
  65,
  105),
 ('test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  'SharpLink.UnitTests.Runtime',
  'NegotiatedSessionOptionsTests',
  'HandshakeCompletionAndTerminalRaceShouldNeverPublishPartialState',
  184,
  219),
 ('test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  'SharpLink.UnitTests.Runtime',
  'NegotiatedSessionOptionsTests',
  'InvalidHandshakeOptionsShouldFaultWithoutPublishingReady',
  108,
  160))

ATTRIBUTE_LINES = (11,64,183,107)
PARAMETER_TYPES = ([],[],[],[])
RETURN_TYPES = ('System.Threading.Tasks.Task',)*4

EXCEPTION_TAGS = (((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Protocol3 requires revision 1 without downgrade.',
  'revision-mismatch',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  68),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated session omits Protocol3 mandatory capabilities.',
  'mandatory-capabilities-missing',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  70),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated session requires valid authority and binding limits.',
  'authority-or-binding-limits-invalid',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  73),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated capabilities contain unknown bits.',
  'unknown-capabilities',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  75),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated receive windows must be positive.',
  'receive-window-nonpositive',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  83),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'The negotiated connection window cannot be smaller than the stream window.',
  'connection-window-smaller',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  85),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated receive windows exceed the local flow-control limits.',
  'receive-window-exceeds-local',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  89),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated compression capability and provider binding must be published together.',
  'compression-capability-binding-mismatch',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  97),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated compression provider is missing.',
  'compression-provider-missing',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  112),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated compression profile is invalid.',
  'compression-profile-invalid',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  119),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated compression profile does not match its provider binding.',
  'compression-profile-provider-mismatch',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  127),
 ((0, 1, 2, 3),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated compression binding is not owned by this runtime context.',
  'compression-binding-foreign',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  139),
 ((0, 1, 2, 3),
  'SharpLink.Abstractions.SharpLinkException',
  'Negotiated session initialization failed.',
  'initialization-wrapper',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  25),
 ((3,),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated frame limit 1023 is outside the local protocol limits.',
  'frame-limit-outside-local',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  80),
 ((3,),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Negotiated frame limit 8193 is outside the local protocol limits.',
  'frame-limit-outside-local',
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  80),
 ((0,),
  'System.Exception',
  'the first valid handshake completion must win',
  'first-completion-did-not-win',
  'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  45),
 ((0,),
  'System.Exception',
  'a complete negotiated snapshot must become visible with Ready',
  'ready-not-published',
  'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  47),
 ((0,),
  'System.Exception',
  'Ready must never be observable without negotiated options',
  'ready-options-missing',
  'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  49),
 ((0,),
  'System.Exception',
  'the published snapshot must contain every negotiated scalar from one completion',
  'snapshot-scalars-mismatch',
  'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  55),
 ((0,),
  'System.Exception',
  'compression and flow-control bindings must be prepared before publication',
  'compression-flow-binding-incomplete',
  'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  57),
 ((0,),
  'System.Exception',
  'negotiated options must expose no mutable property setters',
  'snapshot-mutable-setter',
  'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  61),
 ((3,),
  'System.Exception',
  'the operation should throw a SharpLinkException',
  'invalid-expected-error-missing',
  'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  465))

FINITE_ASSERTION_TEMPLATES = ((1, 'round {round}: exactly one concurrent completion must win', 'concurrent-winner-count', 96, 100),
 (1, 'round {round}: the winner must atomically publish Ready and one snapshot', 'concurrent-ready-snapshot', 98, 100),
 (1,
  'round {round}: the published snapshot must contain exactly one winning proposal',
  'concurrent-mixed-proposal',
  103,
  100),
 (2, 'round {round}: terminal must win the final lifecycle state', 'terminal-final-state', 215, 100),
 (2,
  'round {round}: a snapshot may exist only when the atomic Ready publication won first',
  'terminal-snapshot-identity',
  217,
  100),
 (3, 'invalid case {index}: negotiation validation must use ProtocolViolation', 'invalid-error-code', 154, 9),
 (3, 'invalid case {index}: malformed negotiation must terminate the session', 'invalid-terminal-state', 156, 9),
 (3, 'invalid case {index}: failed negotiation must never publish Ready options', 'invalid-ready-published', 158, 9))
# Only source-bounded loop counters are enumerated. No report text is parsed
# as an index, interpolated into output, or admitted by substring matching.
EXCEPTION_TAGS += tuple(((index,), 'System.Exception', template.replace('{round}',str(value)).replace('{index}',str(value)), tag, CASES[index][0], line)
                        for index,template,tag,line,count in FINITE_ASSERTION_TEMPLATES for value in range(count))

EXTRA_FRAMES = (((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  'SharpLink.UnitTests.Runtime.NegotiatedSessionOptionsTests',
  'Ensure',
  'Boolean condition, String message',
  498,
  502),
 ((1, 2),
  'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  'SharpLink.UnitTests.Runtime.NegotiatedSessionOptionsTests',
  'CreateOptions',
  'ProtocolV2Capabilities capabilities, Int32 maxFramePayloadBytes',
  441,
  449),
 ((3,),
  'test/SharpLink.UnitTests/Runtime/NegotiatedSessionOptionsTests.cs',
  'SharpLink.UnitTests.Runtime.NegotiatedSessionOptionsTests',
  'CaptureSharpLinkException',
  'Action action',
  460,
  471),
 ((0, 1, 2, 3),
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  'SharpLink.Runtime.RpcSession',
  'TryCompleteHandshake',
  'NegotiatedSessionOptions options',
  5,
  41),
 ((0, 1, 2, 3),
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  'SharpLink.Runtime.RpcSession',
  'ValidateAndCreateNegotiatedFlowController',
  'NegotiatedSessionOptions options',
  64,
  107),
 ((0, 1, 2, 3),
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  'SharpLink.Runtime.RpcSession',
  'ValidateCompressionBinding',
  'SharpLinkCompressionProviderBinding binding',
  109,
  140),
 ((0, 1, 2, 3),
  'src/SharpLink.Runtime/RpcSession.Negotiation.cs',
  'SharpLink.Runtime.RpcSession',
  'NegotiationViolation',
  'String message, Exception innerException',
  142,
  148),
 ((0, 1, 2, 3), 'src/SharpLink.Runtime/RpcSession.cs', 'SharpLink.Runtime.RpcSession', 'DisposeAsync', '', 638, 699),
 ((0, 1, 2, 3),
  'src/SharpLink.Runtime/RpcSession.cs',
  'SharpLink.Runtime.RpcSession',
  'Fault',
  'Exception exception',
  605,
  606),
 ((0, 1, 2, 3),
  'src/SharpLink.Runtime/RpcSession.cs',
  'SharpLink.Runtime.RpcSession',
  'Fault',
  'Exception exception, SessionTerminalState state',
  614,
  636),
 ((2,),
  'src/SharpLink.Runtime/RpcSession.cs',
  'SharpLink.Runtime.RpcSession',
  'NotifyDisconnected',
  'Exception exception',
  602,
  603))

EXCEPTION_CATEGORIES = {'SharpLink.Abstractions.SharpLinkException': 'sharplink-exception',
 'SharpLink.Client.SharpLinkClient+ContractRouteFailure': 'contract-route-failure',
 'SharpLink.Runtime.PhysicalTaskFailureException': 'physical-task-failure',
 'SharpLink.Runtime.SharpLinkProtocolViolationException': 'protocol-violation',
 'System.AggregateException': 'aggregate',
 'System.ArgumentException': 'argument',
 'System.ArgumentNullException': 'argument-null',
 'System.ArgumentOutOfRangeException': 'argument-range',
 'System.BadImageFormatException': 'bad-image',
 'System.Exception': 'exception',
 'System.IO.FileLoadException': 'file-load',
 'System.IO.FileNotFoundException': 'file-not-found',
 'System.InvalidOperationException': 'invalid-operation',
 'System.MethodAccessException': 'method-access',
 'System.MissingMethodException': 'missing-method',
 'System.NullReferenceException': 'null-reference',
 'System.OperationCanceledException': 'operation-cancelled',
 'System.Threading.Tasks.TaskCanceledException': 'task-cancelled',
 'System.TimeoutException': 'timeout',
 'System.TypeInitializationException': 'type-initialization',
 'System.TypeLoadException': 'type-load',
 'TUnit.Assertions.Exceptions.AssertionException': 'assertion'}

STATUS_KIND = {'cancelled': 'error', 'error': 'error', 'failed': 'failure', 'passed': 'passed', 'skipped': 'skipped', 'timedOut': 'error'}

def keys(r, obj, required, optional=()):
    r.require(type(obj) is dict and set(required) <= set(obj) <= set(required) | set(optional), 'HP_SHAPE')

def string(r, value):
    r.require(type(value) is str and len(value) <= 1024 * 1024, 'HP_SHAPE')

def number(r, value):
    r.require(type(value) in (int, float) and math.isfinite(value), 'HP_SHAPE')

def strings(r, values):
    r.require(type(values) is list and len(values) <= 1024, 'HP_LIMIT')
    for value in values: string(r, value)

def pairs(r, values):
    r.require(type(values) is list and len(values) <= 1024, 'HP_LIMIT')
    for value in values:
        keys(r, value, ('key', 'value'))
        for item in value.values(): string(r, item)

def exception_shape(r, exception, depth=0):
    r.require(depth < 16, 'HP_LIMIT')
    keys(r, exception, ('type', 'message'), ('stackTrace', 'innerException'))
    for name in ('type', 'message', 'stackTrace'):
        if name in exception: string(r, exception[name])
    if 'innerException' in exception: exception_shape(r, exception['innerException'], depth + 1)

def verify_sources(r, root):
    root = Path(root)
    for name, digest in SOURCE_PINS.items():
        # The caller's stable reader rejects symlinks and races and preserves
        # private filesystem failures. Nothing from a local path is projected.
        r.require(r.sha(root / name) == digest, 'HP_SOURCE')

def selected_test_shape(r, test):
    required = ('id','displayName','methodName','className','status','durationMs','filePath','lineNumber','endLineNumber')
    optional = ('startTime','endTime','exception','output','errorOutput','categories','customProperties',
                'sourceRelativePath','skipReason','retryAttempt','attempts','traceId','spanId','additionalTraceIds')
    keys(r,test,required,optional)
    r.require(not {'retryAttempt','attempts'} & set(test),'HP_RETRY')
    for name in set(required+optional)-{'durationMs','lineNumber','endLineNumber','exception','categories','customProperties','additionalTraceIds','retryAttempt','attempts'}:
        if name in test: string(r,test[name])
    number(r,test['durationMs']);r.require(test['durationMs']>=0,'HP_SHAPE')
    for name in ('categories','additionalTraceIds'):
        if name in test: strings(r,test[name])
    if 'customProperties' in test:pairs(r,test['customProperties'])
    status=test['status'];r.require(status in STATUS_KIND,'HP_EXCEPTION')
    if status in ('passed','skipped'):
        r.require('exception' not in test and (status=='skipped' or 'skipReason' not in test),'HP_EXCEPTION')
    else:
        r.require('exception' in test and 'skipReason' not in test,'HP_EXCEPTION')
        exception_shape(r,test['exception'])
    return test

def xml_name(value):
    # Exact pinned writer normalization, including non-BMP UTF-16 code units.
    raw=value.encode('utf-16-le',errors='surrogatepass');output=[]
    for i in range(0,len(raw),2):
        code=int.from_bytes(raw[i:i+2],'little')
        output.append(chr(code) if code in (9,10,13) or 0x20<=code<=0xD7FF or 0xE000<=code<=0xFFFD else '[0x'+format(code,'X')+']')
    return ''.join(output)

def full_domains(r,native,junit,ordinary,facts):
    r.require(len(ordinary)==FULL_CASE_COUNT,'HP_GATE')
    # Facts must be the terminal return from COMPLETE global reconciliation,
    # not partially populated facts left behind when its classifier raised.
    r.require(type(facts) is dict and facts.get('ownerClosed') is True and facts.get('executionClosureObserved') is True and
              type(facts.get('observed')) is int and facts['observed']==len(ordinary) and
              type(facts.get('expected')) is int and facts['expected']==len(ordinary) and type(facts.get('missingCount')) is int and facts['missingCount']==0,'HP_GATE')
    r.require(type(native) is dict and type(native.get('schemaVersion')) is int and native['schemaVersion']==1 and
              native.get('assemblyName')=='SharpLink.UnitTests' and native.get('tunitVersion')=='1.73.19.0' and
              native.get('runtimeVersion')=='.NET 10.0.14','HP_IDENTITY')
    r.require(facts.get('nativeReportSHA256')==r.digest(native) and facts.get('resultUIDSetSHA256')==r.digest(sorted(ordinary)) and
              facts.get('statusCounts')==native.get('summary') and type(facts.get('retriesObserved')) is bool and
              type(facts.get('allOrdinaryPassed')) is bool,'HP_GATE')
    groups=native.get('groups');r.require(type(groups) is list and 0<len(groups)<=100000,'HP_SHAPE')
    by_uid={};keys_expected=Counter();group_ids=set()
    for group in groups:
        r.require(type(group) is dict and type(group.get('namespace')) is str and type(group.get('className')) is str,'HP_SHAPE')
        identity=(group['namespace'],group['className']);r.require(identity not in group_ids,'HP_IDENTITY');group_ids.add(identity)
        tests=group.get('tests');r.require(type(tests) is list and 0<len(tests)<=len(ordinary),'HP_SHAPE')
        for test in tests:
            r.require(type(test) is dict and type(test.get('id')) is str,'HP_SHAPE');uid=test['id']
            r.require(uid in ordinary and uid not in by_uid,'HP_IDENTITY')
            row=ordinary[uid];meta=row['type'];loc=row['location'];status=test.get('status')
            r.require(type(status) is str and status in STATUS_KIND,'HP_IDENTITY')
            r.require(test.get('displayName')==row['displayName'] and test.get('methodName')==meta['methodName'] and
                      test.get('className')==group['className']==meta['typeName'] and group['namespace']==meta['namespace'] and
                      test.get('filePath')==loc['file'] and type(test.get('lineNumber')) is int and test['lineNumber']==loc['lineStart'] and
                      type(test.get('endLineNumber')) is int and test['endLineNumber']==loc['lineEnd'],'HP_IDENTITY')
            by_uid[uid]=test;keys_expected[(xml_name(row['displayName']),xml_name(meta['typeName']),STATUS_KIND[status])]+=1
    r.require(set(by_uid)==set(ordinary),'HP_IDENTITY')
    r.require(type(junit) is str and len(junit.encode())<=16*1024*1024 and '<!DOCTYPE' not in junit and '<!ENTITY' not in junit,'HP_JUNIT')
    try:xml=ET.fromstring(junit)
    except ET.ParseError as error:raise r.Rejected('HP_JUNIT') from error
    r.require(xml.tag=='testsuites' and len(xml)==1 and xml[0].tag=='testsuite','HP_JUNIT')
    suite=xml[0];r.require(all(x.tag in ('properties','testcase') for x in suite) and len(suite.findall('properties'))<=1,'HP_JUNIT')
    by_key={};counts=Counter()
    for case in suite.findall('testcase'):
        r.require(all(x.tag in ('failure','error','skipped') for x in case) and len(case)<=1,'HP_JUNIT')
        kind=case[0].tag if len(case) else 'passed';key=(case.get('name'),case.get('classname'),kind)
        by_key.setdefault(key,[]).append(case);counts[key]+=1
    r.require(counts==keys_expected,'HP_JUNIT')
    totals={'name':'SharpLink.UnitTests','tests':str(len(ordinary)),
            'failures':str(sum(v for k,v in counts.items() if k[2]=='failure')),
            'errors':str(sum(v for k,v in counts.items() if k[2]=='error')),
            'skipped':str(sum(v for k,v in counts.items() if k[2]=='skipped'))}
    r.require(all(all(node.get(k)==v for k,v in totals.items()) for node in (xml,suite)),'HP_JUNIT')
    return by_uid,by_key


def argument_identity(selection):
    uid,_=selection
    return {'caseID':hashlib.sha256(uid.encode()).hexdigest(),'kind':'parameterless',
            'parameter':None,'value':None,'limitation':'none'}


def source_frames(r,index,stack):
    source,namespace,cls,method,start,end=CASES[index]
    frame_map={}
    for line in range(start,end+1):
        frame_map[f'   at {namespace}.{cls}.{method}() in /_/{source}:line {line}']=(source,method,line)
    for indexes,path,typ,member,signature,first,last in EXTRA_FRAMES:
        if index in indexes:
            for line in range(first,last+1):
                frame_map[f'   at {typ}.{member}({signature}) in /_/{path}:line {line}']=(path,member,line)
    # Compiler ordinals are bounded but never reported. Only unchanged no-arg
    # lambda source spans are accepted; a different stack spelling stays unknown.
    spans={1:((81,85,'first-completion-lambda'),(86,90,'second-completion-lambda')),
           2:((199,203,'completion-lambda'),(204,208,'termination-lambda')),
           3:((151,151,'invalid-completion-lambda'),)}.get(index,())
    prefix=re.escape(namespace+'.'+cls)+r'\.(?:<>c__DisplayClass[0-9]{1,3}_[0-9]{1,3}|<>c)\.'
    pattern=re.compile(r'^   at '+prefix+'<'+re.escape(method)+r'>b__[0-9]{1,3}(?:_[0-9]{1,3})?\(\) in /_/'+re.escape(source)+r':line ([0-9]{1,4})$')
    string(r,stack);lines=stack.splitlines();r.require(len(lines)<=4096,'HP_LIMIT')
    output=[];known=0
    for raw in lines:
        value=frame_map.get(raw)
        if value is None and spans:
            match=pattern.fullmatch(raw)
            if match:
                line=int(match.group(1))
                for first,last,label in spans:
                    if first<=line<=last:value=(source,method+'-'+label,line);break
        if value is not None:
            known+=1
            if len(output)<64:output.append(dict(zip(('source','member','line'),value)))
    return output,len(lines)-known,max(0,known-64)

def projected_exception(r, index, exception):
    exception_shape(r, exception)
    result = []; current = exception
    while current is not None:
        frames, unknown, omitted = source_frames(r,index, current.get('stackTrace', ''))
        row = {'depth': len(result), 'category': EXCEPTION_CATEGORIES.get(current['type'], 'unknown'),
               'assertionTag': 'unknown', 'sourceFrames': frames, 'unrecognizedStackLineCount': unknown, 'omittedRecognizedStackLineCount': omitted}
        for indexes, typ, message, tag, path, line in EXCEPTION_TAGS:
            if index in indexes and (current['type'], current['message']) == (typ, message):
                row.update(assertionTag=tag, exactMessageSource={'source': path, 'line': line})
        result.append(row); current = current.get('innerException')
    return result


def select_four(r,ordinary,explicit):
    r.require(type(ordinary) is dict and 4<=len(ordinary)<=100000,'HP_SELECTION')
    r.require(type(explicit) is set and not(set(ordinary)&explicit),'HP_SELECTION')
    output=[]
    for index,(source,namespace,cls,method,start,end) in enumerate(CASES):
        matches=[]
        for uid,row in ordinary.items():
            r.require(type(row) is dict and type(row.get('type')) is dict,'HP_SHAPE')
            meta=row['type']
            if (meta.get('namespace'),meta.get('typeName'),meta.get('methodName'))!=(namespace,cls,method):continue
            keys(r,meta,('assemblyFullName','namespace','typeName','methodName','methodArity','returnTypeFullName','parameterTypeFullNames'))
            r.require(meta['assemblyFullName']=='SharpLink.UnitTests, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null' and
                      type(meta['methodArity']) is int and meta['methodArity']==0 and
                      meta['returnTypeFullName']==RETURN_TYPES[index] and meta['parameterTypeFullNames']==[], 'HP_SELECTION')
            r.require(type(uid) is str and 0<len(uid)<=8192 and not any(ord(c)<32 for c in uid) and row.get('uid')==uid,'HP_SELECTION')
            string(r,row.get('displayName'));loc=row.get('location');keys(r,loc,('file','lineStart','lineEnd'))
            r.require(loc['file']=='/_/'+source and type(loc['lineStart']) is int and loc['lineStart']==ATTRIBUTE_LINES[index] and
                      type(loc['lineEnd']) is int and loc['lineEnd']==end,'HP_SELECTION')
            matches.append((uid,row))
        r.require(len(matches)==1,'HP_SELECTION');output.extend(matches)
    r.require(len(output)==4 and len({uid for uid,_ in output})==4,'HP_SELECTION')
    return output


def project(r,native,junit,ordinary,explicit,facts,source_root):
    """Atomic safe runtime4 evidence; never mutate reports or global facts.

    r is the already hash-verified census module (require, Rejected, sha, digest).
    Caller global classify must have returned on the full2879 domain. Preserve
    its failure/exit/rejection facts independently even when all four pass.
    """
    verify_sources(r,source_root)
    selections=select_four(r,ordinary,explicit)
    by_uid,by_key=full_domains(r,native,junit,ordinary,facts)
    output=[]
    for index,(uid,row) in enumerate(selections):
        test=selected_test_shape(r,by_uid[uid]);status=test['status']
        key=(xml_name(row['displayName']),xml_name(row['type']['typeName']),STATUS_KIND[status])
        matches=by_key[key]
        r.require(len(matches)==1 and sum(len(v) for k,v in by_key.items() if k[:2]==key[:2])==1,'HP_JUNIT')
        case=matches[0];r.require(set(case.attrib)=={'name','classname','time'},'HP_JUNIT')
        if 'exception' in test:
            child=case[0];r.require(set(child.attrib)=={'message','type'} and len(child)==0,'HP_JUNIT')
            r.require(child.get('message')==xml_name(test['exception']['message']) and child.get('type')==test['exception']['type'],'HP_JUNIT')
        source,namespace,cls,method,_,_=CASES[index]
        output.append({'case':index+1,'source':source,'method':namespace+'.'+cls+'.'+method,
            'argumentIdentity':argument_identity((uid,row)),'status':status,'selectedPassed':status=='passed',
            'exceptions':projected_exception(r,index,test['exception']) if 'exception' in test else [],
            'exceptionEvidenceScope':'serialized-innerException-chain-only','aggregateSiblingCausesObserved':False,
            'caseGroup':'invalid-control' if index==3 else 'handshake-publication',
            'caughtValidationExceptionsObserved':False,'internalIterationEvidence':'not-serialized',
            'bodyVersusCleanup':'inconclusive' if status!='passed' else 'not-applicable'})
    return {'schemaVersion':1,'scope':'four-existing-runtime-handshake-cases-within-full-ordinary-run','complete':True,
            'selectedCaseCount':4,'fullOrdinaryCaseCount':FULL_CASE_COUNT,
            'allSelectedPassed':all(x['selectedPassed'] for x in output),'doesNotQualifyFullSuite':True,
            'nativeFullReportCanonicalSHA256':facts['nativeReportSHA256'],
            'junitFullReportDecodedTextSHA256':hashlib.sha256(junit.encode()).hexdigest(),
            'fullResultUIDSetSHA256':facts['resultUIDSetSHA256'],'sourcePinsSHA256':r.digest(SOURCE_PINS),
            'cases':output}

