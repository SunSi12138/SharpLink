# Issue 739: finite controlled SendPump costs

Exact production source: `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`.
The fixture never changes production C#. This closes a named source-shape measurement gap, not default RPC ownership: the prior actual Add probe did not activate incomplete writer flush or send-capacity waits.

## Cases and units

All actual cases use one warmed, handshaken `RpcSession`, Balanced's ordinary non-timed flush policy, caller `CancellationToken.None`, and the identical header-only normal Response frame (`F=15` serialized bytes, request ID 1). A fresh process runs one case. Setup, JIT warmup, one-second tiering pause, full GC, hashes, JSON and disposal are outside the measured interval. The controller is synchronous and polls then consumes each ValueTask exactly once: it does not register the ordinary asynchronous API caller continuation. This is deliberately a controlled SendPump shape, not ordinary async caller cost.

| Case | One measured cycle |
|---|---|
| pump-idle-sync | Idle-wait publication observed; real non-force admission; synchronous flush; exact lease inactive, zero reserved bytes and new idle-wait publication observed |
| pump-force-sync | Same normal frame, but `SendPacketAndFlushAsync`; separately labeled production force-flush completion/TCS contract |
| pump-flush-gated | Real non-force send; actual writer FlushAsync initially incomplete until its reusable core has accepted and returned from continuation registration |
| pump-capacity-completed | First flush held; capacity `2F`; second real enqueue completes synchronously, reservation becomes `2F`; release; second flush synchronous |
| pump-capacity-incomplete | Identical pair, capacity `F`; second real admission remains incomplete and reservation remains `F` until release; eventual two frames/two flushes |
| fixture-sync | Same pool rent, encode, copy, synchronous writer flush, return and lease observation without RpcSession |
| fixture-gated | Same fixture work with gated source registration/release/consumption |
| fixture-pair | Two simultaneous rented leases; first gated flush, second synchronous flush, matching pair controller/lease work |

Both capacities are below the 32KiB progress-reserve threshold. Neither frame is oversize. The writer validates exact serialized bytes and exactly one frame per flush. Every cycle requires exact flush, pending, reset, accepted-registration, registration-return, release, GetResult, byte and frame deltas; two-frame cycles are rejected if coalesced or otherwise different. Admission state and reserved bytes are checked before release. There is no per-cycle artificial TCS, Task.Run, AsTask, WhenAll, growing writer buffer or closure. The forced production contract and capacity waiter may themselves allocate, which is the point of these separately labeled paths.

## Observability boundaries

- `HasPendingSendPumpIdleWait` is an existing point-in-time publication observation, not proof every pump callback stack has unwound. The code/source explicitly permits publication before the idle source's OnCompleted returns. Residual idle-registration bookkeeping may cross the whole-process measurement window; no stronger fence is claimed.
- The controlled writer's gate observes its actual `ManualResetValueTaskSourceCore<FlushResult>.OnCompleted` acceptance and return plus wrapper registration-inflight zero. Release never relies on a queued completion callback. After the final registration-return publication the wrapper makes no further core or continuation access. This does not claim that the wrapper caller has observed its machine-level return instruction.
- Source completion is explicitly inline-capable. Source Reset requires prior consumption and no registration-inflight; one pending source at a time. This is a measurement policy, not a transport scheduling recommendation.
- Read-only, prewarmed `UnsafeAccessor` observes `PooledByteBufferWriter._active` (field/type validated during setup). The exact rented objects are retained through cycle-end checks, with no intervening rent. State `1 -> 0`, zero reservations, verified writer consumption and idle publication jointly establish the intended checkpoints. This is not a count of calls to Return, their exact timing, or native resource reclamation.
- Observer/controller CPU and allocation costs remain included in gross samples and matching fixture controls. No blind fixture subtraction. Fixture controls do not simulate queue scheduling, async caller continuation registration, or transport work.
- No cancellation/fault, default RPC owner, TCP/SHM, AOT, optimization, #387 gate or reopened #741/#750 claim.

## Exact CLI and budgets

Python3 and the selected .NET SDK are required. No extra Python package is needed. Output and work-root must both be absent; failed artifacts are never overwritten and samples are never retried or pruned.

Local correctness (SDK10.0.102/runtime10.0.2, eight fresh records):

```
python3 eng/validation/issue739/sendpump/run.py --stage local-pilot \
  --dotnet /tmp/dotnet754/dotnet --output /tmp/sendpump-local \
  --work-root /tmp/sendpump-local-work --budget-seconds 300 \
  --nuget-source /tmp/issue739-home/.nuget/packages
```

For the provided local environment, set HOME, DOTNET_CLI_HOME to `/tmp/issue739-home` and NUGET_PACKAGES to `/tmp/issue739-home/.nuget/packages`. This uses the already populated offline package cache. `--cycles`/`--warmup` overrides are allowed only in local correctness mode.

Hosted net10 (SDK10.0.112/runtime10.0.12, six fresh processes/case =48 records):

```
python3 eng/validation/issue739/sendpump/run.py --stage net10 --dotnet dotnet \
  --output artifacts/issue739/sendpump-net10 \
  --work-root "$RUNNER_TEMP/issue739-sendpump-net10-work" --budget-seconds 840
```

Hosted net11 is a separately approved run after net10 audit. Replace stage/output/work suffix with `net11`. Exact SDK11.0.100-rc.1.26425.128/runtime11.0.0-rc.1.26425.128; three ABBA cycles plus AA per eight cases =112 fresh records. Both hosted stages use16384 measured cycles and4096 warmup cycles. Every process has60s timeout and the runner's remaining total budget bounds every subprocess. Python export/rehash work is not interruptible by that subprocess guard; the workflow job timeout is the outer hard limit. Keep enough job time for failure-artifact upload.

## Builds and evidence

Only Runtime and Abstractions are dependencies of this driver. Builds happen in isolated copies. .NET11 only toggles those two production libraries. Driver and metadata inspector stay traditional. B's independently built driver is archived, binding configuration is checked, and the complete A driver artifact set is reused for B without changing B dependencies. Actual compiler commands and metadata prove selected SendPump/session lowering, unchanged source/API signatures, and exact driver identity. The existing source-pinned net11 `run_pilot.py` helpers and Metadata inspector are reused unchanged; their input hashes are included. The pooled-reader metadata witness retains its annotation yet changes to runtime-async under this exact RC1, so annotation is never treated as automatic opt-out. That witness is not a measured SendPump path.

Outputs preserve raw samples/logs, failures, source/build-overlay hashes, actual compiler commands/binlogs, compiler/runtime/reference-pack hashes, resolved packages, executable/DLL/config/inspector hashes, original independent B artifacts, selected-method metadata proof, all executed binaries/configs, harness sources, fresh PIDs, whole-process precise bytes, cycle/frame denominators, ns, process CPU, Gen0 and exact warmup/measured lifecycle counts. The end rehash verifies inputs, executed DLL/configs and toolchain. Summary includes every raw value, medians/ranges and net11 paired ABBA differences/AA drift. Comparable net11 A/B summaries contain only ABBA rows; the two AA rows are reported separately. Signed short-token rollovers are checked against the exact number of gated resets in both warmup and measurement (hosted counts do not reach a rollover). Offline NuGet source/audit overrides are restricted to local correctness mode. It intentionally imposes no new #387 performance gate and makes no outlier-removal or production-performance acceptance decision.

Run validator mutation tests with:

```
python3 eng/validation/issue739/sendpump/test_control.py
python3 eng/validation/issue739/sendpump/test_control.py /path/to/completed-pilot
```
