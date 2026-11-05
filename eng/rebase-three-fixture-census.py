#!/usr/bin/env python3
"""Reproduce the candidate census from a preserved frozen catalog, without .NET.

Offsets in the original catalog are Unicode text offsets; span hashes are UTF-8.
Only thirteen known test bodies may differ. Every other method slice is required to
remain byte-identical, including every shifted method in the five edited test files.
"""
import copy
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
BASELINE = 'eng/fixture-migration-baseline'
FROZEN_SHA256 = '2c34614a0bc918c48b860e795f3f331d0243065f05f0c9530e6aa0d430faeb66'
BASE_PRODUCT_TREE = 'bfc211191d91b5a77c6a8f0085a0c00a459c5583'
BASE_TEST_TREE = '690953054b445203d5b74614f954c8b7dc8d41ba'
PARENT_COMMIT = 'd6c4d41bca201b2bd82e2e917cfd93d701976b5a'
PARENT_TREE = 'd3a64a8353239d4038f2f833385b7d2881b61d3c'
PARENT_PRODUCT_TREE = 'c3a94c896418a5d5ec62646461f17c67dc89bc96'
PROTOCOL = 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs'
NEGOTIATOR = 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs'
COMPATIBILITY = 'test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs'
PROGRESS = 'test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs'
STACK = 'test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs'
OWNED_FRAME = 'src/SharpLink.Runtime/OwnedFrame.cs'
CANDIDATE_PINS = {
    NEGOTIATOR: 'd40c522f8bc8805860c9a29288109221545c69c0d598ef5ce6c206efe13e6f57',
    COMPATIBILITY: '46c916c63f7d93941eaa02841e0d255e6c5d9b1761e49a23f4057cef3cb5cf1b',
    STACK: '94ac76dbf52335d3b7703d9fac0aef118716cf8eb16061ce4a203138e9955f0d',
    OWNED_FRAME: 'c3a6ed62dd0d0738b838c76354b2b730c2808c2db37e5c88c821cb95142d3195',
    PROTOCOL: '00745a416645a7c5e83a934e47900f75a2c656ab6b072ac9029e8aa3b8aeeb85',
    PROGRESS: '70e7f967dbd40b85d41b7df22131fe48fdb39a5e59789a5b5034561e2c1a8a7c',
}
CHANGED_METHODS = {(PROTOCOL, 'WriterAndParserShouldRoundTripFrame'),
    (PROTOCOL, 'HeaderWithEveryByteInSeparateSegmentShouldParse'),
    (PROGRESS, 'ProgressFlagDoesNotGrowOwnedFrame'),
    (PROTOCOL, 'ZeroThroughFourteenHeaderBytesShouldRemainPartial'),
    (STACK, 'FrameInitializationShouldTouchOnlyItsOwnFifteenBytes'),
    (STACK, 'ExactWriterLimitAndDisposedLeaseMustRemainEnforced'),
    (PROTOCOL, 'MultipleFramesFollowedByHalfFrameShouldPreserveRemainder'),
    (PROTOCOL, 'InvalidLengthTypeFlagsAndRequestIdShouldBeRejected'),
    (PROTOCOL, 'HealthControlFramesShouldRequireBoundedPayloadAndRequestId'),
    (PROTOCOL, 'HandshakeRequestAndResponseShouldRoundTrip'),
    (NEGOTIATOR, 'ClientOfferShouldContainOnlyPolicyAndAuthenticationInputs'),
    (NEGOTIATOR, 'ImplementedCapabilitiesShouldRemainExplicitAndProtocolRecognized'),
    (COMPATIBILITY, 'StructuredErrorShapeShouldRejectPublishedMinorThreePeers')}


def require(value, code):
    if not value: raise ValueError(code)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True, allow_nan=False).encode()


def render(value):
    return (json.dumps(value, ensure_ascii=True, allow_nan=False, indent=2) + '\n').encode()


def object_id(kind, data):
    return hashlib.sha1(kind.encode() + b' ' + str(len(data)).encode() + b'\0' + data).digest()


def tree_id(root, replacements=None, prefix=''):
    """Git tree ID from regular source files, with an optional frozen overlay."""
    replacements = replacements or {}
    entries = []
    for path in root.iterdir():
        require(not path.is_symlink(), 'REBIND_SYMLINK')
        relative = prefix + path.name
        if path.is_dir():
            mode = b'40000'; digest = bytes.fromhex(tree_id(path, replacements, relative + '/')); sort_name = path.name + '/'
        else:
            require(path.is_file(), 'REBIND_FILE_KIND')
            mode = b'100755' if path.stat().st_mode & 0o111 else b'100644'
            digest = object_id('blob', replacements.get(relative, path.read_bytes())); sort_name = path.name
        entries.append((sort_name.encode(), mode + b' ' + path.name.encode() + b'\0' + digest))
    return object_id('tree', b''.join(row[1] for row in sorted(entries))).hex()


def text_span(text, method):
    p = method['sourceProvenance']; span = text[p['startOffset']:p['endOffset']]
    require(sha(span.encode()) == p['spanSHA256'], 'REBIND_OLD_SPAN')
    require(text.count('\n', 0, p['startOffset']) + 1 == method['attributeLine'], 'REBIND_OLD_START')
    require(text.count('\n', 0, p['endOffset']) + 1 == method['endLine'], 'REBIND_OLD_END')
    return span


def relocate(old, new, method):
    p = method['sourceProvenance']; before = text_span(old, method)
    altered = (method['source'], method['methodName']) in CHANGED_METHODS
    if not altered:
        require(new.count(before) == 1, 'REBIND_UNCHANGED_SLICE')
        start = new.index(before); end = start + len(before)
    else:
        # These exact pinned methods have no nested block at four-space indent.
        # The signature, attribute prefix, and closing-boundary shape are fixed.
        require(before.count(p['signature']) == 1, 'REBIND_ORIGINAL_SIGNATURE')
        marker = before[:before.index(p['signature'])] + p['signature'] + '\n    {'
        require(before.startswith(marker) and new.count(marker) == 1, 'REBIND_CHANGED_SIGNATURE')
        start = new.index(marker)
        closing = new.index('\n    }\n', start + len(marker))
        end = closing + len('\n    }')
        require(new[start:end] != before, 'REBIND_EXPECTED_BODY_CHANGE')
    after = new[start:end]
    result = copy.deepcopy(method); q = result['sourceProvenance']
    result['attributeLine'] = new.count('\n', 0, start) + 1
    result['signatureLine'] = result['attributeLine'] + method['signatureLine'] - method['attributeLine']
    result['endLine'] = new.count('\n', 0, end) + 1
    q.update(testAttributeLine=result['attributeLine'], startOffset=start, endOffset=end, spanSHA256=sha(after.encode()))
    # Identity, signatures, parameters, attributes, configuration, eligibility,
    # multiplicity, and every other provenance field are copied unchanged.
    return result, altered


HELPER_SIGNATURE = '    private static byte[] MutateHeader(int? length = null, byte? type = null, byte? flags = null)'
HISTORICAL_DIAGNOSTIC_SHA256 = 'f2f09025a1cc611049c32ec83ad6be2ef2cd8a6351efb9191588a02d761424e1'


def helper_span(text):
    require(text.count(HELPER_SIGNATURE) == 1, 'REBIND_HELPER_SIGNATURE')
    start = text.index(HELPER_SIGNATURE)
    end = text.index('\n    }\n', start) + len('\n    }')
    return start, end, text[start:end]


def outside_methods(text, methods, helper=False):
    spans = [(m['sourceProvenance']['startOffset'], m['sourceProvenance']['endOffset']) for m in methods]
    if helper: spans.append(helper_span(text)[:2])
    cursor = 0; result = []
    for start, end in sorted(spans):
        require(cursor <= start < end <= len(text), 'REBIND_OVERLAPPING_SPANS')
        result.append(text[cursor:start]); cursor = end
    result.append(text[cursor:])
    return ''.join(result)


def helper_delta(old_text, new_text, old_methods, new_methods):
    # Remove only full catalogued test spans and this sole allowed helper.
    # Every remaining byte in all three parser/handshake files must be exact.
    for path in (PROTOCOL, NEGOTIATOR, COMPATIBILITY):
        before = outside_methods(old_text[path], [m for m in old_methods if m['source'] == path], path == PROTOCOL)
        after = outside_methods(new_text[path], [m for m in new_methods if m['source'] == path], path == PROTOCOL)
        require(before == after, 'REBIND_OTHER_HELPER_CHANGE')
    old_start, old_end, before = helper_span(old_text[PROTOCOL])
    new_start, new_end, after = helper_span(new_text[PROTOCOL])
    old_line = '        var frame = CreateFrame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1, []);'
    new_line = '        var frame = CreateFrame(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1,\n            [(byte)ProtocolV2CancelReason.UserCancellation]);'
    require(before.count(old_line) == 1 and after == before.replace(old_line, new_line), 'REBIND_HELPER_EXACT_DELTA')
    def callers(text, methods):
        return {m['methodName']: text[m['sourceProvenance']['startOffset']:m['sourceProvenance']['endOffset']].count('MutateHeader(')
                for m in methods if m['source'] == PROTOCOL and
                'MutateHeader(' in text[m['sourceProvenance']['startOffset']:m['sourceProvenance']['endOffset']]}
    old_calls = callers(old_text[PROTOCOL], old_methods); new_calls = callers(new_text[PROTOCOL], new_methods)
    require(old_calls == {'InvalidLengthTypeFlagsAndRequestIdShouldBeRejected': 5, 'ParserViolationsShouldCarryFixedLowCardinalityClassification': 2}
            and new_calls == {'InvalidLengthTypeFlagsAndRequestIdShouldBeRejected': 6, 'ParserViolationsShouldCarryFixedLowCardinalityClassification': 2}, 'REBIND_HELPER_CALLERS')
    return {'source': PROTOCOL, 'member': 'MutateHeader', 'signature': HELPER_SIGNATURE.strip(),
        'oldLines': [old_text[PROTOCOL].count('\n', 0, old_start) + 1, old_text[PROTOCOL].count('\n', 0, old_end) + 1],
        'newLines': [new_text[PROTOCOL].count('\n', 0, new_start) + 1, new_text[PROTOCOL].count('\n', 0, new_end) + 1],
        'oldSpanSHA256': sha(before.encode()), 'newSpanSHA256': sha(after.encode()),
        'change': 'Replace only the empty Cancel payload with the one-byte UserCancellation reason.',
        'callers': ['InvalidLengthTypeFlagsAndRequestIdShouldBeRejected', 'ParserViolationsShouldCarryFixedLowCardinalityClassification'],
        'oldCallSites': old_calls, 'newCallSites': new_calls, 'otherParserHandshakeNonMethodBytesUnchanged': True}


def generate(root):
    root = Path(root)
    frozen_bytes = (root / BASELINE / 'unit-census.json').read_bytes()
    require(sha(frozen_bytes) == FROZEN_SHA256, 'REBIND_FROZEN_HASH')
    frozen = json.loads(frozen_bytes); result = copy.deepcopy(frozen)
    require(sha((root / BASELINE / 'parser-handshake-diagnostic-result.json').read_bytes()) == HISTORICAL_DIAGNOSTIC_SHA256, 'REBIND_HISTORICAL_EVIDENCE')
    require(len(frozen['methods']) == 2463 and len(frozen['sourcePins']) == 1599, 'REBIND_COUNTS')
    old_text = {}; new_text = {}; overlays = {'src': {}, 'test': {}}
    for path, expected in CANDIDATE_PINS.items():
        old_bytes = (root / BASELINE / (path + '.txt')).read_bytes()
        new_bytes = (root / path).read_bytes()
        require(sha(old_bytes) == frozen['sourcePins'][path], 'REBIND_OLD_PIN')
        require(sha(new_bytes) == expected, 'REBIND_NEW_PIN')
        old_text[path] = old_bytes.decode(); new_text[path] = new_bytes.decode()
        require('\r' not in old_text[path] + new_text[path], 'REBIND_NEWLINES')
        result['sourcePins'][path] = expected
        domain, relative = path.split('/', 1)
        require(domain in overlays, 'REBIND_SOURCE_DOMAIN')
        overlays[domain][relative] = old_bytes
    require(tree_id(root / 'src', overlays['src']) == BASE_PRODUCT_TREE, 'REBIND_OTHER_PRODUCT_CHANGE')
    require(tree_id(root / 'test', overlays['test']) == BASE_TEST_TREE, 'REBIND_OTHER_TEST_CHANGE')
    for path, expected in result['sourcePins'].items():
        require(sha((root / path).read_bytes()) == expected, 'REBIND_ALL_SOURCE_PINS')
    affected = []; altered = []
    for i, method in enumerate(frozen['methods']):
        path = method['source']
        if path in CANDIDATE_PINS:
            rebound, body_changed = relocate(old_text[path], new_text[path], method)
            result['methods'][i] = rebound
            if rebound != method:
                affected.append({'source': path, 'method': method['methodName'],
                    'oldLines': [method['attributeLine'], method['signatureLine'], method['endLine']],
                    'newLines': [rebound['attributeLine'], rebound['signatureLine'], rebound['endLine']],
                    'oldOffsets': [method['sourceProvenance']['startOffset'], method['sourceProvenance']['endOffset']],
                    'newOffsets': [rebound['sourceProvenance']['startOffset'], rebound['sourceProvenance']['endOffset']],
                    'bodyChanged': body_changed,
                    'oldSpanSHA256': method['sourceProvenance']['spanSHA256'],
                    'newSpanSHA256': rebound['sourceProvenance']['spanSHA256']})
            if body_changed: altered.append((path, method['methodName']))
        else:
            text_span((root / path).read_text(), method)
    require(set(altered) == CHANGED_METHODS and len(altered) == 13, 'REBIND_THIRTEEN_BODIES')
    helper = helper_delta(old_text, new_text, frozen['methods'], result['methods'])
    historical = {key: result.pop(key) for key in ('sourceCheckpoint', 'sourceTree', 'sourceDirectorySHA256')}
    product_tree = tree_id(root / 'src'); test_tree = tree_id(root / 'test')
    require(product_tree == PARENT_PRODUCT_TREE, 'REBIND_PARENT_PRODUCT_CHANGE')
    result['sourceDerivation'] = {
        'kind': 'parser-handshake-fixture-source-rebase', 'frozenCatalogSHA256': FROZEN_SHA256,
        'historicalCatalogProvenance': historical, 'parentCommit': PARENT_COMMIT, 'parentTree': PARENT_TREE,
        'frozenProductTree': BASE_PRODUCT_TREE, 'candidateProductTree': product_tree, 'frozenTestTree': BASE_TEST_TREE, 'candidateTestTree': test_tree,
        'candidateSourcePinsSHA256': sha(canonical(result['sourcePins'])),
        'changedSourcePaths': sorted(CANDIDATE_PINS), 'changedMethodBodies': 13,
        'methodCount': 2463, 'sourcePinCount': 1599,
        'qualification': 'Derived source catalog only; not a test execution result.'}
    report = {'schemaVersion': 1, 'scope': 'parser-handshake-fixture-catalog-rebase',
        'frozenCatalogSHA256': FROZEN_SHA256, 'candidateCatalogSHA256': sha(render(result)),
        'parentCommit': PARENT_COMMIT, 'parentTree': PARENT_TREE,
        'frozenProductTree': BASE_PRODUCT_TREE, 'candidateProductTree': product_tree, 'frozenTestTree': BASE_TEST_TREE, 'candidateTestTree': test_tree,
        'methodCount': 2463, 'sourcePinCount': 1599, 'changedMethodBodyCount': 13,
        'unchangedMethodBodyCount': 2450, 'deletedMethods': 0, 'addedMethods': 0,
        'changedPins': {path: {'old': frozen['sourcePins'][path], 'new': result['sourcePins'][path]} for path in sorted(CANDIDATE_PINS)},
        'affectedMethods': affected, 'changedHelper': helper,
        'historicalFullOrdinaryRun': {'runId': 41940224644, 'ordinary': 2879, 'passed': 2589, 'errors': 290,
            'failingFiles': 92, 'failingMethodStatusRows': 253, 'qualified': False},
        'historicalFirstMigrationRun': {'runId': 41950562552, 'head': '4209233576c2100e0d5dc625308f49b76d500169',
            'complete': True, 'passed': 5, 'nonpassing': 1, 'qualified': False,
            'case4': {'measuredBytes': 64, 'comparatorBytes': 56, 'shapeGuardsPassed': True}},
        'historicalPackingRun': {'runId': 41952557024, 'head': '3f2f1d03da65844fba6f367811545f195ccf5206',
            'complete': True, 'passed': 8, 'nonpassing': 0, 'qualified': True, 'fullSuiteQualified': False},
        'historicalThirteenCaseRun': {'runId': 41954983558, 'head': '5e021af675f9252a7bb2ac4023d78bf8bec0aa92',
            'complete': True, 'passed': 13, 'nonpassing': 0, 'qualified': True, 'fullSuiteQualified': False},
        'historicalParserHandshakeDiagnostic': {'runId': 41957258310, 'jobId': 127015025107,
            'head': 'd6c4d41bca201b2bd82e2e917cfd93d701976b5a', 'complete': True,
            'passed': 4, 'nonpassing': 7, 'errorOccurrences': 21, 'qualified': False, 'fullSuiteQualified': False,
            'evidenceSHA256': sha((root / BASELINE / 'parser-handshake-diagnostic-result.json').read_bytes())},
        'candidateExecution': 'not run', 'fullSuiteQualified': False}
    return result, report


def main():
    require(sys.argv[1:] in (['--write'], ['--check']), 'REBIND_ARGUMENTS')
    catalog, report = generate(ROOT)
    outputs = {'eng/unit-census.json': render(catalog), 'eng/three-fixture-census-rebase.json': render(report)}
    for path, data in outputs.items():
        if sys.argv[1] == '--write': (ROOT / path).write_bytes(data)
        else: require((ROOT / path).read_bytes() == data, 'REBIND_REPRODUCIBILITY')
    print(json.dumps({'candidateCatalogSHA256': sha(outputs['eng/unit-census.json']),
        'frozenProductTree': BASE_PRODUCT_TREE, 'candidateProductTree': report['candidateProductTree'], 'testTree': report['candidateTestTree'],
        'methods': 2463, 'pins': 1599, 'changedBodies': 13, 'fullSuiteQualified': False}, sort_keys=True))


if __name__ == '__main__': main()
