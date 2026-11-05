#!/usr/bin/env python3
"""Reproduce the candidate census from a preserved frozen catalog, without .NET.

Offsets in the original catalog are Unicode text offsets; span hashes are UTF-8.
Only three known test bodies may differ. Every other method slice is required to
remain byte-identical, including every shifted method in the two edited files.
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
PARENT_COMMIT = '4209233576c2100e0d5dc625308f49b76d500169'
PARENT_TREE = '013f411efceeba53310ebe0ade5cf525d4b68008'
PROTOCOL = 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs'
PROGRESS = 'test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs'
OWNED_FRAME = 'src/SharpLink.Runtime/OwnedFrame.cs'
CANDIDATE_PINS = {
    OWNED_FRAME: 'c3a6ed62dd0d0738b838c76354b2b730c2808c2db37e5c88c821cb95142d3195',
    PROTOCOL: '736cce73a637487608d83a7db64c678473be51c8e79c978fdec05ddf3b77e384',
    PROGRESS: '70e7f967dbd40b85d41b7df22131fe48fdb39a5e59789a5b5034561e2c1a8a7c',
}
CHANGED_METHODS = {(PROTOCOL, 'WriterAndParserShouldRoundTripFrame'),
    (PROTOCOL, 'HeaderWithEveryByteInSeparateSegmentShouldParse'),
    (PROGRESS, 'ProgressFlagDoesNotGrowOwnedFrame')}


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
        marker = '[Test]\n    ' + p['signature'] + '\n    {'
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


def generate(root):
    root = Path(root)
    frozen_bytes = (root / BASELINE / 'unit-census.json').read_bytes()
    require(sha(frozen_bytes) == FROZEN_SHA256, 'REBIND_FROZEN_HASH')
    frozen = json.loads(frozen_bytes); result = copy.deepcopy(frozen)
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
    require(set(altered) == CHANGED_METHODS and len(altered) == 3, 'REBIND_THREE_BODIES')
    historical = {key: result.pop(key) for key in ('sourceCheckpoint', 'sourceTree', 'sourceDirectorySHA256')}
    product_tree = tree_id(root / 'src'); test_tree = tree_id(root / 'test')
    result['sourceDerivation'] = {
        'kind': 'ownedframe-packing-source-rebase', 'frozenCatalogSHA256': FROZEN_SHA256,
        'historicalCatalogProvenance': historical, 'parentCommit': PARENT_COMMIT, 'parentTree': PARENT_TREE,
        'frozenProductTree': BASE_PRODUCT_TREE, 'candidateProductTree': product_tree, 'frozenTestTree': BASE_TEST_TREE, 'candidateTestTree': test_tree,
        'candidateSourcePinsSHA256': sha(canonical(result['sourcePins'])),
        'changedSourcePaths': sorted(CANDIDATE_PINS), 'changedMethodBodies': 3,
        'methodCount': 2463, 'sourcePinCount': 1599,
        'qualification': 'Derived source catalog only; not a test execution result.'}
    report = {'schemaVersion': 1, 'scope': 'ownedframe-packing-candidate-catalog-rebase',
        'frozenCatalogSHA256': FROZEN_SHA256, 'candidateCatalogSHA256': sha(render(result)),
        'parentCommit': PARENT_COMMIT, 'parentTree': PARENT_TREE,
        'frozenProductTree': BASE_PRODUCT_TREE, 'candidateProductTree': product_tree, 'frozenTestTree': BASE_TEST_TREE, 'candidateTestTree': test_tree,
        'methodCount': 2463, 'sourcePinCount': 1599, 'changedMethodBodyCount': 3,
        'unchangedMethodBodyCount': 2460, 'deletedMethods': 0, 'addedMethods': 0,
        'changedPins': {path: {'old': frozen['sourcePins'][path], 'new': result['sourcePins'][path]} for path in sorted(CANDIDATE_PINS)},
        'affectedMethods': affected,
        'historicalFullOrdinaryRun': {'runId': 41940224644, 'ordinary': 2879, 'passed': 2589, 'errors': 290,
            'failingFiles': 92, 'failingMethodStatusRows': 253, 'qualified': False},
        'historicalFirstMigrationRun': {'runId': 41950562552, 'head': PARENT_COMMIT,
            'complete': True, 'passed': 5, 'nonpassing': 1, 'qualified': False,
            'case4': {'measuredBytes': 64, 'comparatorBytes': 56, 'shapeGuardsPassed': True}},
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
        'methods': 2463, 'pins': 1599, 'changedBodies': 3, 'fullSuiteQualified': False}, sort_keys=True))


if __name__ == '__main__': main()
