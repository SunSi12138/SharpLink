# Controller managed-allocation footprint appendix

This independent console links the actual current `StreamFlowController` from
`SharpLink.Runtime`. Its assembly name matches the existing benchmark friend
assembly. It changes no production code, defaults, package policy or tests.

The purpose is to measure the managed allocation associated with a fresh
controller and its stream lifecycle, especially the additional `System.Threading.Lock`
in the reviewed D control. It is not an RPC benchmark or a retained-heap/RSS test.

## Build and invoke

Stamp `ModeIdentity.cs` in each verified, isolated source tree before compiling.
All committed values are deliberately unconfigured. The fields match the
existing architecture evidence identity, with one crucial distinction:
`MeasurementSourceSha` must be the SHA-256 of this footprint project's executable
source/project inputs, excluding the generated identity file. Do not copy the
separate RPC harness's measurement digest and call it this project's source hash.

`RuntimeSourceTreeSha` identifies the 40-digit Git tree verified by the collector.
The fixed baseline and baseline/D controller digests are checked against the
reviewed values. Command-line arguments verify the compiled identity; they
cannot relabel a built binary. Retain the actual build/publish directory and all
compiler logs; no warning/analyzer policy is disabled.

```sh
dotnet build test/SharpLink.FlowControllerFootprintEvidence/SharpLink.FlowControllerFootprintEvidence.csproj -c Release -p:PublishAot=false
dotnet publish test/SharpLink.FlowControllerFootprintEvidence/SharpLink.FlowControllerFootprintEvidence.csproj -c Release -r linux-x64 -p:PublishAot=true -o publish
```

Invocation:

```text
<mode> <candidate-sha> <controller-sha256> <output.tsv>
```

The JIT DLL and NativeAOT executable are both named `SharpLink.Benchmarks`.
The result cross-checks the conditional AOT compilation marker against runtime
dynamic-code flags and records the measured binary SHA-256. JIT also records the
actual Runtime assembly SHA-256. A managed invocation does not count as AOT.

## Measurement

Each of five recorded rounds uses 10,000 operations. A complete unrecorded
32-operation run warms all paths first; the single-controller churn also warms
64 send and receive lifecycles before its measured loop.

Thirteen phases, producing 65 TSV rows:

1. Construct one actual `System.Threading.Lock` object.
2. Construct one controller with the unchanged production defaults.
3. Create its first send state/dictionary storage and reserve one full stream window.
4. Probe the exhausted stream without joining the queue.
5. Create an actual asynchronous flow-credit waiter for one byte.
6. Return the original unsent reservation and admit that waiter.
7. Return the waiter's newly admitted one byte.
8. Complete the stream, retaining one pooled send state.
9. Create the first receive state and its dictionary storage.
10. Consume and complete that receive stream, retaining one pooled receive state.
11. Complete the connection, clearing active tables and state pools.
12. Repeatedly create/reserve/return/complete one send stream on a warmed controller.
13. Repeatedly accept/consume/complete one receive stream on a warmed controller.

The wait scenario exhausts the **stream** window; the connection retains spare
credit. It is not a connection-starvation or oversized-item benchmark. No buffers
of the credit size are allocated: this appendix measures the controller itself.

Defaults are read from actual `SharpLinkRuntimeOptions` and checked: 1 MiB stream
window, 16 MiB connection window and 4 MiB maximum frame payload. The current
default concurrent-stream bound is passed unchanged.

Measurement uses `GC.GetAllocatedBytesForCurrentThread` immediately before and
after a synchronous body and verifies the managed thread did not change.
Reference arrays, delegates, reporting and the shared cleanup exception are
allocated outside snapshots. `GC.KeepAlive` preserves measured objects.
A collection/finalizer drain precedes each round.

For the constructor phases, all measured allocations occur on the calling
thread. The D-minus-A constructor delta can be compared with the direct Lock
constructor control. Report actual raw rounds and bytes, not estimated field
sizes or an assumed object-header size.

The waiter phase uses production `AcquireSendCreditAsync`; its completion can
run on ThreadPool threads. Waits are joined outside allocation snapshots.
The return/admit phase includes allocations performed by the caller to schedule
completion, but **excludes allocations on callback threads**. These rows must
not be labeled total per-waiter process allocation. They are useful attribution
controls, while the constructor result is the primary footprint comparison.

## Interpretation and limits

This is allocated managed memory, not a GC retained-heap survey. It does not
measure native synchronization state, OS wait handles created under contention,
working set, real serialized buffers, SendPump queues, transport, codecs, request
lifecycle or RPC throughput. It warms a pool holding one state, not all 128 pool
slots or a maximum-concurrency table.

Fresh state rows include real dictionary backing allocations, whereas warmed
single-state churn reuses existing state and dictionary storage. Keep these
populations separate. Connection `Complete` allocation is measured separately
from constructor allocation and must not be silently subtracted.

The output contains metadata comment rows, a TSV header, all five raw rounds,
and a final `#status passed` marker only after every phase and settlement check
has completed. A failure returns nonzero, writes the exception to stderr, and
leaves any earlier raw TSV rows intact. The external workflow owns process
timeouts and must retain stderr and exit status.

