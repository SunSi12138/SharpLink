#!/usr/bin/env bash
set -euo pipefail

ROOT="$(pwd)"
BRANCH="agent/issue-252-pending-segments"
git fetch --depth=1 origin dev "$BRANCH"
BASE="$RUNNER_TEMP/issue252-final-base"
CANDIDATE="$RUNNER_TEMP/issue252-final-candidate"
OUT="$RUNNER_TEMP/issue252-final-results"
git worktree add --detach "$BASE" origin/dev
git worktree add --detach "$CANDIDATE" "origin/$BRANCH"
mkdir -p "$OUT"
echo "[issue252-final] base_sha=$(git -C "$BASE" rev-parse HEAD)"
echo "[issue252-final] candidate_sha=$(git -C "$CANDIDATE" rev-parse HEAD)"

# Put the PR's reusable evidence/concurrency sources into both paired worktrees.
for dir in "$BASE" "$CANDIDATE"; do
  cp "$ROOT/test/SharpLink.Benchmarks/PendingRequestSegmentationEvidenceRunner.cs" \
     "$dir/test/SharpLink.Benchmarks/PendingRequestSegmentationEvidenceRunner.cs"
  cp "$ROOT/test/SharpLink.Benchmarks/PendingRequestConcurrencyBenchmarks.cs" \
     "$dir/test/SharpLink.Benchmarks/PendingRequestConcurrencyBenchmarks.cs"
  cat > "$dir/test/SharpLink.Benchmarks/PendingRequestFirstUseEvidenceRunner.cs" <<'CS'
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class PendingRequestFirstUseEvidenceRunner
{
    public static void Run(string[] args)
    {
        var connections = 100;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--connections")
                connections = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);

        using var context = new SharpLinkRuntimeContextBuilder().Build();
        WarmUp(context);
        var tables = new PendingRequestTable[connections];
        for (var i = 0; i < connections; i++)
            tables[i] = CreateTable(context);

        ForceFullGc();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        Span<byte> payloadBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(payloadBytes, 42);
        for (var i = 0; i < tables.Length; i++)
        {
            var operation = tables[i].Rent<int>(out var id);
            var payload = new ReadOnlySequence<byte>(payloadBytes.ToArray());
            if (!tables[i].Dispatch(id, ref payload))
                throw new InvalidOperationException("First-use evidence dispatch failed.");
            _ = operation.AsValueTask().GetAwaiter().GetResult();
        }
        var elapsed = Stopwatch.GetTimestamp() - started;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Console.WriteLine("FIRST_USE_RESULT=" + JsonSerializer.Serialize(new
        {
            connections,
            nanosecondsPerConnection = elapsed * 1_000_000_000d / Stopwatch.Frequency / connections,
            allocatedBytesPerConnection = allocated / (double)connections
        }));

        foreach (var table in tables)
            table.Dispose();
    }

    private static PendingRequestTable CreateTable(SharpLinkRuntimeContext context)
        => new(65_536, context.Codecs, BenchmarkPendingCallOwner.Instance, TimeProvider.System);

    private static void WarmUp(SharpLinkRuntimeContext context)
    {
        using var table = new PendingRequestTable(64, context.Codecs, BenchmarkPendingCallOwner.Instance, TimeProvider.System);
        var operation = table.Rent<int>(out var id);
        var payload = new ReadOnlySequence<byte>(new byte[sizeof(int)]);
        if (!table.Dispatch(id, ref payload))
            throw new InvalidOperationException("First-use evidence warm-up failed.");
        _ = operation.AsValueTask().GetAwaiter().GetResult();
    }

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
CS

  python3 - "$dir/test/SharpLink.Benchmarks/Program.cs" <<'PY'
from pathlib import Path
import sys
p=Path(sys.argv[1]); t=p.read_text()
marker='        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);\n'
if '--pending-request-segmentation-evidence' not in t:
    block='''        if (args.Length > 0 && string.Equals(
            args[0], "--pending-request-segmentation-evidence", StringComparison.Ordinal))
        {
            await PendingRequestSegmentationEvidenceRunner.RunAsync(args[1..]);
            return;
        }
'''
    t=t.replace(marker,block+marker,1)
if '--pending-request-first-use-evidence' not in t:
    block='''        if (args.Length > 0 && string.Equals(
            args[0], "--pending-request-first-use-evidence", StringComparison.Ordinal))
        {
            PendingRequestFirstUseEvidenceRunner.Run(args[1..]);
            return;
        }
'''
    t=t.replace(marker,block+marker,1)
p.write_text(t)
PY
done

# Avoid one allocation per measured first use from payload construction.
python3 - "$BASE/test/SharpLink.Benchmarks/PendingRequestFirstUseEvidenceRunner.cs" \
          "$CANDIDATE/test/SharpLink.Benchmarks/PendingRequestFirstUseEvidenceRunner.cs" <<'PY'
from pathlib import Path
import sys
for name in sys.argv[1:]:
    p=Path(name); t=p.read_text()
    t=t.replace('        Span<byte> payloadBytes = stackalloc byte[sizeof(int)];\n        BinaryPrimitives.WriteInt32LittleEndian(payloadBytes, 42);\n',
                '        var payloadBytes = new byte[sizeof(int)];\n        BinaryPrimitives.WriteInt32LittleEndian(payloadBytes, 42);\n')
    t=t.replace('            var payload = new ReadOnlySequence<byte>(payloadBytes.ToArray());\n',
                '            var payload = new ReadOnlySequence<byte>(payloadBytes);\n')
    p.write_text(t)
PY

dotnet build "$BASE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
dotnet build "$CANDIDATE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal

run_evidence() {
  local dir="$1" variant="$2"; shift 2
  echo "[issue252-final] variant=$variant command=$*"
  (cd "$dir" && dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release --no-build -- "$@")
}

run_evidence "$BASE" eager-dev --pending-request-segmentation-evidence memory --active 0 --connections 100
run_evidence "$CANDIDATE" c2-lazy-flat --pending-request-segmentation-evidence memory --active 0 --connections 100
run_evidence "$BASE" eager-dev --pending-request-segmentation-evidence memory --active 0 --connections 1000
run_evidence "$CANDIDATE" c2-lazy-flat --pending-request-segmentation-evidence memory --active 0 --connections 1000
run_evidence "$BASE" eager-dev --pending-request-segmentation-evidence memory --active 1 --connections 100
run_evidence "$CANDIDATE" c2-lazy-flat --pending-request-segmentation-evidence memory --active 1 --connections 100
run_evidence "$BASE" eager-dev --pending-request-first-use-evidence --connections 100
run_evidence "$CANDIDATE" c2-lazy-flat --pending-request-first-use-evidence --connections 100

run_bench() {
  local dir="$1" variant="$2" round="$3"
  local artifacts="$OUT/${round}-${variant}-concurrency"
  mkdir -p "$artifacts"
  echo "[issue252-final] round=$round variant=$variant benchmark=concurrency"
  (cd "$dir" && dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release --no-build -- \
    --filter '*PendingRequestConcurrencyBenchmarks.RegisterAndCompleteAcrossFourWindows*' --artifacts "$artifacts")
}

run_bench "$BASE" eager-dev 1
run_bench "$CANDIDATE" c2-lazy-flat 1
run_bench "$CANDIDATE" c2-lazy-flat 2
run_bench "$BASE" eager-dev 2
run_bench "$BASE" eager-dev 3
run_bench "$CANDIDATE" c2-lazy-flat 3

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
rounds=[]
for i in (1,2,3):
    br=row(f'{i}-eager-dev-concurrency'); cr=row(f'{i}-c2-lazy-flat-concurrency')
    bn,cn=ns(br['Mean']),ns(cr['Mean'])
    rounds.append({'round':i,'base_ns':bn,'candidate_ns':cn,'delta_percent':(cn/bn-1)*100,
                   'base_allocated_b':b(br.get('Allocated','0')),'candidate_allocated_b':b(cr.get('Allocated','0'))})
deltas=[r['delta_percent'] for r in rounds]
median=statistics.median(deltas); within=sum(d<=3.0 for d in deltas)
passed=median<=3.0 and within>=2
result={'experiment':'C2-final-concurrency','gate_percent':3.0,'rounds':rounds,
        'median_delta_percent':median,'rounds_within_gate':within,'passed':passed}
print('FINAL_CONCURRENCY_RESULT='+json.dumps(result,separators=(',',':')))
if not passed: raise SystemExit('Final C2 concurrency evidence missed the 3% gate')
PY
