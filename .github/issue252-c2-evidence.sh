#!/usr/bin/env bash
set -euo pipefail

ROOT="$(pwd)"
git fetch --depth=1 origin dev
BASE="$RUNNER_TEMP/issue252-c2-base"
CANDIDATE="$RUNNER_TEMP/issue252-c2-candidate"
OUT="$RUNNER_TEMP/issue252-c2-results"
git worktree add --detach "$BASE" origin/dev
git worktree add --detach "$CANDIDATE" origin/dev
mkdir -p "$OUT"
echo "[issue252-C2] base_sha=$(git -C "$BASE" rev-parse HEAD)"

python3 - "$CANDIDATE" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1]) / "src/SharpLink.Client/PendingRequestTable.cs"
text = path.read_text()

def one(old, new):
    global text
    n = text.count(old)
    if n != 1:
        raise SystemExit(f"expected 1 match, got {n}: {old[:100]!r}")
    text = text.replace(old, new, 1)

def many(old, new, expected):
    global text
    n = text.count(old)
    if n != expected:
        raise SystemExit(f"expected {expected} matches, got {n}: {old[:100]!r}")
    text = text.replace(old, new)

# C1: defer the large flat slot array until first registration. Keep a one-time
# initialization gate so concurrent first touch cannot allocate several 512 KiB losers.
one(
    "    private readonly int _indexMask;\n    private readonly PendingCall?[] _slots;\n",
    "    private const int DeadlinePageShift = 8;\n"
    "    private const int DeadlinePageSize = 1 << DeadlinePageShift;\n"
    "    private readonly int _indexMask;\n"
    "    private readonly int _capacity;\n"
    "    private readonly object _slotsInitializationGate = new();\n"
    "    private readonly int[] _deadlinePageCounts;\n"
    "    private PendingCall?[]? _slots;\n")
one(
    "        _slots = new PendingCall?[capacity];\n        _indexMask = capacity - 1;\n",
    "        _capacity = capacity;\n"
    "        _deadlinePageCounts = new int[(capacity + DeadlinePageSize - 1) >> DeadlinePageShift];\n"
    "        _indexMask = capacity - 1;\n")
one("    public int Capacity => _slots.Length;\n", "    public int Capacity => _capacity;\n")
one(
    "    internal int ActiveCount => Volatile.Read(ref _activeSlots);\n",
    "    internal int ActiveCount => Volatile.Read(ref _activeSlots);\n\n"
    "    internal int LastDeadlineScanInspectedSlots { get; private set; }\n")
one(
    "        {\n            var count = 0;\n            for (var index = 0; index < _slots.Length; index++)\n                if (Volatile.Read(ref _slots[index]) is not null)\n                    count++;\n            return count;\n        }\n",
    "        {\n            var slots = Volatile.Read(ref _slots);\n            if (slots is null)\n                return 0;\n\n            var count = 0;\n            for (var index = 0; index < slots.Length; index++)\n                if (Volatile.Read(ref slots[index]) is not null)\n                    count++;\n            return count;\n        }\n")
one(
    "    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)\n    {\n        var index = (int)(id & _indexMask);\n        var current = Volatile.Read(ref _slots[index]);\n",
    "    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return false;\n\n        var index = (int)(id & _indexMask);\n        var current = Volatile.Read(ref slots[index]);\n")
one(
    "                if (!ReferenceEquals(Volatile.Read(ref _slots[index]), current) ||\n                    current.Id != id ||\n",
    "                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) ||\n                    current.Id != id ||\n")
one(
    "    public bool Contains(long id)\n    {\n        var call = Volatile.Read(ref _slots[(int)(id & _indexMask)]);\n        return call is not null && call.Id == id;\n    }\n",
    "    public bool Contains(long id)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return false;\n\n        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);\n        return call is not null && call.Id == id;\n    }\n")
one(
    "    public CancellationToken GetProducerCancellationToken(long id)\n    {\n        var call = Volatile.Read(ref _slots[(int)(id & _indexMask)]);\n        if (call is null || call.Id != id)\n            return new CancellationToken(canceled: true);\n        return call.ProducerCancellationToken;\n    }\n",
    "    public CancellationToken GetProducerCancellationToken(long id)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return new CancellationToken(canceled: true);\n\n        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);\n        if (call is null || call.Id != id)\n            return new CancellationToken(canceled: true);\n        return call.ProducerCancellationToken;\n    }\n")
one(
    "    public void FailAllPendingRequests(Exception exception)\n    {\n        ArgumentNullException.ThrowIfNull(exception);\n        for (var index = 0; index < _slots.Length; index++)\n",
    "    public void FailAllPendingRequests(Exception exception)\n    {\n        ArgumentNullException.ThrowIfNull(exception);\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n            return;\n\n        for (var index = 0; index < slots.Length; index++)\n")
many(
    "        var published = false;\n        try\n        {\n            while (true)\n            {\n                for (var attempt = 0; attempt < _slots.Length; attempt++)\n",
    "        var published = false;\n        try\n        {\n            var slots = GetOrCreateSlots();\n            while (true)\n            {\n                for (var attempt = 0; attempt < slots.Length; attempt++)\n",
    2)
many(
    "                    if (Volatile.Read(ref _slots[index]) is not null)\n",
    "                    if (Volatile.Read(ref slots[index]) is not null)\n",
    2)
many(
    "                    if (Interlocked.CompareExchange(ref _slots[index], call, null) is null)\n",
    "                    if (deadline.HasValue)\n"
    "                        Interlocked.Increment(ref _deadlinePageCounts[index >> DeadlinePageShift]);\n"
    "                    if (Interlocked.CompareExchange(ref slots[index], call, null) is null)\n",
    2)
many(
    "\n                    call.ReturnUnused();\n",
    "\n                    if (deadline.HasValue)\n"
    "                        Interlocked.Decrement(ref _deadlinePageCounts[index >> DeadlinePageShift]);\n"
    "                    call.ReturnUnused();\n",
    2)
one("        if (active <= _slots.Length)\n", "        if (active <= _capacity)\n")
one(
    "        if (remaining < _slots.Length && Volatile.Read(ref _waiterCount) != 0)\n",
    "        if (remaining < _capacity && Volatile.Read(ref _waiterCount) != 0)\n")
one(
    "    private bool TryTakeMatchingCall(long id, out PendingCall? call)\n    {\n        var index = (int)(id & _indexMask);\n",
    "    private bool TryTakeMatchingCall(long id, out PendingCall? call)\n    {\n        var slots = Volatile.Read(ref _slots);\n        if (slots is null)\n        {\n            call = null;\n            return false;\n        }\n\n        var index = (int)(id & _indexMask);\n")
one(
    "                var exchanged = Interlocked.CompareExchange(ref _slots[index], null, current);\n                if (!ReferenceEquals(exchanged, current))\n                    continue;\n\n                current.WaitUntilRegistered();\n",
    "                var exchanged = Interlocked.CompareExchange(ref slots[index], null, current);\n                if (!ReferenceEquals(exchanged, current))\n                    continue;\n\n                if (current.Deadline.HasValue)\n                    Interlocked.Decrement(ref _deadlinePageCounts[index >> DeadlinePageShift]);\n                current.WaitUntilRegistered();\n")
one(
    "    private bool TryTakeCallAtIndex(int index, out PendingCall? call)\n    {\n        while (true)\n",
    "    private bool TryTakeCallAtIndex(int index, out PendingCall? call)\n    {\n        var slots = Volatile.Read(ref _slots)!;\n        while (true)\n")
one(
    "                if (!ReferenceEquals(Volatile.Read(ref _slots[index]), current))\n                    continue;\n\n                if (!ReferenceEquals(Interlocked.CompareExchange(ref _slots[index], null, current), current))\n                    continue;\n\n                current.WaitUntilRegistered();\n",
    "                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current))\n                    continue;\n\n                if (!ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, current), current))\n                    continue;\n\n                if (current.Deadline.HasValue)\n                    Interlocked.Decrement(ref _deadlinePageCounts[index >> DeadlinePageShift]);\n                current.WaitUntilRegistered();\n")

marker = "    private bool TryAcquireCapacity()\n"
helper = """    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PendingCall?[] GetOrCreateSlots()
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is not null)
            return slots;

        lock (_slotsInitializationGate)
        {
            slots = Volatile.Read(ref _slots);
            if (slots is null)
            {
                slots = new PendingCall?[_capacity];
                Volatile.Write(ref _slots, slots);
            }

            return slots;
        }
    }

"""
one(marker, helper + marker)

# C2: deadline scans stay independent from the flat storage representation. Only
# pages that currently own deadline-bearing calls are scanned.
one(
    "            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);\n"
    "            var now = _timeProvider.GetTimestamp();\n"
    "            for (var index = 0; index < _slots.Length; index++)\n"
    "            {\n"
    "                var call = Volatile.Read(ref _slots[index]);\n"
    "                if (call is null || !call.Deadline.HasValue)\n"
    "                    continue;\n"
    "                if (call.Deadline.Timestamp <= now)\n"
    "                    TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);\n"
    "                else\n"
    "                    UpdateEarliestDeadline(call.Deadline.Timestamp);\n"
    "            }\n",
    "            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);\n"
    "            var slots = Volatile.Read(ref _slots);\n"
    "            if (slots is null)\n"
    "            {\n"
    "                LastDeadlineScanInspectedSlots = 0;\n"
    "                return;\n"
    "            }\n\n"
    "            var now = _timeProvider.GetTimestamp();\n"
    "            var inspectedSlots = 0;\n"
    "            for (var page = 0; page < _deadlinePageCounts.Length; page++)\n"
    "            {\n"
    "                if (Volatile.Read(ref _deadlinePageCounts[page]) == 0)\n"
    "                    continue;\n\n"
    "                var start = page << DeadlinePageShift;\n"
    "                var end = Math.Min(start + DeadlinePageSize, slots.Length);\n"
    "                for (var index = start; index < end; index++)\n"
    "                {\n"
    "                    inspectedSlots++;\n"
    "                    var call = Volatile.Read(ref slots[index]);\n"
    "                    if (call is null || !call.Deadline.HasValue)\n"
    "                        continue;\n"
    "                    if (call.Deadline.Timestamp <= now)\n"
    "                        TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);\n"
    "                    else\n"
    "                        UpdateEarliestDeadline(call.Deadline.Timestamp);\n"
    "                }\n"
    "            }\n\n"
    "            LastDeadlineScanInspectedSlots = inspectedSlots;\n")

if "_slots.Length" in text or "ref _slots[index]" in text:
    raise SystemExit("C2 patch left direct lazy-slot accesses behind")
path.write_text(text)
PY

git -C "$CANDIDATE" diff --check

# Reuse the PR's evidence runner in both clean dev worktrees. Add only its command
# dispatch to each dev Program.cs so the storage comparison remains otherwise paired.
for dir in "$BASE" "$CANDIDATE"; do
  cp "$ROOT/test/SharpLink.Benchmarks/PendingRequestSegmentationEvidenceRunner.cs" \
     "$dir/test/SharpLink.Benchmarks/PendingRequestSegmentationEvidenceRunner.cs"
  python3 - "$dir/test/SharpLink.Benchmarks/Program.cs" <<'PY'
from pathlib import Path
import sys
p=Path(sys.argv[1]); t=p.read_text()
needle="        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);\n"
insert="""        if (args.Length > 0 && string.Equals(
            args[0], "--pending-request-segmentation-evidence", StringComparison.Ordinal))
        {
            await PendingRequestSegmentationEvidenceRunner.RunAsync(args[1..]);
            return;
        }
"""
if t.count(needle)!=1: raise SystemExit('Program benchmark switch marker mismatch')
t=t.replace(needle,insert+needle,1)
p.write_text(t)
PY
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

# Candidate correctness first.
dotnet restore "$CANDIDATE/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj"
dotnet build "$CANDIDATE/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj" -c Release --no-restore -v minimal
dotnet test --project "$CANDIDATE/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj" -c Release --no-build

# Build benchmark executables once; subsequent BDN invocations still create their isolated jobs.
dotnet build "$BASE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
dotnet build "$CANDIDATE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal

run_bench() {
  local dir="$1" variant="$2" round="$3" filter="$4" key="$5"
  local artifacts="$OUT/${round}-${variant}-${key}"
  echo "[issue252-C2] round=$round variant=$variant benchmark=$key"
  mkdir -p "$artifacts"
  (cd "$dir" && dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
      -c Release --no-build -- --filter "$filter" --artifacts "$artifacts")
}

run_scan() {
  local dir="$1" variant="$2"
  local log="$OUT/${variant}-scan.log"
  echo "[issue252-C2] variant=$variant benchmark=sparse-scan"
  (cd "$dir" && dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
      -c Release --no-build -- \
      --pending-request-segmentation-evidence scan --active 8 --deadlines 2 --iterations 10000) | tee "$log"
}

run_bench "$BASE" eager-dev memory '*PendingRequestConstructionBenchmarks.ConstructAndDispose*' construction
run_bench "$CANDIDATE" c2-lazy-flat-pages memory '*PendingRequestConstructionBenchmarks.ConstructAndDispose*' construction
run_scan "$BASE" eager-dev
run_scan "$CANDIDATE" c2-lazy-flat-pages

# Alternate order to reduce runner drift for the normal-path CPU gate.
run_bench "$BASE" eager-dev 1 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single
run_bench "$CANDIDATE" c2-lazy-flat-pages 1 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single
run_bench "$CANDIDATE" c2-lazy-flat-pages 2 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single
run_bench "$BASE" eager-dev 2 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single
run_bench "$BASE" eager-dev 3 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single
run_bench "$CANDIDATE" c2-lazy-flat-pages 3 '*RuntimeHotPathBenchmarks.PendingRegisterAndComplete*' single

python3 - "$OUT" <<'PY'
import csv, glob, json, statistics, sys
from pathlib import Path
root=Path(sys.argv[1])

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
def scan(name):
    lines=(root/f'{name}-scan.log').read_text().splitlines()
    matches=[]
    for line in lines:
        line=line.strip()
        if line.startswith('{') and '"mode":"scan"' in line:
            try: matches.append(json.loads(line))
            except json.JSONDecodeError: pass
    if len(matches)!=1: raise SystemExit(f'expected one scan JSON for {name}, got {len(matches)}')
    return matches[0]

bm=b(row('memory-eager-dev-construction').get('Allocated','0'))
cm=b(row('memory-c2-lazy-flat-pages-construction').get('Allocated','0'))
base_scan=scan('eager-dev'); candidate_scan=scan('c2-lazy-flat-pages')
rounds=[]
for i in (1,2,3):
    br=row(f'{i}-eager-dev-single'); cr=row(f'{i}-c2-lazy-flat-pages-single')
    bn,cn=ns(br['Mean']),ns(cr['Mean'])
    rounds.append({'round':i,'base_single_ns':bn,'candidate_single_ns':cn,
                   'single_delta_percent':(cn/bn-1)*100,
                   'base_allocated_b_per_op':b(br.get('Allocated','0')),
                   'candidate_allocated_b_per_op':b(cr.get('Allocated','0'))})
deltas=[r['single_delta_percent'] for r in rounds]
med=statistics.median(deltas); within=sum(d<=3.0 for d in deltas)
alloc_ok=all(r['candidate_allocated_b_per_op']<=r['base_allocated_b_per_op'] for r in rounds)
ratio=cm/bm if bm else 1.0
scan_width_ok=candidate_scan['inspectedSlots']<=256 and base_scan['inspectedSlots']==65536
scan_cpu_delta=(candidate_scan['nanosecondsPerScan']/base_scan['nanosecondsPerScan']-1)*100
passed=(med<=3.0 and within>=2 and alloc_ok and ratio<=0.10 and scan_width_ok and scan_cpu_delta<0.0)
result={
    'experiment':'C2-lazy-flat-deadline-pages',
    'gate_percent':3.0,
    'base_construction_allocated_b':bm,
    'candidate_construction_allocated_b':cm,
    'construction_allocation_reduction_percent':(1-ratio)*100,
    'base_scan_inspected_slots':base_scan['inspectedSlots'],
    'candidate_scan_inspected_slots':candidate_scan['inspectedSlots'],
    'base_scan_ns':base_scan['nanosecondsPerScan'],
    'candidate_scan_ns':candidate_scan['nanosecondsPerScan'],
    'scan_cpu_delta_percent':scan_cpu_delta,
    'rounds':rounds,
    'median_single_delta_percent':med,
    'single_rounds_within_gate':within,
    'hot_path_allocation_non_regression':alloc_ok,
    'passed':passed,
}
print('EXPERIMENT_C2_RESULT='+json.dumps(result,separators=(',',':')))
if not passed: raise SystemExit('Experiment C2 missed the viability gate')
PY
