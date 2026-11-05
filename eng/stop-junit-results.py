"""Prospective, bounded JUnit TEXT observations for two existing bool cases.

Not a .NET exception parser. Every observation may have originated in an
untrusted exception message or custom ToString implementation. No authentication,
causal graph, runtime frame, Stop object, reference identity, or absence claim.
Caller owns original full reconciliation, failure recording, stable seals and
owner/deadline gates. This module never runs commands, writes, or mutates inputs.
"""
import hashlib
import json
from pathlib import Path
import re

CODES = frozenset(('SJ_SOURCE', 'SJ_GATE', 'SJ_IDENTITY', 'SJ_JUNIT', 'SJ_LIMIT'))
BASE_HEAD = '5b5c776c65d3ea320920a00e5dba91372678e55e' # replaced below
FULL_ORDINARY_COUNT = 2879
MAX_TEXT_CHARS = 1024 * 1024
MAX_TEXT_BYTES = 1024 * 1024
MAX_SCAN_CHARS = 65536
MAX_SCAN_LINES = 1024
MAX_LINE_CHARS = 2048
MAX_OBSERVATIONS = 64
MAX_OUTPUT_BYTES = 65536
SOURCE_PINS = {'eng/lifetime-full-results.py': '754ff4e17ca43b1fc2984d0617275e9bce7a3cef8051a2aab387290b00bbea84',
 'eng/protocol-lifetime-history/argument-display-name-proof.json': '37147528b72191820bfc81686c74f37baf6ac2f62369d6222c58697f81edd82b',
 'src/SharpLink.Client/SharpLinkClient.ResponseLifetime.cs': 'c223e209243e9352e160c2e7a04180c12864f865dae3eea2a1f2c4d87c7f9b19',
 'src/SharpLink.Client/SharpLinkClient.cs': '0096ed43927669451702fc148fd0e933a05ec4005ee5a0c5b2bd4ff24bbf0af8',
 'src/SharpLink.Runtime/FrameworkTaskSupervisor.cs': 'f0bd27e13492e2888dc1c850eaafeabd2166568704543fe88bf4d1f42e068c51',
 'src/SharpLink.Runtime/PhysicalTaskFailureException.cs': 'dbfb8036397db61943739d98834414f1b956f42351fcaeab730f8fc907053ca3',
 'src/SharpLink.Runtime/PooledAsyncStreamDispatcher.OriginalLease.cs': 'dc128b36a7318b2964f1813c57ed621475c3056c1ed5ed99451d163080e1dfa4',
 'src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs': '96b7337e509af1d0c9e879fac3b079804c1f747b139c7f12c70372e0f99a21e7',
 'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs': '9e92501f78b14811cb2bbbdcbf59fcbd9e93b0a93d6d9d428d88c0bad8209bca'}
FRAME_SPANS = ({'end': 429,
  'member': 'Failure',
  'source': 'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'start': 424,
  'tag': 'bounded-observer'},
 {'end': 276,
  'member': 'Fixture.CleanupAsync',
  'source': 'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'start': 238,
  'tag': 'fixture-cleanup'},
 {'end': 275,
  'member': 'Fixture.CleanupAsync local ObserveStop',
  'source': 'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'start': 271,
  'tag': 'cleanup-stop-observation'},
 {'end': 159,
  'member': 'StopAsync',
  'source': 'src/SharpLink.Client/SharpLinkClient.cs',
  'start': 141,
  'tag': 'cached-original-stop-task'},
 {'end': 218,
  'member': 'StopCoreAsync',
  'source': 'src/SharpLink.Client/SharpLinkClient.cs',
  'start': 161,
  'tag': 'stop-cleanup'},
 {'end': 262,
  'member': 'ThrowStopCleanupFailures',
  'source': 'src/SharpLink.Client/SharpLinkClient.cs',
  'start': 255,
  'tag': 'stop-failure-combiner'},
 {'end': 385,
  'member': 'Complete',
  'source': 'src/SharpLink.Runtime/FrameworkTaskSupervisor.cs',
  'start': 315,
  'tag': 'strict-physical-outcome'},
 {'end': 436,
  'member': 'DrainCoreAsync',
  'source': 'src/SharpLink.Runtime/FrameworkTaskSupervisor.cs',
  'start': 409,
  'tag': 'supervisor-drain'},
 {'end': 281,
  'member': 'RunDetachedAsync',
  'source': 'src/SharpLink.Client/SharpLinkClient.ResponseLifetime.cs',
  'start': 270,
  'tag': 'response-physical-worker'},
 {'end': 295,
  'member': 'ReleaseTransferredOwnership',
  'source': 'src/SharpLink.Client/SharpLinkClient.ResponseLifetime.cs',
  'start': 283,
  'tag': 'response-owner-refund'},
 {'end': 420,
  'member': 'CleanupResponseAsync',
  'source': 'src/SharpLink.Client/SharpLinkClient.ResponseLifetime.cs',
  'start': 344,
  'tag': 'response-physical-cleanup'},
 {'end': 139,
  'member': 'DisposeWinnerAsync',
  'source': 'src/SharpLink.Runtime/PooledAsyncStreamDispatcher.OriginalLease.cs',
  'start': 52,
  'tag': 'original-dispatcher-cleanup'},
 {'end': 940,
  'member': 'SlowMoveNextAsync',
  'source': 'src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs',
  'start': 870,
  'tag': 'original-response-move'},
 {'end': 1448,
  'member': 'ThrowIfEnumerationCanceled',
  'source': 'src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs',
  'start': 1443,
  'tag': 'enumeration-cancellation'})

REPORTER_PROOF = {
    'package': 'TUnit/1.73.19',
    'commit': 'ff2dc5186b05eddc8f6aea03a18784df3aa6e737',
    'path': 'src/TUnit.Engine/Xml/JUnitXmlWriter.cs',
    'sha256': 'ff9b06a167dabb1ccae5194c582c2ede1379fd32acf4857acfd341b1e07b242c',
    'gitBlob': '38ba830f332377645be42db4efe1fc253fdb815d',
    'failureLines': [255, 265], 'errorLines': [277, 286],
    'value': 'SanitizeForXml(exception.ToString())',
    'runtimeTextGrammarPinned': False,
}
EXCEPTION_TYPES = {
    'System.AggregateException': 'aggregate',
    'System.InvalidOperationException': 'invalid-operation',
    'System.OperationCanceledException': 'operation-cancelled',
    'System.Threading.Tasks.TaskCanceledException': 'task-cancelled',
    'System.TimeoutException': 'timeout',
    'System.ObjectDisposedException': 'object-disposed',
    'System.ArgumentException': 'argument',
    'System.ArgumentNullException': 'argument-null',
    'System.ArgumentOutOfRangeException': 'argument-range',
    'System.NullReferenceException': 'null-reference',
    'System.Exception': 'exception',
    'SharpLink.Abstractions.SharpLinkException': 'sharplink-exception',
    'SharpLink.Runtime.PhysicalTaskFailureException': 'physical-task-failure',
    'SharpLink.Runtime.SharpLinkProtocolViolationException': 'protocol-violation',
    'TUnit.Assertions.Exceptions.AssertionException': 'assertion',
}
# Recognized lexical spellings only. Prefixes and ordinals do NOT establish
# aggregate edges, nesting depth, sibling count, or message provenance.
_HEADER = re.compile(r'^(?: ---> (?:\(Inner Exception #[0-9]{1,3}\) )?)?'
                     r'([A-Za-z_][A-Za-z0-9_.+`]{0,254})(?::(?: |$)|$)')
_TEST_TYPE = 'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests'
_CLIENT_TYPE = 'SharpLink.Client.SharpLinkClient'
_SUPERVISOR_TYPE = 'SharpLink.Runtime.FrameworkTaskSupervisor'
_DISPATCHER_TYPES = ('SharpLink.Runtime.PooledAsyncStreamDispatcher`1',
                     'SharpLink.Runtime.PooledAsyncStreamDispatcher`1[T]')
_RESPONSE_TYPES = tuple(_CLIENT_TYPE + a + 'ContractCallSite' + b +
                        'InterceptedAsyncEnumerable' + generic
                        for a in ('.', '+') for b in ('.', '+')
                        for generic in ('`1', '`1[T]'))
_FIXTURE_TYPES = tuple(_TEST_TYPE + sep + 'Fixture' for sep in ('.', '+'))
# Exact signature lexemes are finite. Unsupported runtime formatting is unknown.
_RULES = (
    ((_TEST_TYPE,), 'Failure', 'Task task'),
    (_FIXTURE_TYPES, 'CleanupAsync', 'Exception primary'),
    (_FIXTURE_TYPES, None, 'Task operation'),
    ((_CLIENT_TYPE,), 'StopAsync', 'CancellationToken cancellationToken'),
    ((_CLIENT_TYPE,), 'StopCoreAsync', ''),
    ((_CLIENT_TYPE,), 'ThrowStopCleanupFailures', 'List`1 failures'),
    ((_SUPERVISOR_TYPE,), 'Complete', 'FrameworkTaskRegistration registration, Task completedTask'),
    ((_SUPERVISOR_TYPE,), 'DrainCoreAsync', 'Task signal'),
    (_RESPONSE_TYPES, 'RunDetachedAsync', 'Task start'),
    (_RESPONSE_TYPES, 'ReleaseTransferredOwnership', 'Exception failure'),
    (_RESPONSE_TYPES, 'CleanupResponseAsync', ''),
    (_DISPATCHER_TYPES, 'DisposeWinnerAsync', 'Int64 disposingLease, Boolean exactOutcome'),
    (_DISPATCHER_TYPES, 'SlowMoveNextAsync', 'Int64 consumerLeaseState'),
    (_DISPATCHER_TYPES, 'ThrowIfEnumerationCanceled', ''),
)

def _frame_patterns():
    patterns = []
    for span, (types, member, signature) in zip(FRAME_SPANS, _RULES):
        name = re.escape(member) if member is not None else r'<CleanupAsync>g__ObserveStop\|[0-9]{1,3}_[0-9]{1,3}'
        typ = '(?:' + '|'.join(re.escape(x) for x in types) + ')'
        pattern = re.compile(r'^   at ' + typ + r'\.' + name + r'\(' + re.escape(signature) +
                             r'\) in /_/' + re.escape(span['source']) + r':line ([0-9]{1,4})$')
        patterns.append((pattern, span))
    return tuple(patterns)
_FRAME_PATTERNS = _frame_patterns()


def verify_sources(r, source_root):
    """Stable source reads only; r is the caller's verified census module."""
    root = Path(source_root)
    for name, expected in SOURCE_PINS.items():
        r.require(r.sha(root / name) == expected, 'SJ_SOURCE')


def observe_text(r, text):
    """Scan bounded text; return only finite labels, pinned frames and counters.

    Input limits reject. Scan/line/output caps omit complete lines or tokens with
    explicit flags. A partial final scan-boundary line is NEVER interpreted.
    Apparent input truncation cannot be reliably detected and is never certified.
    """
    r.require(type(text) is str and len(text) <= MAX_TEXT_CHARS, 'SJ_LIMIT')
    try:
        byte_count = len(text.encode('utf-8'))
    except UnicodeEncodeError as error:
        raise r.Rejected('SJ_JUNIT') from error
    r.require(byte_count <= MAX_TEXT_BYTES, 'SJ_LIMIT')
    limit = min(len(text), MAX_SCAN_CHARS)
    position = 0
    lines = 0
    observations = []
    unknown_lines = 0
    unknown_headers = 0
    long_lines = 0
    omitted = 0
    partial_boundary = False
    while position < limit and lines < MAX_SCAN_LINES:
        newline = text.find('\n', position, limit)
        if newline < 0:
            if limit < len(text):
                partial_boundary = True
                break
            end = limit
            next_position = limit
        else:
            end = newline
            next_position = newline + 1
        raw = text[position:end]
        position = next_position
        lines += 1
        if raw.endswith('\r'):
            raw = raw[:-1]
        if len(raw) > MAX_LINE_CHARS:
            long_lines += 1
            continue
        observation = None
        if '\r' not in raw:
            header = _HEADER.match(raw)
            if header:
                category = EXCEPTION_TYPES.get(header.group(1), 'unknown')
                # Bare ordinary words are not unknown-type header observations.
                if category != 'unknown' or raw[header.end(1):header.end(1)+1] == ':':
                    unknown_headers += category == 'unknown'
                    observation = {'kind': 'exception-header-shaped-text', 'category': category}
            else:
                for pattern, span in _FRAME_PATTERNS:
                    match = pattern.fullmatch(raw)
                    if match and span['start'] <= int(match.group(1)) <= span['end']:
                        observation = {'kind': 'source-frame-shaped-text', 'tag': span['tag'],
                                       'source': span['source'], 'member': span['member'],
                                       'line': int(match.group(1))}
                        break
        if observation is None:
            unknown_lines += 1
        elif len(observations) < MAX_OBSERVATIONS:
            observations.append({'textLine': lines, **observation})
        else:
            omitted += 1
    scan_truncated = position < len(text)
    return {
        'evidenceSource': 'junit-exception-text',
        'evidenceMeaning': 'unauthenticated-lexical-observations-only',
        'mayOriginateInExceptionMessageOrCustomToString': True,
        'authenticatedExceptionTypesOrFrames': False,
        'exceptionGraphObserved': False,
        'aggregateSiblingCausesObserved': False,
        'caughtStopObjectObserved': False,
        'referenceIdentityObserved': False,
        'sourceTextCompleteness': 'not-established',
        'sourceTruncationDetectable': False,
        'inputCharacterCount': len(text), 'inputByteCount': byte_count,
        'scannedLineCount': lines,
        'unrecognizedScannedLineCount': unknown_lines,
        'unknownHeaderShapedLineCount': unknown_headers,
        'overlongLineCount': long_lines,
        'omittedRecognizedObservationCount': omitted,
        'scanTruncated': scan_truncated,
        'partialBoundaryLineNotInterpreted': partial_boundary,
        'projectionTruncated': scan_truncated or long_lines > 0 or omitted > 0,
        'observations': observations,
    }


def project(r, lifetime, native, junit, ordinary, explicit, facts, source_root):
    """Return two exact bool rows, atomically, AFTER original global classify.

    lifetime must be the caller's SHA-verified eng/lifetime-full-results.py;
    use the existing module, not a new import or filtered reports. Keep original
    UE_RESULTS/facts/errors unchanged even if this optional projection rejects.
    Dependency LP_* codes, filesystem failures and original exceptions propagate.
    """
    verify_sources(r, source_root)
    lifetime.verify_sources(r, source_root)
    r.require(type(ordinary) is dict and len(ordinary) == FULL_ORDINARY_COUNT and
              type(explicit) is set and len(explicit) == 5, 'SJ_GATE')
    selections = lifetime.select_fourteen(r, ordinary, explicit)
    proven = lifetime.argument_proofs(selections)
    r.require(proven.get(0) is True, 'SJ_IDENTITY')
    by_uid, by_key = lifetime.full_domains(r, native, junit, ordinary, facts)
    output = []
    for index, (uid, row) in enumerate(selections[:2]):
        ident = lifetime.argument_identity(index, (uid, row), proven)
        r.require(ident.get('kind') == 'source-literal-boolean' and
                  type(ident.get('value')) is bool and ident.get('limitation') == 'none', 'SJ_IDENTITY')
        test = lifetime.selected_test_shape(r, by_uid[uid])
        status = test['status']
        key = (lifetime.xml_name(row['displayName']), lifetime.xml_name(row['type']['typeName']),
               lifetime.STATUS_KIND[status])
        matches = by_key[key]
        r.require(len(matches) == 1 and sum(len(v) for k, v in by_key.items() if k[:2] == key[:2]) == 1, 'SJ_JUNIT')
        case = matches[0]
        r.require(set(case.attrib) == {'name', 'classname', 'time'}, 'SJ_JUNIT')
        observation = None
        if status in ('error', 'failed'):
            child = case[0]
            r.require(set(child.attrib) == {'message', 'type'} and len(child) == 0 and
                      child.get('message') == lifetime.xml_name(test['exception']['message']) and
                      child.get('type') == test['exception']['type'], 'SJ_JUNIT')
            observation = observe_text(r, child.text or '')
        output.append({
            'duplex': ident['value'], 'argumentIdentity': ident,
            'source': lifetime.CASES[0][0],
            'method': '.'.join(lifetime.CASES[0][1:4]),
            'status': status,
            'observationAvailability': 'bounded-text-observations' if observation is not None else 'not-an-exception-error-or-failure',
            'textObservation': observation,
        })
    r.require(len(output) == 2 and {x['duplex'] for x in output} == {False, True}, 'SJ_IDENTITY')
    output.sort(key=lambda x: x['duplex'])
    result = {
        'schemaVersion': 1,
        'scope': 'two-existing-response-deadline-bool-cases-within-full-ordinary-run',
        'completeCaseCoverage': True, 'selectedCaseCount': 2,
        'doesNotQualifyFullSuite': True,
        'doesNotEstablishHistoricalCause': True,
        'nativeFullReportCanonicalSHA256': facts['nativeReportSHA256'],
        'junitFullReportDecodedTextSHA256': hashlib.sha256(junit.encode()).hexdigest(),
        'fullResultUIDSetSHA256': facts['resultUIDSetSHA256'],
        'sourcePinsSHA256': r.digest(SOURCE_PINS),
        'cases': output,
    }
    r.require(len(json.dumps(result, sort_keys=True, separators=(',', ':')).encode()) <= MAX_OUTPUT_BYTES, 'SJ_LIMIT')
    return result
