from pathlib import Path

source_path = Path('src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs')
text = source_path.read_text()


def replace(old: str, new: str, count: int = 1) -> None:
    global text
    actual = text.count(old)
    if actual < count:
        raise SystemExit(f'missing source pattern (wanted {count}, found {actual}):\n{old}')
    text = text.replace(old, new, count)


replace(
'''        AdmissionPartitionLease? partitionLease = null;
        if (_partitions is not null)
        {
            partitionLease = _partitions.TryAcquire(context);
            if (partitionLease is null)
                return ValueTask.FromResult(AdmissionDecision.Reject("partition_capacity"));
        }

        var request = CreateRequest(context, partitionLease);''',
'''        AdmissionPartitionEntry? partitionEntry = null;
        if (_partitions is not null)
        {
            partitionEntry = _partitions.TryAcquire(context);
            if (partitionEntry is null)
                return ValueTask.FromResult(AdmissionDecision.Reject("partition_capacity"));
        }

        var request = CreateRequest(context, partitionEntry);''')

replace(
'''    private AdmissionRequest CreateRequest(
        SharpLinkAdmissionContext context,
        AdmissionPartitionLease? partitionLease)
    {
        _contracts.TryGetValue(context.ContractId, out var contract);
        _methods.TryGetValue((context.ContractId, context.MethodId), out var method);
        var count = (_global?.SlotCount ?? 0) +
                    (contract?.SlotCount ?? 0) +
                    (method?.SlotCount ?? 0) +
                    (partitionLease?.Runtime.SlotCount ?? 0);
        var slots = new AdmissionLimiterSlot[count];
        count = 0;
        _global?.AppendTo(slots, ref count);
        contract?.AppendTo(slots, ref count);
        method?.AppendTo(slots, ref count);
        partitionLease?.Runtime.AppendTo(slots, ref count);
        return new AdmissionRequest(slots, count, partitionLease);
    }''',
'''    private AdmissionRequest CreateRequest(
        SharpLinkAdmissionContext context,
        AdmissionPartitionEntry? partitionEntry)
    {
        _contracts.TryGetValue(context.ContractId, out var contract);
        _methods.TryGetValue((context.ContractId, context.MethodId), out var method);
        var count = (_global?.SlotCount ?? 0) +
                    (contract?.SlotCount ?? 0) +
                    (method?.SlotCount ?? 0) +
                    (partitionEntry?.Runtime.SlotCount ?? 0);
        var slots = new AdmissionLimiterSlot[count];
        count = 0;
        _global?.AppendTo(slots, ref count);
        contract?.AppendTo(slots, ref count);
        method?.AppendTo(slots, ref count);
        partitionEntry?.Runtime.AppendTo(slots, ref count);
        return new AdmissionRequest(slots, count, partitionEntry);
    }''')

replace('private AdmissionPartitionLease? _partition;', 'private AdmissionPartitionEntry? _partition;')
replace('AdmissionPartitionLease? partition)', 'AdmissionPartitionEntry? partition)', 2)
replace(
    'Interlocked.Exchange(ref _partition, null)?.Dispose();',
    'var partition = Interlocked.Exchange(ref _partition, null);\n        partition?.Owner.Release(partition);')

replace(
'''internal sealed class AdmissionRequest(
    AdmissionLimiterSlot[] slots,
    int slotCount,
    AdmissionPartitionLease? partition) : IDisposable
{
    private AdmissionPartitionLease? _partition = partition;''',
'''internal sealed class AdmissionRequest(
    AdmissionLimiterSlot[] slots,
    int slotCount,
    AdmissionPartitionEntry? partition) : IDisposable
{
    private AdmissionPartitionEntry? _partition = partition;''')
replace(
    'Interlocked.Exchange(ref _partition, null)?.Dispose();',
    'var partition = Interlocked.Exchange(ref _partition, null);\n        partition?.Owner.Release(partition);')

replace(
    'internal AdmissionPartitionLease? TryAcquire(SharpLinkAdmissionContext context)',
    'internal AdmissionPartitionEntry? TryAcquire(SharpLinkAdmissionContext context)')
replace(
    'entry = new AdmissionPartitionEntry(\n                    AdmissionRuleRuntime.Create(_options, _queueLimit, "partition"));',
    'entry = new AdmissionPartitionEntry(\n                    this,\n                    AdmissionRuleRuntime.Create(_options, _queueLimit, "partition"));')
replace('return new AdmissionPartitionLease(this, entry);', 'return entry;')

replace(
'''internal sealed class AdmissionPartitionEntry(AdmissionRuleRuntime runtime)
{
    internal AdmissionRuleRuntime Runtime { get; } = runtime;
    internal int References;
    internal long IdleSince;
    internal bool IsIdle;
}

internal sealed class AdmissionPartitionLease(
    AdmissionPartitionPool owner,
    AdmissionPartitionEntry entry) : IDisposable
{
    private AdmissionPartitionPool? _owner = owner;
    internal AdmissionRuleRuntime Runtime => entry.Runtime;
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(entry);
}''',
'''internal sealed class AdmissionPartitionEntry(
    AdmissionPartitionPool owner,
    AdmissionRuleRuntime runtime)
{
    internal AdmissionPartitionPool Owner { get; } = owner;
    internal AdmissionRuleRuntime Runtime { get; } = runtime;
    internal int References;
    internal long IdleSince;
    internal bool IsIdle;
}''')

if 'AdmissionPartitionLease' in text:
    raise SystemExit('candidate left an AdmissionPartitionLease reference')
source_path.write_text(text)

benchmark_path = Path('test/SharpLink.Benchmarks/AdmissionPartitionBenchmarks.cs')
bench = benchmark_path.read_text()
old = '            _pool.TryAcquire(_context)!.Dispose();'
if old not in bench:
    raise SystemExit('missing benchmark setup pattern')
bench = bench.replace(
    old,
    '            var entry = _pool.TryAcquire(_context)!;\n            _pool.Release(entry);',
    1)
old = '        var lease = _pool.TryAcquire(_context)!;\n        lease.Dispose();'
if old not in bench:
    raise SystemExit('missing benchmark method pattern')
bench = bench.replace(
    old,
    '        var entry = _pool.TryAcquire(_context)!;\n        _pool.Release(entry);',
    1)
benchmark_path.write_text(bench)

Path('test/SharpLink.UnitTests/Server/AdmissionPartitionOwnershipTests.cs').write_text('''using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionPartitionOwnershipTests
{
    [Test]
    public async Task RequestAndLeaseShouldReleasePartitionEntryExactlyOnce()
    {
        var partitionOptions = new SharpLinkPartitionAdmissionOptions
        {
            MaxPartitions = 1,
            IdleTimeout = TimeSpan.FromMinutes(1)
        };
        partitionOptions.UseConcurrency(1);
        using var pool = new AdmissionPartitionPool(
            _ => "hot",
            partitionOptions,
            queueLimit: 0,
            TimeProvider.System);
        var context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "issue-305-test", null, null, null);

        var ownerOptions = new SharpLinkAdmissionControlOptions();
        ownerOptions.Global.UseConcurrency(1);
        await using var owner = SharpLinkAdmissionController.Create(ownerOptions, []);

        var firstEntry = pool.TryAcquire(context)!;
        Ensure(firstEntry.References == 1, "first partition reference acquired");
        var slots = new AdmissionLimiterSlot[firstEntry.Runtime.SlotCount];
        var count = 0;
        firstEntry.Runtime.AppendTo(slots, ref count);
        using var firstRequest = new AdmissionRequest(slots, count, firstEntry);
        Ensure(firstRequest.TryAcquire(owner, out var firstLease, out _),
            "first request should acquire the partition limiter");

        firstRequest.Dispose();
        Ensure(firstEntry.References == 1,
            "successful request transfers rather than releases partition ownership");

        var secondEntry = pool.TryAcquire(context)!;
        Ensure(ReferenceEquals(firstEntry, secondEntry), "same key should reuse the entry");
        Ensure(firstEntry.References == 2, "second request adds one partition reference");
        var secondSlots = new AdmissionLimiterSlot[secondEntry.Runtime.SlotCount];
        count = 0;
        secondEntry.Runtime.AppendTo(secondSlots, ref count);
        using var secondRequest = new AdmissionRequest(secondSlots, count, secondEntry);
        Ensure(!secondRequest.TryAcquire(owner, out _, out _),
            "second request should reject while the first concurrency permit is active");

        secondRequest.Dispose();
        secondRequest.Dispose();
        Ensure(firstEntry.References == 1,
            "rejected request releases its partition reference exactly once");

        firstLease!.Dispose();
        firstLease.Dispose();
        Ensure(firstEntry.References == 0,
            "admitted lease releases its transferred partition reference exactly once");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
''')
