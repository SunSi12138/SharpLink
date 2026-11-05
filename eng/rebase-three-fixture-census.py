#!/usr/bin/env python3
"""Reproduce the candidate census from a preserved frozen catalog, without .NET.

Offsets in the original catalog are Unicode text offsets; span hashes are UTF-8.
Only twenty-seven known test bodies may differ. Every other method slice is required to
remain byte-identical, including every shifted method in the six edited test files.
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
PARENT_COMMIT = '0de69b451c744182f6962d253a1749899fdc29af'
PARENT_TREE = '49fe4b3a225289865bed31571f5d7edf6757e054'
PARENT_TEST_TREE = 'fb8f193f979ba936fcd3e51eff98562f27f06a50'
NEGOTIATOR_PARENT_TEST_TREE = '226e6404e69879868bc2f98037624ec357529f49'
PARENT_PRODUCT_TREE = 'c3a94c896418a5d5ec62646461f17c67dc89bc96'
PROTOCOL = 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs'
PREFERENCE = 'test/SharpLink.UnitTests/Protocol/ResponseCompressionPreferenceProtocolTests.cs'
NEGOTIATOR = 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs'
COMPATIBILITY = 'test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs'
PROGRESS = 'test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs'
STACK = 'test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs'
OWNED_FRAME = 'src/SharpLink.Runtime/OwnedFrame.cs'
CANDIDATE_PINS = {
    PREFERENCE: '7825d4bbad864fcf2063fdd97243c1cca26b311111b1282593e62ed613dae05d',
    NEGOTIATOR: '8e44d5a254ef72c6a8f9275430c5a22b2d99d03aacc9d9ad049ad267234dd1b9',
    COMPATIBILITY: '46c916c63f7d93941eaa02841e0d255e6c5d9b1761e49a23f4057cef3cb5cf1b',
    STACK: '94ac76dbf52335d3b7703d9fac0aef118716cf8eb16061ce4a203138e9955f0d',
    OWNED_FRAME: 'c3a6ed62dd0d0738b838c76354b2b730c2808c2db37e5c88c821cb95142d3195',
    PROTOCOL: '9f5ac65baae379a3d744eae2bdd6b703b1a5aa0309e7174ca792084bbdc36898',
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

NEW_NEGOTIATOR_METHODS = frozenset(('PolicyConstructionShouldRejectEveryIllegalLocalState', 'CapabilityMatrixShouldProduceSymmetricServerAndClientResults', 'UnsupportedRequiredCapabilityShouldReturnUnimplemented', 'ServerNegotiationShouldIntersectLimitsAtCurrentMinorBoundaries', 'ClientValidationShouldRejectOutOfOfferVersionCapabilitiesAndLimits', 'CompressionMatrixShouldHonorOptionalRequiredAndServerPreference', 'MalformedOfferMatrixShouldReturnProtocolViolation', 'MalformedResponseMatrixShouldReturnStructuredFailure', 'ServerResultShouldValidateToEquivalentClientSnapshot', 'RepeatedInputsShouldProduceEquivalentResultsAndErrors'))
CHANGED_METHODS |= {(NEGOTIATOR, name) for name in NEW_NEGOTIATOR_METHODS}
NEW_PROTOCOL_METHODS = frozenset(((PROTOCOL, 'HandshakeResponseShouldRequireCompressionCapabilityAndProfileTogether'),
    (PROTOCOL, 'HandshakeCapabilitiesShouldRejectInconsistentOrUnknownSets'),
    (PROTOCOL, 'CancelReasonShouldRoundTripAndEnforceNegotiatedShape'),
    (PREFERENCE, 'HandshakeShouldRoundTripFixedPreferenceGeneration')))
CHANGED_METHODS |= NEW_PROTOCOL_METHODS
PROTOCOL_PARENT_PINS = {PROTOCOL: '00745a416645a7c5e83a934e47900f75a2c656ab6b072ac9029e8aa3b8aeeb85',
    PREFERENCE: 'f903e42a339e9e07e0dab9cf4d496de1e0266017cd800362a800666a08172970'}
PROTOCOL_PARENT_FILES = {PROTOCOL: 'eng/protocol70-history/ProtocolV2Tests.cs.txt',
    PREFERENCE: 'eng/protocol70-history/ResponseCompressionPreferenceProtocolTests.cs.txt'}
PROTOCOL_HISTORY_PINS = {'eng/protocol70-history/ProtocolV2Tests.cs.txt': '00745a416645a7c5e83a934e47900f75a2c656ab6b072ac9029e8aa3b8aeeb85', 'eng/protocol70-history/ResponseCompressionPreferenceProtocolTests.cs.txt': 'f903e42a339e9e07e0dab9cf4d496de1e0266017cd800362a800666a08172970', 'eng/protocol70-history/full-ordinary-result.json': '073a199d62f9add8222789e3f4fec936377e64e2897cd5145497f046d0c3c8d6', 'eng/protocol70-history/parent-unit-census.json': '3d05292b01e9ec133f10fad420593895e122469eb82528d6190ac2e9a8dcde12', 'eng/protocol70-history/twenty-one-case-diagnostic-result.json': 'e6916265942d76dd66761a46d979014603c6f51035ec085687bb7b3af43fb5d4'}


def protocol_parent_overlays(root):
    result={}
    for path,preimage in PROTOCOL_PARENT_FILES.items():
        data=(root/preimage).read_bytes();require(sha(data)==PROTOCOL_PARENT_PINS[path], 'REBIND_PROTOCOL_PARENT_PIN')
        result[path.removeprefix('test/')]=data
    return result


def protocol_delta(root,candidate):
    parent_bytes=(root/'eng/protocol70-history/parent-unit-census.json').read_bytes()
    require(sha(parent_bytes)==PROTOCOL_HISTORY_PINS['eng/protocol70-history/parent-unit-census.json'],'REBIND_PROTOCOL_PARENT_CATALOG')
    parent=json.loads(parent_bytes)
    require(len(parent['methods'])==len(candidate['methods'])==2463,'REBIND_PROTOCOL_PARENT_METHODS')
    replacements=protocol_parent_overlays(root)
    require(tree_id(root/'test',replacements)==PARENT_TEST_TREE,'REBIND_PROTOCOL_PARENT_TREE')
    require(tree_id(root/'src')==PARENT_PRODUCT_TREE,'REBIND_PROTOCOL_PRODUCT_TREE')
    require({path for path in parent['sourcePins'] if parent['sourcePins'][path]!=candidate['sourcePins'][path]}==set(PROTOCOL_PARENT_PINS),'REBIND_PROTOCOL_PIN_SET')
    changes=[];edits={path:[] for path in PROTOCOL_PARENT_PINS};affected=[]
    for old,new in zip(parent['methods'],candidate['methods']):
        keys=('source','namespace','className','methodName','arity','parameters','return','configurations','initialEligibility','expectedCases')
        require(all(old[k]==new[k] for k in keys),'REBIND_PROTOCOL_METHOD_IDENTITY')
        require(old['sourceProvenance']['signature']==new['sourceProvenance']['signature'] and old['sourceProvenance']['attributes']==new['sourceProvenance']['attributes'],'REBIND_PROTOCOL_SIGNATURE')
        path=old['source'];p=old['sourceProvenance'];q=new['sourceProvenance']
        before=text_span(replacements[path.removeprefix('test/')].decode() if path in PROTOCOL_PARENT_PINS else (root/path).read_text(),old)
        after=text_span((root/path).read_text(),new)
        target=(path,old['methodName']) in NEW_PROTOCOL_METHODS
        require((before!=after)==target,'REBIND_PROTOCOL_EXACT_FOUR_BODIES')
        if path in PROTOCOL_PARENT_PINS:
            affected.append({'source':path,'method':old['methodName'],'oldLines':[old['attributeLine'],old['signatureLine'],old['endLine']],
                'newLines':[new['attributeLine'],new['signatureLine'],new['endLine']],'bodyChanged':target,
                'oldSpanSHA256':p['spanSHA256'],'newSpanSHA256':q['spanSHA256']})
        else:require(old==new,'REBIND_PROTOCOL_OTHER_METHOD_METADATA')
        if target:
            marker=before[:before.index(p['signature'])]+p['signature']+'\n    {'
            require(after.startswith(marker),'REBIND_PROTOCOL_METHOD_PREFIX')
            edits[path].append((q['startOffset'],q['endOffset'],before));changes.append((path,old['methodName']))
    for path,items in edits.items():
        restored=(root/path).read_text()
        for start,end,before in sorted(items,reverse=True):restored=restored[:start]+before+restored[end:]
        require(restored.encode()==replacements[path.removeprefix('test/')],'REBIND_PROTOCOL_OTHER_BYTES')
    require(set(changes)==NEW_PROTOCOL_METHODS and len(changes)==4 and len(affected)==30,'REBIND_PROTOCOL_DELTA_COUNT')
    return {'parentCatalogSHA256':sha(parent_bytes),'parentTestTree':PARENT_TEST_TREE,'candidateProductUnchanged':True,
        'changedBodiesAgainstParent':[{'source':p,'method':m} for p,m in sorted(changes)],
        'parentPreimageSHA256':dict(PROTOCOL_PARENT_PINS),'allOtherParentMethodsByteIdentical':2459,
        'allOtherParentTestBytesUnchanged':True,'methodRecordsInTwoFiles':affected,'nonMethodBytesUnchanged':True}



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


NEGOTIATOR_HISTORY = 'eng/pure-negotiation-history/diagnostic-negotiator-preimage.cs.txt'
NEGOTIATOR_PARENT_SHA256 = 'd40c522f8bc8805860c9a29288109221545c69c0d598ef5ce6c206efe13e6f57'
NEGOTIATOR_OLD_HELPERS_SHA256 = '7aae0354318ceaefd55fcc553821a1c9a9d35ea8db6fa61eac422cf7bc80d481'
NEGOTIATOR_NEW_HELPERS_SHA256 = 'a60ed6ecd641befc0081dac9a85f71d50b6cfb9e3a4b60eaa6a220eeaf57888c'
NEGOTIATOR_OLD_HELPERS_START = '    private static ProtocolV2HandshakeRequest CreateOffer('
NEGOTIATOR_NEW_HELPERS_START = '    private const ProtocolV2Capabilities MandatoryCapabilities ='
NEGOTIATOR_HELPERS_END = '    private static SharpLinkCompressionProviderBinding[] Bindings'
PURE_HISTORY_PINS = {'eng/pure-negotiation-history/diagnostic-negotiator-preimage.cs.txt': 'd40c522f8bc8805860c9a29288109221545c69c0d598ef5ce6c206efe13e6f57', 'eng/pure-negotiation-history/sixteen-case-diagnostic-result.json': '57700796654908f5d3abb3064c04c746384253bad0668f5b4e9dab387373a55a', 'eng/pure-negotiation-history/full-ordinary-result.json': '8b5c5a42210d118055655b75e456cf65f114b9e88fb63debbd8b122c55997e56'}


def negotiation_helper_block(text, candidate):
    marker = NEGOTIATOR_NEW_HELPERS_START if candidate else NEGOTIATOR_OLD_HELPERS_START
    require(text.count(marker) == 1 and text.count(NEGOTIATOR_HELPERS_END) == 1, 'REBIND_NEGOTIATOR_HELPER_BOUNDARY')
    start = text.index(marker); end = text.index(NEGOTIATOR_HELPERS_END, start)
    value = text[start:end]
    expected = NEGOTIATOR_NEW_HELPERS_SHA256 if candidate else NEGOTIATOR_OLD_HELPERS_SHA256
    require(sha(value.encode()) == expected, 'REBIND_NEGOTIATOR_HELPER_HASH')
    return start, end, value


def normalized_negotiator_helpers(old, new):
    _, _, before = negotiation_helper_block(old, False)
    start, end, after = negotiation_helper_block(new, True)
    for signature in ('private static ProtocolV2HandshakeRequest CreateOffer(', 'private static ProtocolV2NegotiationPolicy CreatePolicy('):
        a = before[before.index(signature):].split('=>', 1)[0]
        b = after[after.index(signature):].split('=>', 1)[0]
        require(a == b, 'REBIND_NEGOTIATOR_HELPER_SIGNATURE')
    return new[:start] + before + new[end:]


def negotiation_delta(root, frozen, candidate):
    parent_bytes = (root / NEGOTIATOR_HISTORY).read_bytes()
    require(sha(parent_bytes) == NEGOTIATOR_PARENT_SHA256, 'REBIND_NEGOTIATOR_PARENT')
    parent = parent_bytes.decode(); current = (root / NEGOTIATOR).read_text()
    historical_overlays=protocol_parent_overlays(root);historical_overlays[NEGOTIATOR.removeprefix('test/')]=parent_bytes
    require(tree_id(root / 'test', historical_overlays) == NEGOTIATOR_PARENT_TEST_TREE, 'REBIND_OTHER_PARENT_TEST_CHANGE')
    old_start, old_end, old_block = negotiation_helper_block(parent, False)
    new_start, new_end, new_block = negotiation_helper_block(current, True)
    restored = current; edits = [(new_start, new_end, old_block)]; changes = []; preserved = []
    methods = [m for m in candidate['methods'] if m['source'] == NEGOTIATOR]
    require(len(methods) == 12 and all(m['expectedCases'] == 1 and m['parameters'] == [] for m in methods), 'REBIND_NEGOTIATOR_METHOD_COUNT')
    for method in methods:
        q = method['sourceProvenance']; after = current[q['startOffset']:q['endOffset']]
        signature = q['signature']; marker = after[:after.index(signature)] + signature + '\n    {'
        require(parent.count(marker) == 1, 'REBIND_NEGOTIATOR_PARENT_SIGNATURE')
        start = parent.index(marker); end = parent.index('\n    }\n', start + len(marker)) + len('\n    }')
        before = parent[start:end]
        if method['methodName'] in NEW_NEGOTIATOR_METHODS:
            require(before != after, 'REBIND_NEGOTIATOR_EXPECTED_BODY_CHANGE')
            edits.append((q['startOffset'], q['endOffset'], before))
            changes.append({'method': method['methodName'], 'oldSpanSHA256': sha(before.encode()), 'newSpanSHA256': sha(after.encode())})
        else:
            require(before == after, 'REBIND_PRIOR_NEGOTIATOR_BODY_CHANGE'); preserved.append(method['methodName'])
    require(len(changes) == 10 and len(preserved) == 2, 'REBIND_NEGOTIATOR_DELTA_COUNT')
    for start, end, value in sorted(edits, reverse=True): restored = restored[:start] + value + restored[end:]
    require(restored == parent, 'REBIND_NEGOTIATOR_UNAPPROVED_BYTES')
    return {'source': NEGOTIATOR, 'parentSHA256': NEGOTIATOR_PARENT_SHA256,
        'candidateSHA256': CANDIDATE_PINS[NEGOTIATOR], 'parentTestTree': NEGOTIATOR_PARENT_TEST_TREE,
        'changedBodiesAgainstParent': changes, 'preservedPriorBodies': preserved,
        'allOtherHistoricalParentTestBytesReconstructWithLaterProtocolPreimages': True,
        'laterProtocolPreimages': dict(PROTOCOL_PARENT_PINS),
        'helperBlock': {'oldSHA256': sha(old_block.encode()), 'newSHA256': sha(new_block.encode()),
            'oldLines': [parent.count('\n', 0, old_start) + 1, parent.count('\n', 0, old_end)],
            'newLines': [current.count('\n', 0, new_start) + 1, current.count('\n', 0, new_end)],
            'changedBodiesUnchangedSignatures': ['CreateOffer', 'CreatePolicy'],
            'newDeclarations': ['MandatoryCapabilities', 'TestAuthority', 'AssertMandatoryCapabilityCannotBeOmitted', 'CreateExactPolicy'],
            'unchangedRemainder': ['using directives', 'Bindings', 'CaptureSharpLinkException', 'CaptureArgumentException', 'TestCompressionProvider', 'Ensure']}}


def helper_delta(old_text, new_text, old_methods, new_methods):
    # Remove only full catalogued test spans and this sole allowed helper.
    # Every remaining byte in all three parser/handshake files must be exact.
    for path in (PROTOCOL, NEGOTIATOR, COMPATIBILITY):
        before = outside_methods(old_text[path], [m for m in old_methods if m['source'] == path], path == PROTOCOL)
        current = normalized_negotiator_helpers(old_text[path], new_text[path]) if path == NEGOTIATOR else new_text[path]
        after = outside_methods(current, [m for m in new_methods if m['source'] == path], path == PROTOCOL)
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
        'oldCallSites': old_calls, 'newCallSites': new_calls, 'nonMethodBytesReconstructExactlyAfterAllowedHelperChanges': True}


def generate(root):
    root = Path(root)
    for path, expected in (PURE_HISTORY_PINS | PROTOCOL_HISTORY_PINS).items(): require(sha((root / path).read_bytes()) == expected, 'REBIND_PURE_HISTORY')
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
    require(set(altered) == CHANGED_METHODS and len(altered) == 27, 'REBIND_TWENTY_SEVEN_BODIES')
    helper = helper_delta(old_text, new_text, frozen['methods'], result['methods'])
    negotiation = negotiation_delta(root, frozen, result)
    protocol = protocol_delta(root,result)
    historical = {key: result.pop(key) for key in ('sourceCheckpoint', 'sourceTree', 'sourceDirectorySHA256')}
    product_tree = tree_id(root / 'src'); test_tree = tree_id(root / 'test')
    require(product_tree == PARENT_PRODUCT_TREE, 'REBIND_PARENT_PRODUCT_CHANGE')
    result['sourceDerivation'] = {
        'kind': 'protocol70-fixture-source-rebase', 'frozenCatalogSHA256': FROZEN_SHA256,
        'historicalCatalogProvenance': historical, 'parentCommit': PARENT_COMMIT, 'parentTree': PARENT_TREE,
        'frozenProductTree': BASE_PRODUCT_TREE, 'candidateProductTree': product_tree, 'frozenTestTree': BASE_TEST_TREE, 'candidateTestTree': test_tree,
        'candidateSourcePinsSHA256': sha(canonical(result['sourcePins'])),
        'changedSourcePaths': sorted(CANDIDATE_PINS), 'changedMethodBodies': 27,
        'methodCount': 2463, 'sourcePinCount': 1599,
        'qualification': 'Derived source catalog only; not a test execution result.'}
    report = {'schemaVersion': 1, 'scope': 'protocol70-fixture-catalog-rebase',
        'frozenCatalogSHA256': FROZEN_SHA256, 'candidateCatalogSHA256': sha(render(result)),
        'parentCommit': PARENT_COMMIT, 'parentTree': PARENT_TREE,
        'frozenProductTree': BASE_PRODUCT_TREE, 'candidateProductTree': product_tree, 'frozenTestTree': BASE_TEST_TREE, 'candidateTestTree': test_tree,
        'methodCount': 2463, 'sourcePinCount': 1599, 'changedMethodBodyCount': 27,
        'unchangedMethodBodyCount': 2436, 'deletedMethods': 0, 'addedMethods': 0,
        'changedPins': {path: {'old': frozen['sourcePins'][path], 'new': result['sourcePins'][path]} for path in sorted(CANDIDATE_PINS)},
        'affectedMethods': affected, 'changedHelper': helper, 'negotiatorDelta': negotiation, 'protocolDelta': protocol,
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
        'historicalTwentyThreeCaseRun': {'runId': 41959478271, 'head': '8dba79eacbb18744cc6ccc01ad107bb460d574a4',
            'passed': 23, 'qualified': True, 'fullSuiteQualified': False},
        'historicalCurrentFullRun': {'runId': 41962521900, 'head': '72d728bf226ef3d5c9a5ca4d547f798810147880',
            'passed': 2607, 'errors': 272, 'qualified': False,
            'evidenceSHA256': PURE_HISTORY_PINS['eng/pure-negotiation-history/full-ordinary-result.json']},
        'historicalSixteenCaseDiagnostic': {'runId': 41966659630, 'head': 'aecea7afdfdd426a1bbbd5cf840bed945ef62e7b',
            'passed': 7, 'errors': 9, 'infrastructureQualified': True, 'qualified': False,
            'evidenceSHA256': PURE_HISTORY_PINS['eng/pure-negotiation-history/sixteen-case-diagnostic-result.json']},
        'historicalTimedFailureResolution': 'unresolved-no-timed-source-change',
        'historicalLatestFullRun': {'runId':41970862705,'head':'1f5315ca23753258c002ebd6bed294f0226af603',
            'passed':2614,'errors':265,'qualified':False,'evidenceSHA256':PROTOCOL_HISTORY_PINS['eng/protocol70-history/full-ordinary-result.json']},
        'historicalTwentyOneCaseDiagnostic': {'runId':41976164524,'head':'0de69b451c744182f6962d253a1749899fdc29af',
            'passed':17,'errors':4,'infrastructureQualified':True,'qualified':False,
            'evidenceSHA256':PROTOCOL_HISTORY_PINS['eng/protocol70-history/twenty-one-case-diagnostic-result.json']},
        'historicalLifetimeFailuresResolution':'unresolved-despite-isolated-controls-passing',
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
        'methods': 2463, 'pins': 1599, 'changedBodies': 27, 'fullSuiteQualified': False}, sort_keys=True))


if __name__ == '__main__': main()
