#!/usr/bin/env bash
set -euo pipefail

git fetch --depth=1 origin dev
BASE="$RUNNER_TEMP/issue252-c1-base"
CANDIDATE="$RUNNER_TEMP/issue252-c1-candidate"
OUT="$RUNNER_TEMP/issue252-c1-results"
git worktree add --detach "$BASE" origin/dev
git worktree add --detach "$CANDIDATE" origin/dev
mkdir -p "$OUT"
echo "[issue252-C1] base_sha=$(git -C "$BASE" rev-parse HEAD)"

python3 - "$CANDIDATE" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1]) / "src/SharpLink.Client/PendingRequestTable.cs"
text = path.read_text()

def one(old, new):
    global text
    n = text.count(old)
    if n != 1:
        raise SystemExit(f"expected 1 match, got {n}: {old[:90]!r}")
    text = text.replace(old, new, 1)

def many(old, new, expected):
    global text
    n = text.count(old)
    if n != expected:
        raise SystemExit(f"expected {expected} matches, got {n}: {old[:90]!r}")
    text = text.replace(old, new)

one("    private readonly PendingCall?[] _slots;\n",
    "    private readonly int _capacity;\n    private PendingCall?[]? _slots;\n")
one("        _slots = new PendingCall?[capacity];\n        _indexMask = capacity - 1;\n",
    "        _capacity = capacity;\n        _indexMask = capacity - 1;\n")
one("    public int Capacity => _slots.Length;\n", "    public int Capacity => _capacity;\n")
one("        {\n            var count = 0;\n            for (var index = 0; index < _slots.Length; index++)\n                if (Volatile.Read(ref _slots[index]) is not null)\n                    count++;\n            return count;\n        }\n",
    "        {\n            var slots = Volatile.Read(ref _slots);\n            if (slots is null)\n                return 0;\n\n            var count = 0;\n            for (var index = 0; index < slots.Length; index++)\n                if (Volatile.Read(ref slots[index]) is not null)\n                    count++;\n            return count;\n        }\n")
one("    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)\n    {\n        var index = (int)(id & _indexMask);\n        var current = Volatile.Read(ref _slots[index]);\n",
    "    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return false;\n\n        var index = (int)(id & _indexMask);\n        var current = Volatile.Read(ref slots[index]);\n")
one("                if (!ReferenceEquals(Volatile.Read(ref _slots[index]), current) ||\n                    current.Id != id ||\n",
    "                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) ||\n                    current.Id != id ||\n")
one("    public bool Contains(long id)\n    {\n        var call = Volatile.Read(ref _slots[(int)(id & _indexMask)]);\n        return call is not null && call.Id == id;\n    }\n",
    "    public bool Contains(long id)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return false;\n\n        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);\n        return call is not null && call.Id == id;\n    }\n")
one("    public CancellationToken GetProducerCancellationToken(long id)\n    {\n        var call = Volatile.Read(ref _slots[(int)(id & _indexMask)]);\n        if (call is null || call.Id != id)\n            return new CancellationToken(canceled: true);\n        return call.ProducerCancellationToken;\n    }\n",
    "    public CancellationToken GetProducerCancellationToken(long id)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return new CancellationToken(canceled: true);\n\n        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);\n        if (call is null || call.Id != id)\n            return new CancellationToken(canceled: true);\n        return call.ProducerCancellationToken;\n    }\n")
one("    public void FailAllPendingRequests(Exception exception)\n    {\n        ArgumentNullException.ThrowIfNull(exception);\n        for (var index = 0; index < _slots.Length; index++)\n",
    "    public void FailAllPendingRequests(Exception exception)\n    {\n        ArgumentNullException.ThrowIfNull(exception);\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return;\n\n        for (var index = 0; index < slots.Length; index++)\n")
many("        var published = false;\n        try\n        {\n            while (true)\n            {\n                for (var attempt = 0; attempt < _slots.Length; attempt++)\n",
     "        var published = false;\n        try\n        {\n            var slots = GetOrCreateSlots();\n            while (true)\n            {\n                for (var attempt = 0; attempt < slots.Length; attempt++)\n", 2)
many("                    if (Volatile.Read(ref _slots[index]) is not null)\n",
     "                    if (Volatile.Read(ref slots[index]) is not null)\n", 2)
many("                    if (Interlocked.CompareExchange(ref _slots[index], call, null) is null)\n",
     "                    if (Interlocked.CompareExchange(ref slots[index], call, null) is null)\n", 2)
one("        if (active <= _slots.Length)\n", "        if (active <= _capacity)\n")
one("        if (remaining < _slots.Length && Volatile.Read(ref _waiterCount) != 0)\n",
    "        if (remaining < _capacity && Volatile.Read(ref _waiterCount) != 0)\n")
one("    private bool TryTakeMatchingCall(long id, out PendingCall? call)\n    {\n        var index = (int)(id & _indexMask);\n",
    "    private bool TryTakeMatchingCall(long id, out PendingCall? call)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n        {\n            call = null;\n            return false;\n        }\n\n        var index = (int)(id & _indexMask);\n")
one("    private bool TryTakeCallAtIndex(int index, out PendingCall? call)\n    {\n        while (true)\n",
    "    private bool TryTakeCallAtIndex(int index, out PendingCall? call)\n    {\n        var slots = Volatile.Read(ref _slots)!;\n        while (true)\n")
one("            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);\n            var now = _timeProvider.GetTimestamp();\n            for (var index = 0; index < _slots.Length; index++)\n",
    "            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);\n            var slots = Volatile.Read(ref _slots);\n            if (slots is null)\n                return;\n\n            var now = _timeProvider.GetTimestamp();\n            for (var index = 0; index < slots.Length; index++)\n")
many("ref _slots[index]", "ref slots[index]", 7)
marker = "    private bool TryAcquireCapacity()\n"
helper = """    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PendingCall?[] GetOrCreateSlots()
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is not null)
            return slots;

        var created = new PendingCall?[_capacity];
        return Interlocked.CompareExchange(ref _slots, created, null) ?? created;
    }

"""
one(marker, helper + marker)
if "_slots.Length" in text or "ref _slots[index]" in text:
    raise SystemExit("C1 patch left direct lazy-slot accesses behind")
path.write_text(text)
PY

git -C "$CANDIDATE" diff --check

for dir in "$BASE" "$CANDIDATE"; do
  cat > "$dir/test/SharpLink.Benchmarks/PendingRequestConstructionBenchmarks.cs" <<'CS'
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class PendingRequestConstructionBenchmarks
{
    private SharpLinkRuntimeContext _context = null!;
    [GlobalSetup] public void Setup() => _context = new SharpLinkRuntimeContextBuilder().Build();
    [GlobalCleanup] public void Cleanup() => _context.Dispose();
    [Benchmark]
    public int ConstructAndDispose()
    {
        using var table = new PendingRequestTable(65_536, _context.Codecs, BenchmarkPendingCallOwner.Instance, TimeProvider.System);
        return table.Capacity;
    }
}
CS
done

dotnet restore "$CANDIDATE/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj"
dotnet build "$CANDIDATE/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj" -c Release --no-restore -v minimal
dotnet test --project "$CANDIDATE/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj" -c Release --no-build
dotnet build "$BASE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
dotnet build "$CANDIDATE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal

run_bench() {
  local dir="$1" variant="$2" round="$3" filter="$4" key="$5"
  local artifacts="$OUT/${round}-${variant}-${key}"
  echo "[issue252-C1] round=$round variant=$variant benchmark=$key"
  mkdir -p "$artifacts"
  (cd "$dir" && dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release --no-build -- --filter "$filter" --artifacts "$artifacts")
}

run_bench "$BASE" eager-dev memory '*PendingRequestConstructionBenchmarks.ConstructAndDispose*' construction
run_bench "$CANDIDATE" c1-lazy-flat memory '*PendingRequestConstructionBenchmarks.ConstructAndDispose*' construction
run_bench "$BASE" eager-dev 1 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single
run_bench "$CANDIDATE" c1-lazy-flat 1 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single
run_bench "$CANDIDATE" c1-lazy-flat 2 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single
run_bench "$BASE" eager-dev 2 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single
run_bench "$BASE" eager-dev 3 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single
run_bench "$CANDIDATE" c1-lazy-flat 3 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single

python3 - "$OUT" <<'PY'
import csv, glob, json, statistics, sys
from pathlib import Path
root = Path(sys.argv[1])
def num(v):
    v=v.strip()
    if v in ('','-','NA','N/A'): return 0.0,''
    p=v.replace(',','').split(); return float(p[0]), p[1] if len(p)>1 else ''
def ns(v):
    n,u=num(v); return n*{'ns':1.0,'us':1e3,'µs':1e3,'ms':1e6,'s':1e9}[u]
def b(v):
    n,u=num(v); return n*{'':1.0,'B':1.0,'KB':1024.0,'MB':1048576.0,'GB':1073741824.0}[u]
def row(pattern):
    files=glob.glob(str(root/pattern/'results'/'*-report.csv'))
    if len(files)!=1: raise SystemExit(f'expected one CSV for {pattern}: {files}')
    with open(files[0],newline='') as f: rows=list(csv.DictReader(f))
    if len(rows)!=1: raise SystemExit(f'expected one row for {pattern}: {len(rows)}')
    return rows[0]
bm=b(row('memory-eager-dev-construction').get('Allocated','0'))
cm=b(row('memory-c1-lazy-flat-construction').get('Allocated','0'))
rounds=[]
for i in (1,2,3):
    br=row(f'{i}-eager-dev-single'); cr=row(f'{i}-c1-lazy-flat-single')
    bn,cn=ns(br['Mean']),ns(cr['Mean'])
    rounds.append({'round':i,'base_single_ns':bn,'candidate_single_ns':cn,'single_delta_percent':(cn/bn-1)*100,'base_allocated_b_per_op':b(br.get('Allocated','0')),'candidate_allocated_b_per_op':b(cr.get('Allocated','0'))})
deltas=[r['single_delta_percent'] for r in rounds]
med=statistics.median(deltas); within=sum(d<=3.0 for d in deltas)
alloc_ok=all(r['candidate_allocated_b_per_op']<=r['base_allocated_b_per_op'] for r in rounds)
ratio=cm/bm if bm else 1.0
passed=med<=3.0 and within>=2 and alloc_ok and ratio<=0.10
result={'experiment':'C1-lazy-flat-first-touch','gate_percent':3.0,'base_construction_allocated_b':bm,'candidate_construction_allocated_b':cm,'construction_allocation_reduction_percent':(1-ratio)*100,'rounds':rounds,'median_single_delta_percent':med,'single_rounds_within_gate':within,'hot_path_allocation_non_regression':alloc_ok,'passed':passed}
print('EXPERIMENT_C1_RESULT='+json.dumps(result,separators=(',',':')))
if not passed: raise SystemExit('Experiment C1 missed the viability gate')
PY
