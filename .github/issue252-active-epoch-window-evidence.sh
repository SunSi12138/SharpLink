#!/usr/bin/env bash
set -euo pipefail

ROOT="$GITHUB_WORKSPACE"
DEV_SHA="9b6f627954ec5a0eaca31b4cea5accdd4a6d79c9"
BENCH_SHA="daf649733efca3c2ba7ad1e0fd82d3d0b2b51495"
BASE="$RUNNER_TEMP/issue252-active-epoch-base"
CANDIDATE="$RUNNER_TEMP/issue252-active-epoch-candidate"
OUT="$ROOT/artifacts/issue-252-active-epoch-window"

rm -rf "$BASE" "$CANDIDATE" "$OUT"
mkdir -p "$OUT"

git fetch --no-tags origin dev agent/issue-252-deadline-workload-baseline
ACTUAL_DEV_SHA="$(git rev-parse origin/dev)"
ACTUAL_BENCH_SHA="$(git rev-parse origin/agent/issue-252-deadline-workload-baseline)"
if [[ "$ACTUAL_DEV_SHA" != "$DEV_SHA" ]]; then
  echo "dev moved: expected $DEV_SHA, got $ACTUAL_DEV_SHA" >&2
  exit 1
fi
if [[ "$ACTUAL_BENCH_SHA" != "$BENCH_SHA" ]]; then
  echo "benchmark source moved: expected $BENCH_SHA, got $ACTUAL_BENCH_SHA" >&2
  exit 1
fi

git worktree add --detach "$BASE" "$DEV_SHA"
git worktree add --detach "$CANDIDATE" "$DEV_SHA"
cleanup() {
  git -C "$ROOT" worktree remove --force "$BASE" >/dev/null 2>&1 || true
  git -C "$ROOT" worktree remove --force "$CANDIDATE" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "[issue252-active-epoch] base_sha=$(git -C "$BASE" rev-parse HEAD)"
echo "[issue252-active-epoch] candidate_source_sha=$(git -C "$CANDIDATE" rev-parse HEAD)"
echo "[issue252-active-epoch] benchmark_sha=$BENCH_SHA"

# Use the exact schema-v2 comprehensive workload already reviewed in #272 on both variants.
for dir in "$BASE" "$CANDIDATE"; do
  git show "$BENCH_SHA:test/SharpLink.Benchmarks/DeadlineWorkloadEvidenceRunner.cs" \
    > "$dir/test/SharpLink.Benchmarks/DeadlineWorkloadEvidenceRunner.cs"
  git show "$BENCH_SHA:test/SharpLink.Benchmarks/Program.cs" \
    > "$dir/test/SharpLink.Benchmarks/Program.cs"

  cat > "$dir/test/SharpLink.Benchmarks/PendingRequestEpochGuardrailBenchmarks.cs" <<'CS'
using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Guardrail for issue #252 scan-index experiments. Every invocation consumes one 256-ID page.
/// The deadline methods reset the existing earliest-deadline epoch outside the timed region so the
/// measured batch starts fresh/re-armed. The four-worker cases use persistent workers.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 5, iterationCount: 30)]
public class PendingRequestEpochGuardrailBenchmarks
{
    private const int PageSize = 256;
    private const int WorkerCount = 4;
    private const int CallsPerWorker = PageSize / WorkerCount;
    private const int WorkerNoDeadline = 1;
    private const int WorkerDeadline = 2;

    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _pending = null!;
    private IRpcCodec<int> _codec = null!;
    private byte[] _responsePayload = null!;
    private RpcDeadline _deadline;
    private FieldInfo _earliestField = null!;
    private Barrier _workerBarrier = null!;
    private Thread[] _workers = null!;
    private Exception?[] _workerFailures = null!;
    private int _workerMode;
    private int _stopWorkers;

    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkRuntimeContextBuilder().Build();
        _pending = new PendingRequestTable(
            65_536,
            _context.Codecs,
            BenchmarkPendingCallOwner.Instance,
            TimeProvider.System);
        _codec = _context.Codecs.GetCodec<int>();
        _responsePayload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(_responsePayload, 42);
        _deadline = RpcDeadline.Create(
            TimeProvider.System.GetUtcNow().AddDays(1),
            TimeProvider.System);
        _earliestField = typeof(PendingRequestTable).GetField(
            "_approximateEarliestDeadline",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing earliest-deadline field.");

        _ = CompleteNoDeadlineCall();
        _ = CompleteDeadlineCall();

        while (true)
        {
            var operation = _pending.Rent<int>(out var requestId);
            _ = Complete(operation, requestId);
            if ((requestId & (PageSize - 1)) == PageSize - 1)
                break;
        }

        _workerFailures = new Exception?[WorkerCount];
        _workerBarrier = new Barrier(WorkerCount + 1);
        _workers = new Thread[WorkerCount];
        for (var worker = 0; worker < WorkerCount; worker++)
        {
            var workerIndex = worker;
            _workers[worker] = new Thread(() => WorkerLoop(workerIndex))
            {
                IsBackground = true,
                Name = $"pending-epoch-guardrail-{workerIndex}"
            };
            _workers[worker].Start();
        }
    }

    [IterationSetup]
    public void ResetEarliestDeadlineEpoch()
        => _earliestField.SetValue(_pending, long.MaxValue);

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_workers is not null)
        {
            Volatile.Write(ref _stopWorkers, 1);
            _workerBarrier.SignalAndWait();
            _workerBarrier.SignalAndWait();
            foreach (var worker in _workers)
                worker.Join();
            _workerBarrier.Dispose();
        }

        _pending.Dispose();
        _context.Dispose();
    }

    [Benchmark(OperationsPerInvoke = PageSize)]
    public int RegisterAndCompleteNoDeadline()
    {
        var result = 0;
        for (var call = 0; call < PageSize; call++)
            result = CompleteNoDeadlineCall();
        return result;
    }

    [Benchmark(OperationsPerInvoke = PageSize)]
    public int RegisterAndCompleteNoDeadlineWithinOnePage()
        => RunWorkers(WorkerNoDeadline);

    [Benchmark(OperationsPerInvoke = PageSize)]
    public int RegisterAndCompleteWithLongDeadline()
    {
        var result = 0;
        for (var call = 0; call < PageSize; call++)
            result = CompleteDeadlineCall();
        return result;
    }

    [Benchmark(OperationsPerInvoke = PageSize)]
    public int RegisterAndCompleteLongDeadlinesWithinOnePage()
        => RunWorkers(WorkerDeadline);

    private int RunWorkers(int mode)
    {
        for (var worker = 0; worker < _workerFailures.Length; worker++)
            _workerFailures[worker] = null;
        Volatile.Write(ref _workerMode, mode);

        _workerBarrier.SignalAndWait();
        _workerBarrier.SignalAndWait();

        for (var worker = 0; worker < _workerFailures.Length; worker++)
        {
            if (_workerFailures[worker] is { } failure)
                throw new InvalidOperationException($"Guardrail worker {worker} failed.", failure);
        }

        var remaining = _pending.ActiveCount;
        if (remaining != 0)
            throw new InvalidOperationException($"Guardrail leaked {remaining} pending calls.");
        return remaining;
    }

    private void WorkerLoop(int worker)
    {
        while (true)
        {
            _workerBarrier.SignalAndWait();
            if (Volatile.Read(ref _stopWorkers) != 0)
            {
                _workerBarrier.SignalAndWait();
                return;
            }

            try
            {
                var mode = Volatile.Read(ref _workerMode);
                for (var call = 0; call < CallsPerWorker; call++)
                {
                    _ = mode == WorkerDeadline
                        ? CompleteDeadlineCall()
                        : CompleteNoDeadlineCall();
                }
            }
            catch (Exception exception)
            {
                _workerFailures[worker] = exception;
            }

            _workerBarrier.SignalAndWait();
        }
    }

    private int CompleteNoDeadlineCall()
    {
        var operation = _pending.Rent<int>(out var requestId);
        return Complete(operation, requestId);
    }

    private int CompleteDeadlineCall()
    {
        var operation = _pending.Rent(
            _codec,
            PendingCallKind.Unary,
            _deadline,
            CancellationToken.None,
            out var requestId);
        return Complete(operation, requestId);
    }

    private int Complete(RpcRequestOperation<int> operation, long requestId)
    {
        var payload = new ReadOnlySequence<byte>(_responsePayload);
        if (!_pending.Dispatch(requestId, ref payload))
            throw new InvalidOperationException("Guardrail dispatch failed.");
        return operation.AsValueTask().GetAwaiter().GetResult();
    }
}
CS
done

# Prototype only in the isolated candidate worktree. It adds no deadline-registration marker.
python3 - "$CANDIDATE/src/SharpLink.Client/PendingRequestTable.cs" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
text = path.read_text()

def one(old: str, new: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"expected exactly one patch match, got {count}: {old[:140]!r}")
    text = text.replace(old, new, 1)

one(
'''    private long _nextId;\n    private long _approximateEarliestDeadline = long.MaxValue;\n''',
'''    private long _nextId;\n    // Request ID immediately before the current non-idle epoch. The value is only advanced after\n    // _activeSlots reaches zero and is rechecked as still zero, so every live request is newer.\n    private long _deadlineScanEpochStartId;\n    private long _approximateEarliestDeadline = long.MaxValue;\n''')

idle_old = '''        if (remaining == 0)\n            _owner.OnPendingCallCapacityIdle();\n'''
if text.count(idle_old) != 2:
    raise SystemExit(f"expected two idle callback sites, got {text.count(idle_old)}")
text = text.replace(
    idle_old,
'''        if (remaining == 0)\n        {\n            RecordDeadlineScanEpochStartIfStillIdle();\n            _owner.OnPendingCallCapacityIdle();\n        }\n''')

one(
'''    private void SignalSlotAvailable()\n    {\n''',
'''    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n    private void RecordDeadlineScanEpochStartIfStillIdle()\n    {\n        // Capacity is reserved before request IDs are allocated. If a new registrar starts before the\n        // second read, _activeSlots is non-zero and this reset is abandoned. If it starts afterwards,\n        // its eventual ID is necessarily newer than this snapshot.\n        var boundary = Volatile.Read(ref _nextId);\n        if (Volatile.Read(ref _activeSlots) == 0)\n            Volatile.Write(ref _deadlineScanEpochStartId, boundary);\n    }\n\n    private void SignalSlotAvailable()\n    {\n''')

old_scan = '''            var now = _timeProvider.GetTimestamp();\n            for (var index = 0; index < slots.Length; index++)\n            {\n                var call = Volatile.Read(ref slots[index]);\n                if (call is null || !call.Deadline.HasValue)\n                    continue;\n                if (call.Deadline.Timestamp <= now)\n                {\n                    TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);\n                }\n                else\n                {\n                    UpdateEarliestDeadline(call.Deadline.Timestamp);\n                }\n            }\n'''
new_scan = '''            if (Volatile.Read(ref _activeSlots) == 0)\n                return;\n\n            var now = _timeProvider.GetTimestamp();\n            var epochStartId = Volatile.Read(ref _deadlineScanEpochStartId);\n            var currentId = Volatile.Read(ref _nextId);\n            var epochSpan = unchecked((ulong)(currentId - epochStartId));\n\n            // Every live call belongs to the current non-idle epoch. If fewer than Capacity request-ID\n            // positions have elapsed since the last proven idle point, their physical slots are exactly\n            // this bounded ring window. ID churn/collisions may make the window larger than ActiveCount,\n            // but can never place a live call outside it. Ambiguous or wide epochs fall back to dev's\n            // capacity-wide scan.\n            if (epochSpan > 0 && epochSpan < (ulong)slots.Length)\n            {\n                var firstIndex = (int)(unchecked(epochStartId + 1) & _indexMask);\n                for (var offset = 0; offset < (int)epochSpan; offset++)\n                    ScanDeadlineSlot(slots, (firstIndex + offset) & _indexMask, now);\n            }\n            else\n            {\n                for (var index = 0; index < slots.Length; index++)\n                    ScanDeadlineSlot(slots, index, now);\n            }\n'''
one(old_scan, new_scan)

one(
'''    private void ArmDeadlineTimer(long deadlineTimestamp)\n    {\n''',
'''    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n    private void ScanDeadlineSlot(PendingCall?[] slots, int index, long now)\n    {\n        var call = Volatile.Read(ref slots[index]);\n        if (call is null || !call.Deadline.HasValue)\n            return;\n        if (call.Deadline.Timestamp <= now)\n            TryComplete(call.Id, PendingCallCompletionReason.DeadlineExceeded);\n        else\n            UpdateEarliestDeadline(call.Deadline.Timestamp);\n    }\n\n    private void ArmDeadlineTimer(long deadlineTimestamp)\n    {\n''')

path.write_text(text)
PY

# Build identical benchmark surfaces, then validate prototype correctness before performance evidence.
for dir in "$BASE" "$CANDIDATE"; do
  dotnet restore "$dir/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj"
  dotnet build "$dir/test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj" \
    -c Release --no-restore -v minimal
done

dotnet test "$CANDIDATE/test/SharpLink.UnitTests/SharpLink.UnitTests.csproj" \
  -c Release -v minimal

run_workload() {
  local dir="$1"
  local variant="$2"
  local round="$3"
  local json="$OUT/workload-${round}-${variant}.json"
  echo "[issue252-active-epoch] workload round=$round variant=$variant"
  (
    cd "$dir"
    dotnet run -c Release --no-build \
      --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -- \
      --deadline-workload-evidence \
      --rounds 1 \
      --warmup-seconds 2 \
      --duration-seconds 8 \
      --json "$json" \
      --production-baseline-sha "$DEV_SHA"
  )
}

# Alternating order limits hosted-run drift bias.
run_workload "$BASE" dev 1
run_workload "$CANDIDATE" active-epoch 1
run_workload "$CANDIDATE" active-epoch 2
run_workload "$BASE" dev 2
run_workload "$BASE" dev 3
run_workload "$CANDIDATE" active-epoch 3

python3 - "$OUT" <<'PY'
import json
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])
scenarios = ["single-fast", "concurrent-fast", "concurrent-normal", "deadline-heavy"]

def report(round_number, variant):
    return json.loads((root / f"workload-{round_number}-{variant}.json").read_text())

def row(data, scenario):
    matches = [item for item in data["Results"] if item["Scenario"] == scenario]
    if len(matches) != 1:
        raise SystemExit(f"expected one {scenario} row, got {len(matches)}")
    return matches[0]

rounds = {scenario: [] for scenario in scenarios}
for round_number in (1, 2, 3):
    base = report(round_number, "dev")
    candidate = report(round_number, "active-epoch")
    for scenario in scenarios:
        b = row(base, scenario)
        c = row(candidate, scenario)
        rounds[scenario].append({
            "round": round_number,
            "base_qps": b["Qps"],
            "candidate_qps": c["Qps"],
            "qps_delta_percent": (c["Qps"] / b["Qps"] - 1.0) * 100.0,
            "base_cpu_ns_op": b["CpuNanosecondsPerOperation"],
            "candidate_cpu_ns_op": c["CpuNanosecondsPerOperation"],
            "cpu_delta_percent": (c["CpuNanosecondsPerOperation"] / b["CpuNanosecondsPerOperation"] - 1.0) * 100.0,
            "base_b_op": b["AllocatedBytesPerOperation"],
            "candidate_b_op": c["AllocatedBytesPerOperation"],
            "allocation_delta_b_op": c["AllocatedBytesPerOperation"] - b["AllocatedBytesPerOperation"],
            "base_p95_ms": b["P95LatenessMilliseconds"],
            "candidate_p95_ms": c["P95LatenessMilliseconds"],
            "base_p99_ms": b["P99LatenessMilliseconds"],
            "candidate_p99_ms": c["P99LatenessMilliseconds"],
            "base_timer_callbacks_per_second": b["TimerCallbacksPerSecond"],
            "candidate_timer_callbacks_per_second": c["TimerCallbacksPerSecond"],
        })

summaries = {}
for scenario in scenarios:
    values = rounds[scenario]
    summaries[scenario] = {
        "median_cpu_delta_percent": statistics.median(item["cpu_delta_percent"] for item in values),
        "median_qps_delta_percent": statistics.median(item["qps_delta_percent"] for item in values),
        "median_allocation_delta_b_op": statistics.median(item["allocation_delta_b_op"] for item in values),
        "median_base_p95_ms": statistics.median(item["base_p95_ms"] for item in values),
        "median_candidate_p95_ms": statistics.median(item["candidate_p95_ms"] for item in values),
        "median_base_p99_ms": statistics.median(item["base_p99_ms"] for item in values),
        "median_candidate_p99_ms": statistics.median(item["candidate_p99_ms"] for item in values),
        "median_base_timer_callbacks_per_second": statistics.median(item["base_timer_callbacks_per_second"] for item in values),
        "median_candidate_timer_callbacks_per_second": statistics.median(item["candidate_timer_callbacks_per_second"] for item in values),
    }

overall_cpu = statistics.median(item["median_cpu_delta_percent"] for item in summaries.values())

def lateness_ok(item, percentile):
    base = item[f"median_base_{percentile}_ms"]
    candidate = item[f"median_candidate_{percentile}_ms"]
    return candidate <= base + max(0.05, base * 0.25)

passed = (
    overall_cpu <= -5.0
    and all(item["median_cpu_delta_percent"] <= 3.0 for item in summaries.values())
    and all(item["median_qps_delta_percent"] >= -3.0 for item in summaries.values())
    and all(item["median_allocation_delta_b_op"] <= 1.0 for item in summaries.values())
    and all(lateness_ok(item, "p95") and lateness_ok(item, "p99") for item in summaries.values())
)

result = {
    "experiment": "active-epoch-deadline-scan-window-combined-workload",
    "dev_sha": "9b6f627954ec5a0eaca31b4cea5accdd4a6d79c9",
    "predeclared_gate": {
        "overall_median_cpu_delta_percent_max": -5.0,
        "per_scenario_cpu_regression_percent_max": 3.0,
        "per_scenario_qps_regression_percent_max": 3.0,
        "allocation_delta_b_op_max": 1.0,
        "lateness_limit": "candidate <= base + max(0.05 ms, 25% of base)",
    },
    "rounds": rounds,
    "summaries": summaries,
    "overall_median_cpu_delta_percent": overall_cpu,
    "passed": passed,
}
(root / "combined-result.json").write_text(json.dumps(result, indent=2))
print("COMBINED_WORKLOAD_RESULT=" + json.dumps(result, separators=(",", ":")))
if not passed:
    raise SystemExit("active-epoch window did not produce a material net combined-workload win")
PY

# Only a combined-workload winner reaches the normal/deadline hot-path guardrail.
run_guardrail() {
  local dir="$1"
  local variant="$2"
  local round="$3"
  local artifacts="$OUT/guardrail-${round}-${variant}"
  mkdir -p "$artifacts"
  echo "[issue252-active-epoch] guardrail round=$round variant=$variant"
  (
    cd "$dir"
    dotnet run --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj \
      -c Release --no-build -- \
      --filter '*PendingRequestEpochGuardrailBenchmarks*' \
      --artifacts "$artifacts"
  )
}

run_guardrail "$BASE" dev 1
run_guardrail "$CANDIDATE" active-epoch 1
run_guardrail "$CANDIDATE" active-epoch 2
run_guardrail "$BASE" dev 2
run_guardrail "$BASE" dev 3
run_guardrail "$CANDIDATE" active-epoch 3

python3 - "$OUT" <<'PY'
import csv
import glob
import json
import statistics
import sys
from pathlib import Path

root = Path(sys.argv[1])
methods = [
    "RegisterAndCompleteNoDeadline",
    "RegisterAndCompleteNoDeadlineWithinOnePage",
    "RegisterAndCompleteWithLongDeadline",
    "RegisterAndCompleteLongDeadlinesWithinOnePage",
]

def split_number(value):
    value = (value or "").strip()
    if value in ("", "-", "NA", "N/A"):
        return 0.0, ""
    parts = value.replace(",", "").split()
    return float(parts[0]), parts[1] if len(parts) > 1 else ""

def to_ns(value):
    number, unit = split_number(value)
    scale = {"ns": 1.0, "us": 1_000.0, "µs": 1_000.0, "μs": 1_000.0,
             "ms": 1_000_000.0, "s": 1_000_000_000.0}
    if unit not in scale:
        raise SystemExit(f"unknown time unit {unit!r} in {value!r}")
    return number * scale[unit]

def to_bytes(value):
    number, unit = split_number(value)
    scale = {"": 1.0, "B": 1.0, "KB": 1024.0, "MB": 1024.0 * 1024.0}
    if unit not in scale:
        raise SystemExit(f"unknown allocation unit {unit!r} in {value!r}")
    return number * scale[unit]

def samples(round_number, variant):
    files = glob.glob(str(root / f"guardrail-{round_number}-{variant}" / "results" / "*-report.csv"))
    if len(files) != 1:
        raise SystemExit(f"expected one CSV for {round_number}/{variant}, got {files}")
    with open(files[0], newline="", encoding="utf-8-sig") as handle:
        rows = list(csv.DictReader(handle))
    by_method = {row["Method"]: row for row in rows}
    missing = [method for method in methods if method not in by_method]
    if missing:
        raise SystemExit(f"missing guardrail methods {missing}")
    return {
        method: {
            "ns": to_ns(by_method[method]["Mean"]),
            "allocated_b": to_bytes(by_method[method].get("Allocated", "0")),
        }
        for method in methods
    }

rounds = {method: [] for method in methods}
for round_number in (1, 2, 3):
    base = samples(round_number, "dev")
    candidate = samples(round_number, "active-epoch")
    for method in methods:
        b = base[method]
        c = candidate[method]
        rounds[method].append({
            "round": round_number,
            "base_ns": b["ns"],
            "candidate_ns": c["ns"],
            "delta_percent": (c["ns"] / b["ns"] - 1.0) * 100.0,
            "base_allocated_b": b["allocated_b"],
            "candidate_allocated_b": c["allocated_b"],
        })

summaries = {}
for method in methods:
    values = rounds[method]
    deltas = [item["delta_percent"] for item in values]
    summaries[method] = {
        "median_delta_percent": statistics.median(deltas),
        "rounds_within_3_percent": sum(delta <= 3.0 for delta in deltas),
        "zero_allocation": all(
            item["base_allocated_b"] == 0.0 and item["candidate_allocated_b"] == 0.0
            for item in values
        ),
    }

passed = all(
    item["median_delta_percent"] <= 3.0
    and item["rounds_within_3_percent"] >= 2
    and item["zero_allocation"]
    for item in summaries.values()
)
result = {
    "experiment": "active-epoch-normal-and-deadline-maintenance-guardrail",
    "gate_percent": 3.0,
    "rounds": rounds,
    "summaries": summaries,
    "passed": passed,
}
(root / "guardrail-result.json").write_text(json.dumps(result, indent=2))
print("ACTIVE_EPOCH_GUARDRAIL_RESULT=" + json.dumps(result, separators=(",", ":")))
if not passed:
    raise SystemExit("active-epoch window missed the retained normal/deadline hot-path gate")
PY
