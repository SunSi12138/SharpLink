# Issue 739: bounded source multiplicity diagnostic

This is an isolated diagnostic projection, never a product-source change. A is
the exact `dev` production source at
`eb99fe887cf2129d9b88441245ca0a4a6406b6c2`; B adds fixed atomic counters to copies
of named source sites. Both use the same mechanically projected baseline driver.
No custom awaiter, builder, task conversion, or continuation wrapper is added.

## Run

```sh
python3 -m unittest discover -s eng/validation/issue739/multiplicity -p 'test_*.py'
python3 eng/validation/issue739/multiplicity/run.py \
  --dotnet eng/validation/issue739/dotnet-pinned.sh \
  --output artifacts/issue739/dev-multiplicity \
  --work-root /tmp/issue739-multiplicity
```

Use SDK **10.0.112**, runtime **10.0.12**, with
`ISSUE739_RUNTIME_VERSION=10.0.12`. The work directory must be new. A whole-run
1,380-second budget and per-process timeouts preserve partial manifests on
failure; an incomplete matrix is never relabeled complete.

Four cells are TCP/SHM Add at c1/c32. Every cell runs `ABBA ABBA ABBA AA`, giving
56 fresh RPC processes, plus one diagnostic counter-primitive control process.
Each RPC process retains baseline N=131072, warmup=32768, one connection,
ThreadPool minimum 132, five-minute heartbeat, no request timeout, telemetry,
interceptors, retry, compression, or health calls from the driver. Generated
method descriptors and relevant configuration are checked at setup.

Local non-performance correctness may use
`--correctness-only --tcp-only --operations 128 --warmup 64`; this runs A/B at
TCP c1/c32 and deliberately produces no transferred allocation budget. Do not
run SHM locally where its shared-memory permissions are unavailable, and do not
retry denied socket or diagnostic access through another route.

## Counter meaning

- Logical decision/fast/slow/helper counts preserve the original awaiter. In this
  exclusive plain unary path, `RpcRequestOperation<int>.OnCompleted` is the actual
  logical helper's underlying continuation registration. Entry, accepted,
  failure and in-flight counts guard the fact that a callback can complete and
  recycle the operation before `OnCompleted` returns. No mutable operation field
  is read after forwarding.
- Successful permit constructions are split by `mayDecode`. Only the plain
  shape is priced at 64 bytes. Snapshot cache hits and explicit new snapshots
  are separate from AsyncLocal assignment counts.
- Push and restore categorize attempted assignments: null/null, null/snapshot, same snapshot,
  snapshot/null, and different snapshot references. Only balanced qualifying
  null→snapshot→null cycles use the 72-byte calibration in a successful run.
  This term additionally assumes no other AsyncLocal map entries; the category
  counters cannot verify that runtime-internal condition.
- TCP ownership-reader `IsCompletedSuccessfully` branches and pooled helper
  entries are source observations, not true suspension or allocation counts.
  `AwaitReadAsync` keeps its original pooling builder.
- Send accepted frames, flush calls/frames/status snapshots, capacity waiter
  uses, and successful capacity TCS construction are separate. A flush status
  snapshot is not actual registration.
- WakeupSignal and SHM pulse accepted registrations are actual registrations at
  those inner sources. SHM pulse counts are intentionally undifferentiated
  outbound/control/data/space signals and cannot be called read suspension.
- `RpcRequestOperation` constructor counts and the named `new` counters cover
  only those explicit materialization sites. Hidden BCL/pool allocations remain
  unknown. Never equate rent/helper/registration counts with all object counts.

## Boundaries and evidence

Counters are initialized during warmup and never reset around pending work.
Pre/post raw arrays and their timestamp spans are retained. These diagnostic
snapshots surround a slightly wider window than the byte timestamps. The
registration-return fence is separate from logical-active or admitted-active
counts; the latter do not prove an `OnCompleted` caller has returned.

The allocation window includes **N payload calls plus one drain Add sentinel**.
The ledger expects N+1 where source semantics imply it. It never subtracts the
sentinel or empty-driver costs. The existing fixed 20 ms terminal tail is not a
formal whole-transport cleanup fence; read/signal deltas remain window counts.

The conditional budget is
`136 × accepted logical-source registrations + 64 × plain permit constructions
+ 72 × qualifying context cycles`. It transfers prior runtime-10.0.12
source-shape calibrations, not exact-all-object attribution. Failed assumptions
leave the prediction null. Signed residuals, including negative residuals, are
retained. No 90%-owner-coverage or zero-transport-allocation claim is warranted.

Artifacts contain input/source hashes, A/B source patches, all projected source
hashes, actual executed assembly/config hashes, raw logs and JSON, partial and
final owner ledgers, raw ABBA B-minus-A differences and AA variation. Reflection
records compiler-generated async state-machine fields and builder attributes;
all A/B samples must match. This verifies unchanged field metadata, not runtime
allocation size or identical machine code.

Vanilla remains the primary allocation/performance result. Diagnostic CPU,
batching, status and timing changes are observer effects. Do not subtract them
as a known overhead correction. The phase ends after this four-cell ledger;
outer RPC read-consumer registration, TCP/BCL registrations, hidden pool misses,
the .NET 11 SendPump control and stack-attribution gaps remain explicitly open.
