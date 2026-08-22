#!/usr/bin/env bash
set -euo pipefail

ROOT="$GITHUB_WORKSPACE"
BASE="$RUNNER_TEMP/issue252-ring-base"
OUT="$RUNNER_TEMP/issue252-ring-results"
DEV_SHA="9b6f627954ec5a0eaca31b4cea5accdd4a6d79c9"
SIZES=(64 256 1024)

rm -rf "$BASE" "$OUT"
mkdir -p "$OUT"
git fetch --no-tags origin dev agent/issue-252-sparse-ring-evidence
git worktree add --detach "$BASE" "$DEV_SHA"
CANDIDATES=()
cleanup() {
  git -C "$ROOT" worktree remove --force "$BASE" >/dev/null 2>&1 || true
  for dir in "${CANDIDATES[@]:-}"; do
    git -C "$ROOT" worktree remove --force "$dir" >/dev/null 2>&1 || true
  done
}
trap cleanup EXIT

cp "$ROOT/test/SharpLink.Benchmarks/PendingRequestSparseRingBenchmarks.cs" \
   "$BASE/test/SharpLink.Benchmarks/PendingRequestSparseRingBenchmarks.cs"

patch_candidate() {
  local dir="$1"
  local size="$2"
  python3 - "$dir/src/SharpLink.Client/PendingRequestTable.cs" "$size" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
size = int(sys.argv[2])
text = path.read_text()

def one(old: str, new: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'expected one match, got {count}: {old[:100]!r}')
    text = text.replace(old, new, 1)

one(
'''internal sealed class PendingRequestTable : IDisposable
{
    private readonly int _indexMask;
    private readonly int _capacity;
    private readonly object _slotsInitializationGate = new();
    private PendingCall?[]? _slots;
''',
 f'''internal sealed class PendingRequestTable : IDisposable
{{
    private const int SparseSlotCount = {size};
    private readonly int _indexMask;
    private readonly int _capacity;
    private readonly object _slotsInitializationGate = new();
    private PendingCall?[]? _slots;
    private PendingCall?[]? _sparseSlots;
''')

one(
'    internal bool SlotsMaterialized => Volatile.Read(ref _slots) is not null;\n',
'''    internal bool SlotsMaterialized
        => Volatile.Read(ref _slots) is not null || Volatile.Read(ref _sparseSlots) is not null;
''')

one(
'''    public int Count
    {
        get
        {
            var slots = Volatile.Read(ref _slots);
            if (slots is null)
                return 0;

            var count = 0;
            for (var index = 0; index < slots.Length; index++)
                if (Volatile.Read(ref slots[index]) is not null)
                    count++;
            return count;
        }
    }
''',
'''    public int Count
    {
        get
        {
            var count = 0;
            var sparseSlots = Volatile.Read(ref _sparseSlots);
            if (sparseSlots is not null)
            {
                for (var index = 0; index < sparseSlots.Length; index++)
                    if (Volatile.Read(ref sparseSlots[index]) is not null)
                        count++;
            }

            var slots = Volatile.Read(ref _slots);
            if (slots is not null)
            {
                for (var index = 0; index < slots.Length; index++)
                    if (Volatile.Read(ref slots[index]) is not null)
                        count++;
            }
            return count;
        }
    }
''')

one(
'''    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return false;

        var index = (int)(id & _indexMask);
        var current = Volatile.Read(ref slots[index]);
        if (current is not null && current.Id == id &&
            current.Kind is PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming)
        {
            // A successful Response is only the server's acknowledgement; StreamComplete owns
            // the terminal transition for server and duplex streams. The callback shares the
            // per-call completion gate with terminal removal, so a matching acknowledgement is
            // observed before cancellation, deadline, or disconnect can report the terminal result.
            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) ||
                    current.Id != id ||
                    current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
                {
                    return false;
                }

                current.CompletionObserver?.OnResponseObserved();
                return true;
            }
        }

        if (!TryTakeMatchingCall(id, out var call))
            return false;

        CompleteTakenCall(call!, PendingCallCompletionReason.Response, exception: null, ref payload);
        return true;
    }
''',
'''    public bool Dispatch(long id, ref ReadOnlySequence<byte> payload)
    {
        if (!TryFindMatchingCall(id, out var slots, out var index, out var current))
            return false;

        if (current!.Kind is PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming)
        {
            // A successful Response is only the server's acknowledgement; StreamComplete owns
            // the terminal transition for server and duplex streams. The callback shares the
            // per-call completion gate with terminal removal, so a matching acknowledgement is
            // observed before cancellation, deadline, or disconnect can report the terminal result.
            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) ||
                    current.Id != id ||
                    current.Kind is not (PendingCallKind.ServerStreaming or PendingCallKind.DuplexStreaming))
                {
                    return false;
                }

                current.CompletionObserver?.OnResponseObserved();
                return true;
            }
        }

        if (!TryTakeMatchingCall(id, out var call))
            return false;

        CompleteTakenCall(call!, PendingCallCompletionReason.Response, exception: null, ref payload);
        return true;
    }
''')

one(
'''    public bool Contains(long id)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return false;

        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);
        return call is not null && call.Id == id;
    }

    public CancellationToken GetProducerCancellationToken(long id)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return new CancellationToken(canceled: true);

        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);
        if (call is null || call.Id != id)
            return new CancellationToken(canceled: true);
        return call.ProducerCancellationToken;
    }
''',
'''    public bool Contains(long id)
        => TryFindMatchingCall(id, out _, out _, out _);

    public CancellationToken GetProducerCancellationToken(long id)
    {
        if (!TryFindMatchingCall(id, out _, out _, out var call))
            return new CancellationToken(canceled: true);
        return call!.ProducerCancellationToken;
    }
''')

one(
'''    public void FailAllPendingRequests(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return;

        for (var index = 0; index < slots.Length; index++)
        {
            if (!TryTakeCallAtIndex(index, out var call))
                continue;

            var payload = ReadOnlySequence<byte>.Empty;
            CompleteTakenCall(
                call!,
                PendingCallCompletionReason.ConnectionClosed,
                exception,
                ref payload);
        }
    }
''',
'''    public void FailAllPendingRequests(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var sparseSlots = Volatile.Read(ref _sparseSlots);
        if (sparseSlots is not null)
            FailAllPendingRequests(sparseSlots, exception);

        var slots = Volatile.Read(ref _slots);
        if (slots is not null)
            FailAllPendingRequests(slots, exception);
    }

    private void FailAllPendingRequests(PendingCall?[] slots, Exception exception)
    {
        for (var index = 0; index < slots.Length; index++)
        {
            if (!TryTakeCallAtIndex(slots, index, out var call))
                continue;

            var payload = ReadOnlySequence<byte>.Empty;
            CompleteTakenCall(
                call!,
                PendingCallCompletionReason.ConnectionClosed,
                exception,
                ref payload);
        }
    }
''')

if text.count('            var slots = GetOrCreateSlots();\n') != 2:
    raise SystemExit('expected two registration storage initializers')
text = text.replace('            var slots = GetOrCreateSlots();\n', '            var slots = GetOrCreateRegistrationSlots();\n')
if text.count('                    var index = (int)(id & _indexMask);\n') != 2:
    raise SystemExit('expected two registration index expressions')
text = text.replace('                    var index = (int)(id & _indexMask);\n', '                    var index = (int)(id & (slots.Length - 1));\n')

marker = '''                // A capacity reservation guarantees that some physical slot is free. Concurrent
                // registrars can consume the request IDs that map to that slot, so retry another
                // bounded round instead of reporting false resource exhaustion.
                Thread.Yield();
'''
replacement = '''                if (slots.Length < _capacity)
                {
                    slots = GetOrCreateSlots();
                    continue;
                }

                // A capacity reservation guarantees that some physical slot is free. Concurrent
                // registrars can consume the request IDs that map to that slot, so retry another
                // bounded round instead of reporting false resource exhaustion.
                Thread.Yield();
'''
if text.count(marker) != 2:
    raise SystemExit('expected two registration retry markers')
text = text.replace(marker, replacement)

one(
'''    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
''',
'''    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PendingCall?[] GetOrCreateRegistrationSlots()
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is not null)
            return slots;

        var sparseSlots = Volatile.Read(ref _sparseSlots);
        if (sparseSlots is not null)
            return sparseSlots;

        lock (_slotsInitializationGate)
        {
            slots = Volatile.Read(ref _slots);
            if (slots is not null)
                return slots;

            sparseSlots = Volatile.Read(ref _sparseSlots);
            if (sparseSlots is null)
            {
                sparseSlots = new PendingCall?[Math.Min(_capacity, SparseSlotCount)];
                Volatile.Write(ref _sparseSlots, sparseSlots);
            }
            return sparseSlots;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryFindMatchingCall(
        long id,
        out PendingCall?[] slots,
        out int index,
        out PendingCall? call)
    {
        var fullSlots = Volatile.Read(ref _slots);
        if (fullSlots is not null)
        {
            index = (int)(id & _indexMask);
            call = Volatile.Read(ref fullSlots[index]);
            if (call is not null && call.Id == id)
            {
                slots = fullSlots;
                return true;
            }
        }

        var sparseSlots = Volatile.Read(ref _sparseSlots);
        if (sparseSlots is not null)
        {
            index = (int)(id & (sparseSlots.Length - 1));
            call = Volatile.Read(ref sparseSlots[index]);
            if (call is not null && call.Id == id)
            {
                slots = sparseSlots;
                return true;
            }
        }

        slots = null!;
        index = 0;
        call = null;
        return false;
    }
''')

one(
'''    private bool TryTakeMatchingCall(long id, out PendingCall? call)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
        {
            call = null;
            return false;
        }

        var index = (int)(id & _indexMask);
        while (true)
        {
            var current = Volatile.Read(ref slots[index]);
            if (current is null || current.Id != id)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) || current.Id != id)
                    continue;

                var exchanged = Interlocked.CompareExchange(ref slots[index], null, current);
                if (!ReferenceEquals(exchanged, current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }

    private bool TryTakeCallAtIndex(int index, out PendingCall? call)
    {
        var slots = Volatile.Read(ref _slots)!;
        while (true)
        {
            var current = Volatile.Read(ref slots[index]);
            if (current is null)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current))
                    continue;

                if (!ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, current), current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }
''',
'''    private bool TryTakeMatchingCall(long id, out PendingCall? call)
    {
        while (true)
        {
            if (!TryFindMatchingCall(id, out var slots, out var index, out var current))
            {
                call = null;
                return false;
            }

            lock (current!.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) || current.Id != id)
                    continue;

                var exchanged = Interlocked.CompareExchange(ref slots[index], null, current);
                if (!ReferenceEquals(exchanged, current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }

    private static bool TryTakeCallAtIndex(PendingCall?[] slots, int index, out PendingCall? call)
    {
        while (true)
        {
            var current = Volatile.Read(ref slots[index]);
            if (current is null)
            {
                call = null;
                return false;
            }

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current))
                    continue;

                if (!ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, current), current))
                    continue;

                current.WaitUntilRegistered();
                call = current;
                return true;
            }
        }
    }
''')

one(
'''    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _deadlineScanRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
            var slots = Volatile.Read(ref _slots);
            if (slots is null)
                return;

            var now = _timeProvider.GetTimestamp();
            for (var index = 0; index < slots.Length; index++)
            {
                var call = Volatile.Read(ref slots[index]);
                if (call is null || !call.Deadline.HasValue)
                    continue;
                if (call.Deadline.Timestamp <= now)
                {
                    TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
                }
                else
                {
                    UpdateEarliestDeadline(call.Deadline.Timestamp);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _deadlineScanRunning, 0);
            var next = Volatile.Read(ref _approximateEarliestDeadline);
            if (next != long.MaxValue)
                ArmDeadlineTimer(next);
        }
    }
''',
'''    private void ScanExpiredDeadlines()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(ref _deadlineScanRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _approximateEarliestDeadline, long.MaxValue);
            var now = _timeProvider.GetTimestamp();
            var sparseSlots = Volatile.Read(ref _sparseSlots);
            if (sparseSlots is not null)
                ScanExpiredDeadlines(sparseSlots, now);

            var slots = Volatile.Read(ref _slots);
            if (slots is not null)
                ScanExpiredDeadlines(slots, now);
        }
        finally
        {
            Volatile.Write(ref _deadlineScanRunning, 0);
            var next = Volatile.Read(ref _approximateEarliestDeadline);
            if (next != long.MaxValue)
                ArmDeadlineTimer(next);
        }
    }

    private void ScanExpiredDeadlines(PendingCall?[] slots, long now)
    {
        for (var index = 0; index < slots.Length; index++)
        {
            var call = Volatile.Read(ref slots[index]);
            if (call is null || !call.Deadline.HasValue)
                continue;
            if (call.Deadline.Timestamp <= now)
            {
                TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);
            }
            else
            {
                UpdateEarliestDeadline(call.Deadline.Timestamp);
            }
        }
    }
''')

if not text.endswith('\n'):
    text += '\n'
path.write_text(text)
PY
}

for size in "${SIZES[@]}"; do
  dir="$RUNNER_TEMP/issue252-ring-$size"
  CANDIDATES+=("$dir")
  git worktree add --detach "$dir" "$DEV_SHA"
  cp "$ROOT/test/SharpLink.Benchmarks/PendingRequestSparseRingBenchmarks.cs" \
     "$dir/test/SharpLink.Benchmarks/PendingRequestSparseRingBenchmarks.cs"
  patch_candidate "$dir" "$size"
  grep -q "private const int SparseSlotCount = $size;" "$dir/src/SharpLink.Client/PendingRequestTable.cs"
done

# Build and run the relevant existing unit suite first. If the representation is not correctness-compatible,
# do not spend hosted time benchmarking it.
dotnet build "$BASE/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
for i in "${!SIZES[@]}"; do
  size="${SIZES[$i]}"
  dir="${CANDIDATES[$i]}"
  dotnet build "$dir/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release -v minimal
  dotnet test --project "$dir/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj" -c Release --no-restore
  dotnet run --project "$dir/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" -c Release --no-build -- \
    --pending-request-segmentation-evidence churn-memory --active 0 --connections 64 \
    > "$OUT/memory-$size.txt"
done

action_run() {
  local dir="$1"
  local label="$2"
  local pair="$3"
  local round="$4"
  local artifacts="$OUT/$pair-$round-$label"
  mkdir -p "$artifacts"
  (
    cd "$dir"
    dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release --no-build -- \
      --filter '*PendingRequestSparseRingBenchmarks*' --artifacts "$artifacts"
  )
}

for size in "${SIZES[@]}"; do
  idx=0
  for i in "${!SIZES[@]}"; do
    if [[ "${SIZES[$i]}" == "$size" ]]; then idx="$i"; break; fi
  done
  candidate="${CANDIDATES[$idx]}"
  action_run "$BASE" base "$size" 1
  action_run "$candidate" "ring$size" "$size" 1
  action_run "$candidate" "ring$size" "$size" 2
  action_run "$BASE" base "$size" 2
  action_run "$BASE" base "$size" 3
  action_run "$candidate" "ring$size" "$size" 3
done

python3 - "$OUT" <<'PY'
import csv
import glob
import json
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])
sizes = (64, 256, 1024)
SEQ = 'SequentialRegisterAndComplete'
CONC = 'RegisterAndCompleteAcrossFourWindows'

def split_number(value):
    value = (value or '').strip()
    if value in ('', '-', 'NA', 'N/A'):
        return 0.0, ''
    parts = value.replace(',', '').split()
    return float(parts[0]), parts[1] if len(parts) > 1 else ''

def to_ns(value):
    number, unit = split_number(value)
    scale = {'ns':1.0,'us':1_000.0,'µs':1_000.0,'μs':1_000.0,'ms':1_000_000.0,'s':1_000_000_000.0}
    return number * scale[unit]

def to_bytes(value):
    number, unit = split_number(value)
    scale = {'':1.0,'B':1.0,'KB':1024.0,'MB':1024.0*1024.0,'GB':1024.0*1024.0*1024.0}
    if unit not in scale:
        raise SystemExit(f'unknown allocation unit {unit!r} in {value!r}')
    return number * scale[unit]

def load(pair, round_no, label):
    files = glob.glob(str(root / f'{pair}-{round_no}-{label}' / 'results' / '*-report.csv'))
    if len(files) != 1:
        raise SystemExit(f'expected one CSV: {pair=} {round_no=} {label=} {files=}')
    with open(files[0], newline='', encoding='utf-8-sig') as handle:
        rows = list(csv.DictReader(handle))
    return {row['Method']: row for row in rows}

def sample(row):
    return {'ns':to_ns(row['Mean']), 'allocated_b':to_bytes(row.get('Allocated','0'))}

results=[]
for size in sizes:
    rounds=[]
    for n in (1,2,3):
        base=load(str(size),n,'base')
        cand=load(str(size),n,f'ring{size}')
        entry={'round':n}
        for name,key in ((SEQ,'sequential'),(CONC,'concurrent')):
            b=sample(base[name]); c=sample(cand[name])
            entry[key]={
                'base_ns':b['ns'],
                'candidate_ns':c['ns'],
                'delta_percent':(c['ns']/b['ns']-1.0)*100.0,
                'base_allocated_b':b['allocated_b'],
                'candidate_allocated_b':c['allocated_b'],
            }
        rounds.append(entry)
    seq=[r['sequential']['delta_percent'] for r in rounds]
    conc=[r['concurrent']['delta_percent'] for r in rounds]
    mem_lines=[line.strip() for line in (root/f'memory-{size}.txt').read_text().splitlines() if line.strip().startswith('{')]
    memory=json.loads(mem_lines[-1])
    result={
        'sparse_slots':size,
        'used_idle_retained_bytes_per_connection':memory['retainedBytesPerConnection'],
        'rounds':rounds,
        'median_sequential_delta_percent':statistics.median(seq),
        'sequential_rounds_within_3_percent':sum(x <= 3.0 for x in seq),
        'median_concurrent_delta_percent':statistics.median(conc),
        'concurrent_rounds_within_3_percent':sum(x <= 3.0 for x in conc),
        'sequential_zero_allocation':all(r['sequential']['base_allocated_b']==0 and r['sequential']['candidate_allocated_b']==0 for r in rounds),
    }
    result['passed']= (
        result['median_sequential_delta_percent'] <= 3.0 and
        result['sequential_rounds_within_3_percent'] >= 2 and
        result['median_concurrent_delta_percent'] <= 3.0 and
        result['concurrent_rounds_within_3_percent'] >= 2 and
        result['sequential_zero_allocation']
    )
    results.append(result)

final={
    'experiment':'issue252-lazy-direct-ring-plus-flat-promotion',
    'dev_sha':'9b6f627954ec5a0eaca31b4cea5accdd4a6d79c9',
    'gate_percent':3.0,
    'results':results,
}
print('SPARSE_RING_RESULT='+json.dumps(final,separators=(',',':')))
if not any(r['passed'] for r in results):
    raise SystemExit('No sparse-ring size satisfied the predeclared CPU/allocation gate')
PY
