"""The frozen dev tests are synchronous zero-allocation tests, not #931 children."""
import hashlib
from validate import require

SOURCE_PATH = 'test/SharpLink.UnitTests/Runtime/ReadOwnershipPipeReaderLifetimeTests.cs'
SOURCE_SHA256 = 'c538ee7fb5ca4a2325b5062bd867382bc05c73526b54f7a638ae15ef0d07fa21'
METHODS = ('SuccessfulSuspensionsShouldNotAllocateAfterWarmup',
           'RegisteredConsumerSuspensionsShouldNotAllocateAfterWarmup')
UIDS = tuple('SharpLink.UnitTests.Runtime.ReadOwnershipPipeReaderLifetimeTests.1.1.' + method + '.1.1.0'
             for method in METHODS)


def verify_source(root):
    source = root / SOURCE_PATH
    require(source.resolve(strict=True) == source and not source.is_symlink(), 'allocation source alias')
    raw = source.read_bytes()
    require(hashlib.sha256(raw).hexdigest() == SOURCE_SHA256, 'allocation source bytes differ from dev')
    text = raw.decode('utf-8-sig')
    require(all('[Test]\n    [NotInParallel]\n    public void ' + method + '()' in text for method in METHODS),
            'allocation tests must be original synchronous dev methods')
    require(text.count('Ensure(allocated == 0,') == 2 and
            text.count('Ensure(gen2After == gen2Before,') == 2 and
            'Ensure(consumed == 11_000,' in text, 'original zero allocation/Gen2/consumer assertions changed')
    require('READ_OWNERSHIP_ALLOCATION_VERIFIED' not in text and 'ReadOwnershipAllocationProbe.RunAsync' not in text,
            'wrong #931 child-probe fixture')


def verify_discovery(discovery):
    tests = {t['uid']: t for t in discovery['tests']}
    for uid, method in zip(UIDS, METHODS):
        require(uid in tests, 'missing original synchronous allocation UID')
        test = tests[uid]
        require(test.get('type', {}).get('namespace') == 'SharpLink.UnitTests.Runtime'
                and test.get('type', {}).get('typeName') == 'ReadOwnershipPipeReaderLifetimeTests'
                and test.get('type', {}).get('methodName') == method
                and test.get('location', {}).get('file', '').replace('\\', '/').endswith('/' + SOURCE_PATH),
                'allocation discovery source identity differs')


def verify_result(ordinary, report):
    require(set(UIDS).issubset(ordinary), 'original allocation UIDs not in ordinary corpus')
    selected = [t for g in report['groups'] for t in g['tests'] if t.get('id') in UIDS]
    require(len(selected) == 2 and {t.get('id') for t in selected} == set(UIDS)
            and all(t.get('status') == 'passed' for t in selected), 'original allocation tests did not both pass')
