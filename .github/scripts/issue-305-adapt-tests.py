from pathlib import Path

path = Path('test/SharpLink.UnitTests/Server/AdmissionPartitionPoolTests.cs')
text = path.read_text()

# Candidate A2 deliberately removes the per-acquire disposable wrapper from the
# pool API. Direct pool tests therefore release each acquired entry exactly once;
# controller/request/lease idempotence is covered separately by the ownership test.
text = text.replace('AdmissionPartitionLease? replacement = null;',
                    'AdmissionPartitionEntry? replacement = null;')

text = text.replace(
'''                lease!.Dispose();
                if ((iteration & 127) == 0)
                    lease.Dispose();''',
'''                pool.Release(lease!);''')

text = text.replace(
'''                active.Dispose();
                active.Dispose();''',
'''                pool.Release(active);''')

text = text.replace(
'''        pool.Dispose();
        lease.Dispose();
        lease.Dispose();''',
'''        pool.Dispose();
        pool.Release(lease);''')

for old, new in (
    ('pool.TryAcquire(context)!.Dispose();', 'pool.Release(pool.TryAcquire(context)!);'),
    ('pool.TryAcquire(CreateContext("expired"))!.Dispose();',
     'pool.Release(pool.TryAcquire(CreateContext("expired"))!);'),
    ('pool.TryAcquire(contexts[operation % maxPartitions])!.Dispose();',
     'pool.Release(pool.TryAcquire(contexts[operation % maxPartitions])!);'),
    ('lease!.Dispose();', 'pool.Release(lease!);'),
    ('reacquired!.Dispose();', 'pool.Release(reacquired!);'),
    ('first!.Dispose();', 'pool.Release(first!);'),
    ('first.Dispose();', 'pool.Release(first);'),
    ('second!.Dispose();', 'pool.Release(second!);'),
    ('active.Dispose();', 'pool.Release(active);'),
    ('replacement!.Dispose();', 'pool.Release(replacement!);'),
    ('replacement?.Dispose();',
     'if (replacement is not null)\n                pool.Release(replacement);'),
):
    text = text.replace(old, new)

if 'AdmissionPartitionLease' in text:
    raise SystemExit('partition pool tests still reference AdmissionPartitionLease')

for stale in (
    'lease.Dispose();',
    'lease!.Dispose();',
    'first.Dispose();',
    'first!.Dispose();',
    'second!.Dispose();',
    'active.Dispose();',
    'replacement!.Dispose();',
    'reacquired!.Dispose();',
):
    if stale in text:
        raise SystemExit(f'partition pool tests still contain stale per-acquire Dispose: {stale}')

path.write_text(text)
