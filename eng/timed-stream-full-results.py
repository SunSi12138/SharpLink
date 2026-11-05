"""Read-only four-case projection from ORIGINAL full ordinary reports.

Caller must first close the owner, seal reports, reconcile the complete native/
JUnit/discovery domain and retain all original global failures. This module runs
no commands, writes no files, imports no runner/catalog, and mutates no input.
It returns one complete projection or raises a fixed-code rejection; the caller
publishes the returned object atomically and keeps its global result unchanged.
"""
from collections import Counter
import hashlib
import math
from pathlib import Path
import re
import xml.etree.ElementTree as ET

CODES = frozenset(('TP_SOURCE','TP_GATE','TP_SELECTION','TP_IDENTITY','TP_SHAPE',
                   'TP_LIMIT','TP_EXCEPTION','TP_RETRY','TP_JUNIT'))
SOURCE_PINS = {'eng/stream-negotiation-history/boolean-display-name-proof.json': '1917416924ccc1f506b7922aff4872d8b7bac20bfa2147da12347129c4cfed6e',
 'src/SharpLink.Client/ClientStreamPublicationGroup.cs': '8a006afa636f61a9af8179b5f4c582ba893336e497d168fd07415317a5eec153',
 'src/SharpLink.Client/SharpLinkClient.cs': '0096ed43927669451702fc148fd0e933a05ec4005ee5a0c5b2bd4ff24bbf0af8',
 'src/SharpLink.Runtime/FrameworkTaskSupervisor.cs': 'f0bd27e13492e2888dc1c850eaafeabd2166568704543fe88bf4d1f42e068c51',
 'src/SharpLink.Runtime/PhysicalTaskFailureException.cs': 'dbfb8036397db61943739d98834414f1b956f42351fcaeab730f8fc907053ca3',
 'src/SharpLink.Runtime/RpcSession.StreamPublicationFence.cs': '5c27aa880f0c9e3e544321c75453280abfd2f3684cb73f0912424c04fe70513c',
 'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs': 'bb0bf4e489c2bc58ca070c496cc33a0681c874331b574f40005ba2e1ef14ff15'}
CASES = (('test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  'SharpLink.UnitTests.Client',
  'SharpLinkClientTimeBudgetTests',
  'TimedClientStreamShouldPublishCreationTimeBudgetAndFailThroughPendingDeadline',
  188,
  257),
 ('test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  'SharpLink.UnitTests.Client',
  'SharpLinkClientTimeBudgetTests',
  'TimedClientStreamShouldPublishCreationTimeBudgetAndFailThroughPendingDeadline',
  188,
  257),
 ('test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  'SharpLink.UnitTests.Client',
  'SharpLinkClientTimeBudgetTests',
  'ExplicitMethodTimeoutShouldOverrideClientDefaultTimeBudget',
  18,
  52),
 ('test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  'SharpLink.UnitTests.Client',
  'SharpLinkClientTimeBudgetTests',
  'InheritedTimeBudgetShouldCapSelectedMethodPolicyWithoutRestartingIt',
  55,
  93))
ATTRIBUTE_LINES = (185,185,17,54)
EXCEPTION_CATEGORIES = {'SharpLink.Abstractions.SharpLinkException': 'sharplink-exception',
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
EXCEPTION_TAGS = (((0, 1),
  'System.Exception',
  'the wire TimeBudget must be the remaining sampled once when the frame is created',
  'wire-budget-sample',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  231),
 ((0, 1),
  'System.Exception',
  'the producer must belong to the exact emitted head',
  'producer-head-identity',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  241),
 ((0, 1),
  'System.Exception',
  'producer entry must not complete the pending call',
  'premature-pending-completion',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  242),
 ((0, 1),
  'System.Exception',
  'an emitted client-stream Request whose budget elapsed must fail through its pending deadline',
  'deadline-code',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  248),
 ((0, 1),
  'System.Exception',
  'an expired head must never enter the producer writer',
  'expired-producer-start',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  250),
 ((0, 1),
  'System.Exception',
  'expected SharpLinkException',
  'missing-expected-sharplink-error',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  664),
 ((0, 1),
  'System.Exception',
  'expected exactly one ready connection',
  'ready-connection-count',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  650),
 ((2,),
  'System.Exception',
  'explicit method timeout should emit a TimeBudget',
  'explicit-budget-flag',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  46),
 ((2,),
  'System.Exception',
  'method timeout must override, not be min-capped by, the 30 second client fallback',
  'explicit-policy-override',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  48),
 ((2,),
  'System.Exception',
  'method-timeout override response',
  'explicit-response',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  51),
 ((3,),
  'System.Exception',
  "a downstream call must propagate the parent's remaining TimeBudget instead of restarting 120 seconds",
  'inherited-remaining-budget',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  90),
 ((3,),
  'System.Exception',
  'inherited-budget response',
  'inherited-response',
  'test/SharpLink.UnitTests/Client/SharpLinkClientTimeBudgetTests.cs',
  92))
STATUS_KIND = {'passed':'passed','failed':'failure','error':'error','timedOut':'error','cancelled':'error','skipped':'skipped'}


def keys(r, obj, required, optional=()):
    r.require(type(obj) is dict and set(required) <= set(obj) <= set(required) | set(optional), 'TP_SHAPE')


def string(r, value):
    r.require(type(value) is str and len(value) <= 1024 * 1024, 'TP_SHAPE')


def number(r, value):
    r.require(type(value) in (int, float) and math.isfinite(value), 'TP_SHAPE')


def strings(r, values):
    r.require(type(values) is list and len(values) <= 1024, 'TP_LIMIT')
    for value in values: string(r, value)


def pairs(r, values):
    r.require(type(values) is list and len(values) <= 1024, 'TP_LIMIT')
    for value in values:
        keys(r, value, ('key', 'value'))
        for item in value.values(): string(r, item)


def exception_shape(r, exception, depth=0):
    r.require(depth < 16, 'TP_LIMIT')
    keys(r, exception, ('type', 'message'), ('stackTrace', 'innerException'))
    for name in ('type', 'message', 'stackTrace'):
        if name in exception: string(r, exception[name])
    if 'innerException' in exception: exception_shape(r, exception['innerException'], depth + 1)


def argument_identity(index, selection, pair_proven=False):
    uid, row = selection
    result = {'caseID': hashlib.sha256(uid.encode()).hexdigest(),
              'kind': 'opaque-boolean-case' if index < 2 else 'parameterless',
              'parameter': 'expiresDuringEmission' if index < 2 else None,
              'value': None,
              'limitation': 'boolean-value-not-proven' if index < 2 else 'none'}
    # Pinned TUnit default formatting is conditional: a changed/custom name
    # stays opaque. No substring, UID-order or attribute-order inference.
    if index < 2 and pair_proven:
        exact = {CASES[index][3] + '(True)': True, CASES[index][3] + '(False)': False}
        if row['displayName'] in exact:
            result.update(kind='source-literal-boolean', value=exact[row['displayName']],
                          limitation='none', proof='pinned-default-display-name-exact-match')
    return result


def boolean_pair_proven(selections):
    if len(selections) < 2: return False
    names = [row['displayName'] for _, row in selections[:2]]
    return set(names) == {CASES[0][3] + '(True)', CASES[0][3] + '(False)'}


def projected_exception(r, index, exception):
    exception_shape(r, exception)
    result = []; current = exception
    while current is not None:
        frames, unknown, omitted = source_frames(r, index, current.get('stackTrace', ''))
        row = {'depth': len(result), 'category': EXCEPTION_CATEGORIES.get(current['type'], 'unknown'),
               'assertionTag': 'unknown', 'sourceFrames': frames, 'unrecognizedStackLineCount': unknown, 'omittedRecognizedStackLineCount': omitted}
        for indexes, typ, message, tag, path, line in EXCEPTION_TAGS:
            if index in indexes and (current['type'], current['message']) == (typ, message):
                row.update(assertionTag=tag, exactMessageSource={'source': path, 'line': line})
        result.append(row); current = current.get('innerException')
    return result


def source_frames(r, index, stack):
    source, namespace, cls, method, start, end = CASES[index]
    frame_map = {}; full = namespace + '.' + cls
    signature = 'Boolean expiresDuringEmission' if index < 2 else ''
    for line in range(start, end + 1):
        frame_map[f'   at {full}.{method}({signature}) in /_/{source}:line {line}'] = (source, method, line)
    helpers = (('Ensure', 'Boolean condition, String message', 759, 763),
               ('CaptureSharpLinkExceptionAsync', 'Task operation', 654, 665),
               ('GetOnlyReadyConnection', 'SharpLinkClient client', 644, 652))
    for member, signature, first, last in helpers:
        for line in range(first, last + 1):
            frame_map[f'   at {full}.{member}({signature}) in /_/{source}:line {line}'] = (source, member, line)
    if index < 4:
        product = 'src/SharpLink.Client/SharpLinkClient.cs'
        for member, signature, first, last in (
                ('DisposeAsync', '', 139, 139),
                ('StopAsync', 'CancellationToken cancellationToken', 141, 159),
                ('StopCoreAsync', '', 161, 218),
                ('ThrowStopCleanupFailures', 'List`1 failures', 255, 262)):
            for line in range(first, last + 1):
                frame_map[f'   at SharpLink.Client.SharpLinkClient.{member}({signature}) in /_/{product}:line {line}'] = (product, member, line)
    if index < 4:
        physical = (
            ('src/SharpLink.Client/ClientStreamPublicationGroup.cs', 'SharpLink.Client.ClientStreamPublicationGroup', 'CompleteCleanupAsync', 'TaskCompletionSource completion, RpcDeadline deadline, Exception setupFailure', 87, 130),
            ('src/SharpLink.Client/ClientStreamPublicationGroup.cs', 'SharpLink.Client.ClientStreamPublicationGroup', 'PublishCleanupAsync', 'SharpLinkException error', 132, 147),
            ('src/SharpLink.Client/ClientStreamPublicationGroup.cs', 'SharpLink.Client.ClientStreamPublicationGroup', 'FinishAsync', 'Exception publicationFailure, Boolean startupFailureOwnedByCaller', 245, 360),
            ('src/SharpLink.Runtime/FrameworkTaskSupervisor.cs', 'SharpLink.Runtime.FrameworkTaskSupervisor', 'DrainCoreAsync', 'Task signal', 409, 436),
            ('src/SharpLink.Runtime/RpcSession.StreamPublicationFence.cs', 'SharpLink.Runtime.RpcSession', 'CompleteStreamFenceCoreAsync', 'RpcStreamPublication group, RpcFramePublicationRole role', 28, 75))
        for path, typ, member, signature, first, last in physical:
            for line in range(first, last + 1):
                frame_map[f'   at {typ}.{member}({signature}) in /_/{path}:line {line}'] = (path, member, line)
    lines = stack.splitlines()
    r.require(len(lines) <= 4096, 'TP_LIMIT')
    output = [dict(zip(('source', 'member', 'line'), frame_map[raw])) for raw in lines if raw in frame_map]
    return output[:64], len(lines) - len(output), max(0, len(output) - 64)


def verify_sources(r, root):
    root = Path(root)
    for name, digest in SOURCE_PINS.items():
        # The caller's stable reader rejects symlinks and races and preserves
        # private filesystem failures. Nothing from a local path is projected.
        r.require(r.sha(root / name) == digest, 'TP_SOURCE')


def select_four(r, ordinary, explicit):
    r.require(type(ordinary) is dict and 4 <= len(ordinary) <= 100000, 'TP_SELECTION')
    r.require(type(explicit) is set and not (set(ordinary) & explicit), 'TP_SELECTION')
    output = []
    for index in (0,2,3):
        source, namespace, cls, method, start, end = CASES[index]
        matches = []
        for uid, row in ordinary.items():
            r.require(type(row) is dict and type(row.get('type')) is dict, 'TP_SHAPE')
            meta = row['type']
            if (meta.get('namespace'),meta.get('typeName'),meta.get('methodName')) != (namespace,cls,method): continue
            keys(r,meta,('assemblyFullName','namespace','typeName','methodName','methodArity','returnTypeFullName','parameterTypeFullNames'))
            r.require(meta['assemblyFullName'] == 'SharpLink.UnitTests, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null' and
                      type(meta['methodArity']) is int and meta['methodArity'] == 0 and
                      meta['returnTypeFullName'] == 'System.Threading.Tasks.Task' and
                      meta['parameterTypeFullNames'] == (['System.Boolean'] if index == 0 else []), 'TP_SELECTION')
            r.require(type(uid) is str and 0 < len(uid) <= 8192 and not any(ord(c)<32 for c in uid) and row.get('uid') == uid, 'TP_SELECTION')
            string(r,row.get('displayName'))
            loc=row.get('location');keys(r,loc,('file','lineStart','lineEnd'))
            r.require(loc['file']=='/_/'+source and type(loc['lineStart']) is int and loc['lineStart']==ATTRIBUTE_LINES[index] and
                      type(loc['lineEnd']) is int and loc['lineEnd']==end,'TP_SELECTION')
            matches.append((uid,row))
        r.require(len(matches)==(2 if index==0 else 1),'TP_SELECTION')
        output.extend(sorted(matches,key=lambda pair:hashlib.sha256(pair[0].encode()).hexdigest()))
    r.require(len(output)==4 and len({uid for uid,_ in output})==4,'TP_SELECTION')
    return output


def selected_test_shape(r, test):
    required = ('id','displayName','methodName','className','status','durationMs','filePath','lineNumber','endLineNumber')
    optional = ('startTime','endTime','exception','output','errorOutput','categories','customProperties',
                'sourceRelativePath','skipReason','retryAttempt','attempts','traceId','spanId','additionalTraceIds')
    keys(r,test,required,optional)
    r.require(not {'retryAttempt','attempts'} & set(test),'TP_RETRY')
    for name in set(required+optional)-{'durationMs','lineNumber','endLineNumber','exception','categories','customProperties','additionalTraceIds','retryAttempt','attempts'}:
        if name in test: string(r,test[name])
    number(r,test['durationMs']);r.require(test['durationMs']>=0,'TP_SHAPE')
    for name in ('categories','additionalTraceIds'):
        if name in test: strings(r,test[name])
    if 'customProperties' in test:pairs(r,test['customProperties'])
    status=test['status'];r.require(status in STATUS_KIND,'TP_EXCEPTION')
    if status in ('passed','skipped'):
        r.require('exception' not in test and (status=='skipped' or 'skipReason' not in test),'TP_EXCEPTION')
    else:
        r.require('exception' in test and 'skipReason' not in test,'TP_EXCEPTION')
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
    # Facts must be the terminal return from COMPLETE global reconciliation,
    # not partially populated facts left behind when its classifier raised.
    r.require(type(facts) is dict and facts.get('ownerClosed') is True and facts.get('executionClosureObserved') is True and
              type(facts.get('observed')) is int and facts['observed']==len(ordinary) and
              type(facts.get('expected')) is int and facts['expected']==len(ordinary) and facts.get('missingCount')==0,'TP_GATE')
    r.require(type(native) is dict and type(native.get('schemaVersion')) is int and native['schemaVersion']==1 and
              native.get('assemblyName')=='SharpLink.UnitTests' and native.get('tunitVersion')=='1.73.19.0' and
              native.get('runtimeVersion')=='.NET 10.0.14','TP_IDENTITY')
    r.require(facts.get('nativeReportSHA256')==r.digest(native) and facts.get('resultUIDSetSHA256')==r.digest(sorted(ordinary)) and
              facts.get('statusCounts')==native.get('summary') and type(facts.get('retriesObserved')) is bool and
              type(facts.get('allOrdinaryPassed')) is bool,'TP_GATE')
    groups=native.get('groups');r.require(type(groups) is list and 0<len(groups)<=100000,'TP_SHAPE')
    by_uid={};keys_expected=Counter();group_ids=set()
    for group in groups:
        r.require(type(group) is dict and type(group.get('namespace')) is str and type(group.get('className')) is str,'TP_SHAPE')
        identity=(group['namespace'],group['className']);r.require(identity not in group_ids,'TP_IDENTITY');group_ids.add(identity)
        tests=group.get('tests');r.require(type(tests) is list and 0<len(tests)<=len(ordinary),'TP_SHAPE')
        for test in tests:
            r.require(type(test) is dict and type(test.get('id')) is str,'TP_SHAPE');uid=test['id']
            r.require(uid in ordinary and uid not in by_uid,'TP_IDENTITY')
            row=ordinary[uid];meta=row['type'];loc=row['location'];status=test.get('status')
            r.require(type(status) is str and status in STATUS_KIND,'TP_IDENTITY')
            r.require(test.get('displayName')==row['displayName'] and test.get('methodName')==meta['methodName'] and
                      test.get('className')==group['className']==meta['typeName'] and group['namespace']==meta['namespace'] and
                      test.get('filePath')==loc['file'] and type(test.get('lineNumber')) is int and test['lineNumber']==loc['lineStart'] and
                      type(test.get('endLineNumber')) is int and test['endLineNumber']==loc['lineEnd'],'TP_IDENTITY')
            by_uid[uid]=test;keys_expected[(xml_name(row['displayName']),xml_name(meta['typeName']),STATUS_KIND[status])]+=1
    r.require(set(by_uid)==set(ordinary),'TP_IDENTITY')
    r.require(type(junit) is str and len(junit.encode())<=16*1024*1024 and '<!DOCTYPE' not in junit and '<!ENTITY' not in junit,'TP_JUNIT')
    try:xml=ET.fromstring(junit)
    except ET.ParseError as error:raise r.Rejected('TP_JUNIT') from error
    r.require(xml.tag=='testsuites' and len(xml)==1 and xml[0].tag=='testsuite','TP_JUNIT')
    suite=xml[0];r.require(all(x.tag in ('properties','testcase') for x in suite) and len(suite.findall('properties'))<=1,'TP_JUNIT')
    by_key={};counts=Counter()
    for case in suite.findall('testcase'):
        r.require(all(x.tag in ('failure','error','skipped') for x in case) and len(case)<=1,'TP_JUNIT')
        kind=case[0].tag if len(case) else 'passed';key=(case.get('name'),case.get('classname'),kind)
        by_key.setdefault(key,[]).append(case);counts[key]+=1
    r.require(counts==keys_expected,'TP_JUNIT')
    totals={'name':'SharpLink.UnitTests','tests':str(len(ordinary)),
            'failures':str(sum(v for k,v in counts.items() if k[2]=='failure')),
            'errors':str(sum(v for k,v in counts.items() if k[2]=='error')),
            'skipped':str(sum(v for k,v in counts.items() if k[2]=='skipped'))}
    r.require(all(all(node.get(k)==v for k,v in totals.items()) for node in (xml,suite)),'TP_JUNIT')
    return by_uid,by_key


def project(r,native,junit,ordinary,explicit,facts,source_root):
    """Return safe four-case evidence; never write or mutate global facts.

    r is the caller's already hash-verified census module (require, Rejected,
    sha and digest). Run only after the caller's global classify returns.
    Parent source/report seals and owner receipts remain authoritative.
    """
    verify_sources(r,source_root)
    selections=select_four(r,ordinary,explicit)
    by_uid,by_key=full_domains(r,native,junit,ordinary,facts)
    proven=boolean_pair_proven(selections);output=[]
    for index,(uid,row) in enumerate(selections):
        test=selected_test_shape(r,by_uid[uid]);status=test['status']
        key=(xml_name(row['displayName']),xml_name(row['type']['typeName']),STATUS_KIND[status])
        matches=by_key[key];r.require(len(matches)==1,'TP_JUNIT');case=matches[0]
        r.require(set(case.attrib)=={'name','classname','time'},'TP_JUNIT')
        if 'exception' in test:
            child=case[0];r.require(set(child.attrib)=={'message','type'} and len(child)==0,'TP_JUNIT')
            r.require(child.get('message')==xml_name(test['exception']['message']) and child.get('type')==test['exception']['type'],'TP_JUNIT')
        source,namespace,cls,method,_,_=CASES[index]
        output.append({'case':index+1,'source':source,'method':namespace+'.'+cls+'.'+method,
            'argumentIdentity':argument_identity(index,(uid,row),proven),'status':status,'selectedPassed':status=='passed',
            'exceptions':projected_exception(r,index,test['exception']) if 'exception' in test else [],
            'exceptionEvidenceScope':'serialized-innerException-chain-only','aggregateSiblingCausesObserved':False,
            'bodyVersusDisposal':'inconclusive' if status!='passed' else 'not-applicable'})
    return {'schemaVersion':1,'scope':'four-existing-timed-cases-within-full-ordinary-run','complete':True,
            'selectedCaseCount':4,'allSelectedPassed':all(x['selectedPassed'] for x in output),
            'doesNotQualifyFullSuite':True,'historicalFullRunArgumentMappingAvailable':False,
            'nativeFullReportCanonicalSHA256':facts['nativeReportSHA256'],
            'junitFullReportDecodedTextSHA256':hashlib.sha256(junit.encode()).hexdigest(),
            'fullResultUIDSetSHA256':facts['resultUIDSetSHA256'],'sourcePinsSHA256':r.digest(SOURCE_PINS),
            'booleanPairProven':proven,'cases':output}
