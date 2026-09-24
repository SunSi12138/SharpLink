# Ready-writer CI evidence and failure attribution

## Published cumulative baseline

Commit `8d16888f3043bc886644d95725c2b73520df3e85` publishes the previously
local ready-writer, prepared-byte-budget, shutdown cleanup and event-driven
deadline-test increments on #742. Default source tree:
`bf27e65c64029d60cdf6984809d841757ae7d657`. The disposable measured runtime tree
is `b62c5536a05deb67748c4c396982559e0b678c73`. The predecessor is `fa6cc88f`;
its production drain/detach fix remains unchanged. This increment does not
enable B3 in the shipping request loop.

The minimal NativeAOT host links the same transport/ready-writer sources and
uses source-generated JSON with reflection serialization disabled. The native
report requires `DynamicCodeSupported=false`, the exact tree, an executable
SHA256, four-CPU affinity, all expected cases and successful process exits.
A managed test of the host is not a NativeAOT performance result.

## First exact-head NativeAOT result

Run https://github.com/SunSi12138/SharpLink/actions/runs/35989703412, attempt 1:
Native build, self-checks and all eight transport processes succeeded.
Artifact 10803003834 SHA256:
`edf82243befbb8929b029e637a56c311a1997c1a056f5a658b32fbb058c00f67`.
All 128 samples were independently revalidated with the committed verifier.

Every row below is c128, quantum 16, 16 preparation slots and the same 8 KiB
prepared-byte cap on A-ready and B3-ready. Tiny cases use 2048 items/stream,
8 KiB stream and connection windows; large cases use 128 items/stream,
8 KiB stream and 512 KiB connection windows. Flush threshold: 16 KiB.
Two AB/BA process launches, four measured rounds/mode in each. Ratios are
ratios of medians, not summed percentages. CPU includes both endpoints.

| Transport | Item bytes | A items/s | B3 items/s | Throughput delta | CPU delta | Allocation delta B/item | Separate launch throughput deltas |
|---|---:|---:|---:|---:|---:|---:|---|
| SharedMemory | 16 | 1112265 | 1782273 | +60.24% | -42.70% | -121.413 | +60.38%, +61.01% |
| TCP | 16 | 1139487 | 1973810 | +73.22% | -45.36% | -141.109 | +71.06%, +80.14% |
| SharedMemory | 4096 | 143587 | 142652 | -0.65% | +2.38% | -426.957 | -0.07%, -0.44% |
| TCP | 4096 | 92132 | 92677 | +0.59% | +0.13% | -394.267 | -0.90%, +3.59% |

The artifact also retains quantum-one controls. Positive throughput is faster;
positive CPU/allocation is worse. Absolute native/JIT timings are not pooled.
These are balanced-wire fixed-lifecycle transport controls, not full RPC,
universal speedup, cold/retained-allocation or production Go evidence.

## Failures that remain part of the record

The same initial head was not CI-green:

* PR Fast run 35989703452: 1896/1897; the retry endpoint-reset test observed
  `ChannelClosedException` while waiting for the third request on the first
  connection. A single same-SHA verification attempt later passed. This is
  not a root-cause fix, and the first failure is retained.
* Ready-writer JIT artifact 10803309812: a c128 TCP/4096-byte/B3 warmup timed
  out at 15965 received items with exactly 65392640 returned bytes. The
  predeclared matrix was incomplete. It is not an accepted JIT report.
* Older matched B2 transport artifact 10803098830: TCP/4096-byte B2-adaptive
  warmup timed out at 6914 received items and 28319744 returned bytes.

One same-SHA verification attempt was run, not a retry-until-green loop.
PR Fast and the older B2 transport control then succeeded; neither is called
a root-cause fix. The ready-writer JIT matrix failed again, this time on
A-ready TCP/4096-byte (round 1, count-only preparation), at 13183 items and
53997568 returned bytes. Its second artifact 10808390739 has SHA256
`6f52cc160dec2b661f75870668f1118fd48898f2b0f76881f71deaa00c74a6b2`.
Both failed attempts and their incomplete populations remain retained.

The timeouts occur in different credit paths sharing the transport case.
That is a diagnostic lead, not proof of a TCP, SendPump, queue or lock defect.
Diagnostic local repeats did not establish a cause. Their timings are not
performance evidence. No timeout, assertion or required population is relaxed.

## Failure arbitration increment

The case previously canceled peers in a participant's catch block, then
re-threw. The aggregate waiter also used that cancellation token. Consequently
it could observe `TaskCanceledException` before the actual producer/parser
fault reached `Task.WhenAll`, obscuring the useful error.

`PhaseBTransportFailure` records the original exception and its owner before
canceling peers. It joins publication of synchronous cancellation-callback
failures and retains them as secondary errors. A canceled sibling cannot
replace the first cause; pure external/timeout cancellation remains a failure.
Session disconnects feed the same arbiter; normal disposal unsubscribes first.
Cold failure output includes session connectivity and unfinished task indices.
There are no background diagnostic timers, per-item counters or changed credit,
queue, flush, deadline or transport policies. The first cause is diagnostic
information, not evidence that the original stall is fixed.

Seven deterministic checks exercise first-cause ownership, session termination,
throwing cancellation callbacks, concurrent callback publication, external
cancellation, racing faults and success. They run in both managed and native
hosts. Workflow artifact names now include `github.run_attempt`, retaining
failed attempts with unambiguous attempt labels instead of combining evidence.

New-head CI is required after this increment. Keep #735 open and #742 Draft:
dynamic lifecycle, original waiter FIFO equivalence, duplicate/clamped wire
updates, production cancellation/deadline, receiver batching, full-duplex RPC
and the remaining allocation/performance matrix are not accepted yet.
