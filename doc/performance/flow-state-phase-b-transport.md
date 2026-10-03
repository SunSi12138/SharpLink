# Phase B: same-path TCP / SharedMemory control

This continues #735 from `4c17a3892b60496fb31589142010004333b2e891`.
The input source tree is `b5061cacb7bfb5a40c2cd282b11d9441e97df7b8`.
The shipping `src/` files, Phase A controller, protocol and default windows are
unchanged. This is not a drop-in B2 controller or permission to merge #742.

## Reference designs and the remaining architectural difference

Netty documents a single-thread-owned HTTP/2 remote flow controller:
https://netty.io/4.2/api/io/netty/handler/codec/http2/DefaultHttp2RemoteFlowController.html

Kestrel's connection writer queues stream-owned `Http2OutputProducer` objects,
tracks the connection window, and drains producer buffers under stream-window
and connection-window limits. It does not simply put one completion-bearing
credit command on a new queue for every item. It also retains dedicated locks;
"single owner" is not a claim of zero synchronization everywhere:
https://source.dot.net/Microsoft.AspNetCore.Server.Kestrel.Core/Internal/Http2/Http2FrameWriter.cs.html

Our B2 grant owner and the existing SendPump remain separate execution owners.
Co-locating credit scheduling with the connection writer / scheduling ready
streams is a next integration hypothesis, not an implemented result here.
The stress results below do not invalidate either external design.

## New matched path

`--phase-b-transport-evidence` runs four modes in the existing benchmark host:
A (the unchanged, resolved Phase A controller), B1 (per-item owner queue), B2
(fixed 4 KiB grant), and B2-adaptive (bounded fair-share extras).

Every mode creates the same real TCP or SharedMemory connection pair and runs:

```
credit admission -> actual sender SendPump -> StreamData frame
-> parser -> production StreamManager + consumption accounting
-> actual receiver SendPump -> key-only WindowUpdate frame
-> parser -> selected send-credit authority
```

Every mode permits ONE unsettled emission per stream. Sender built-in flow
negotiation is disabled for every mode to avoid double debit; the external A
or B controller supplies send credit. The receiver uses the same negotiated
production controller. Parameters are set locally, not by a full handshake
exchange. Generated RPC invocation, logical-call deadlines and unrestricted
production pipelining are not part of this measurement. Both sessions use the
same explicit `RpcSessionFlushOptions(1, TimeSpan.MaxValue)`: a one-byte size
threshold with no timed batching delay. This is not the default profile batching
policy; full production batching still needs a separate comparison.

Data sequence, identity and all payload bytes are checked. Actual peer-returned
credit must equal actual data bytes. Parser/producer failures cancel the case;
failed/incomplete output and exit status are retained. All grant-model debt,
pending publications and waiters must settle; Close must return the full
connection window and retire every state. Balanced updates are required:
excess/duplicate-credit compatibility is still the explicitly unresolved
policy from the prior wire-boundary control, not silently fixed here.

The measured loop includes serialization, both transport endpoints, emission
settlement, receive validation and credit return. Connection/stream setup,
final model Close and cold/retained allocation are outside timing/allocation.
CPU is process-wide CPU time for BOTH endpoints. Allocation is process-wide
managed allocation and is NOT zero. Producer completion spread is retained
but is NOT a waiter-level fairness or tail-latency proof. Internal Channel,
Lock and runtime RMWs and hardware instructions remain unmeasured.

## Adaptive extras and pressure coalescing

Only an owner-side refill's optional extra grant is limited by:

```
min(configured grant, connection window / (2 * retained state count))
```

The count includes retained tombstones; it is a conservative share, not a new
wire limit. The whole required item is always admitted or waits. Oversized
borrow/repay is not fragmented or weakened. Existing grants can exceed a new
share when streams are registered later; the existing pressure/fairness path
reclaims them. No extra connection-wide RMW enters the local item path.

Under adaptive pressure the owner publishes the freeze flag before the first
reclamation sweep. Later waiters do not repeat the same sweep while local
admission is frozen. Late unsent refunds/Close retain their own owner commands
and required credit reclamation. Ten added checks preserve FIFO, cancellation,
stream-blocked-head skipping, oversized debt, writer pins, actual pool reuse,
and 4096 balanced fixed/adaptive differential operations. The old 64 checks
remain. The fixed/B1 controls intentionally retain repeated sweeps.

## Local evidence: do not combine the two source versions

Stage 1 (adaptive cap): measured source tree
`abfaa417bd2733986bec5d8a7d59d915e537d89e`, 36 reports / 1152 rows. It covers
c1/8/32/128, 16 B, 4 KiB controls and a tight 8 KiB connection window.
Stage 2 (also coalesces pressure sweeps): measured source tree
`427e6ae184b2073036248b92a9a5866d8f0cccbe`, 12 reports / 384 rows. It repeats
c128/16 B with two independent launches and the tight-window control. Stage 1
large-item and small-concurrency numbers are NOT new Stage 2 measurements.

Each report has all four modes, eight cyclic/reverse-ordered rounds after
warming every mode through the actual path (at least two passes and one second).
The high-concurrency long-stream cases have two process launches. The primary
statistic is the ratio of per-mode median items/s, not median percentage change.
Per-launch medians, geometric means, CPU, allocations and raw rounds are retained;
no favorable process is selected. The local host had four CPU-equivalents,
affinity CPUs 0-3, .NET 10.0.12, SDK 10.0.111, PGO ON/OFF and ReadyToRun off.
These are exploratory local results without confidence-interval acceptance.

Final Stage 2 c128, 1024 items/stream, 16 B items, explicit 8 KiB stream /
512 KiB connection windows, adaptive versus A:

| Transport / PGO | A items/s | Adaptive items/s | Throughput | CPU time/item | B/item delta |
|---|---:|---:|---:|---:|---:|
| TCP / ON | 242428 | 288730 | +19.10% | -16.28% | +1.084 |
| TCP / OFF | 250128 | 238462 | -4.66% | +7.46% | -0.061 |
| SharedMemory / ON | 1353728 | 1359982 | +0.46% | -6.74% | -0.623 |
| SharedMemory / OFF | 979571 | 1098051 | +12.10% | -10.46% | -1.304 |

Owner commands including actual WindowUpdates fall from fixed B2's
0.2904-0.3960/item to adaptive 0.015625/item in these cases. A's zero owner
commands means no QUEUE, not no locks. Stage 2 TCP/PGO ON's independent
launch median ratios are +27.56% and +3.66%, while the second geometric ratio
is -3.69%. TCP/PGO OFF launches have opposite signs. Lower coordination is
reliable in these rows; stable universal speed improvement is not established.

Important negative evidence: at c128, 256 items/stream and an 8 KiB connection
window, final adaptive SharedMemory throughput is -84.53% (PGO OFF) / -85.43%
(PGO ON) against A. Commands rise back to about 1.48/item. Sweep coalescing
removes redundant traversals but does not solve owner queue/continuation costs
when grants cannot amortize admission. Profiling has not attributed the whole
regression. Stage 1's 4 KiB controls also regress, including TCP/c128/PGO ON
-26.03%; those are retained separately rather than extrapolated to Stage 2.

## Checks and current blockers

Final research/benchmark/unit builds: 0 warnings/errors. Model checks: 74/74.
Existing flow tests 54/54, writer boundary tests 6/6, wire boundary tests 10/10.
Python guards: 14 + 7 + 9 passed. New workflow: two Bash blocks parse correctly;
project reference boundary and offline maintainability pass unchanged budgets.
Local offline restore used NuGetAudit=false; it is not an online audit.

The Stage 1 complete UnitTests run passed 1893/1893, exit 0. The final Stage 2
complete run passed 1892/1893, exit 2: the unchanged
`TimedDuplexStreamingShouldLeaveNoFrameworkTaskWhenTheDeadlineWinsAStalledWrite`
failed its accepted-head-frame assertion. It is NOT claimed fixed, unrelated
by proven root cause, or replaced by the earlier passing run. No test was
removed or loosened. The previous unpushed StreamManager notification patch
is not included in this worktree.

NativeAOT publish was attempted locally but failed on missing official runtime
packs. No new AOT transport results or remote CI runs exist for this increment.
A new read-only JIT workflow runs the full 36-report matrix after publication;
it is configured, not yet executed. GitHub was not connected during this turn.

## Reproduce after applying and committing the patch

```
python3 eng/test-flow-state-phase-b.py
python3 eng/test-flow-state-fused.py
python3 eng/test-flow-state-transport.py
dotnet run -c Release --project test/SharpLink.FlowStatePhaseB -- --self-test
dotnet build -c Release test/SharpLink.Benchmarks
python3 eng/run-flow-state-transport.py --output artifacts/transport --pgo 1 --launch 1 --transport tcp --group high
python3 eng/verify-flow-state-transport.py artifacts/transport
```

The runner refuses a dirty worktree or overwriting an existing report. The
workflow includes the omitted modes/launches/transports/negative groups and
requires all 18 reports per PGO job. More production work remains: integrate
ready-stream scheduling with writer ownership; reconcile wire permissions with
receipt debt; preserve full multi-handle/cancellation/capacity contracts and
receive batching; then measure complete production RPC and NativeAOT paths.
