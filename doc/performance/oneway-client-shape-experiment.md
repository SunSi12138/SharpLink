# OneWay client-shape specialization (#729)

## Status and decision rule

Investigation in progress. No production Go decision has been made.

The issue originally required measurable full-RPC improvement. The owner clarified on
2026-10-09 that a clear, repeatable **local** performance improvement with a modest
complexity increase is sufficient; an end-to-end gain need not be measurable. This
investigation follows that updated criterion. Full-RPC tests remain correctness and
regression checks, not a mandatory positive-speedup gate.

No fixed percentage was supplied by the owner. Earlier proposed 5% full-RPC and 3%
B-over-C screens are superseded before measurement. A decision must consider effect
size against measurement spread, repeatability across independent processes, actual
production work, state-machine/codegen evidence, allocations, and maintenance cost.
An inconclusive measurement is not evidence of a gain or a regression.

## Frozen inputs and arms

- Production baseline A: `553854f56c33a6e02ef225c24f558e5e92037aa1` (`dev`).
- C: apply `eng/perf/oneway-shapes/internal-split.patch` to A. Preserve public and
  generated ABI. Keep one non-async `HasClientStreams` dispatch and use separate
  plain and streaming core state machines.
- B: apply `eng/perf/oneway-shapes/static-entries.patch` to A (already includes C).
  Generated plain calls use `InvokeOneWayAsync<TRequest>`; streaming calls use
  `InvokeOneWayStreamingAsync<TRequest,TStreams>`. Retain the old generic member
  and provide default interface fallbacks for existing custom channels. Metadata
  and dynamic-module wrappers forward new entries without losing their ownership
  or cancellation semantics.

B deliberately reuses the existing telemetry/interceptor plumbing, which reaches
C's internal dispatcher. It tests the cost/benefit of the ordinary generated-entry
boundary without duplicating interceptor state types or changing mutation semantics.
It is not a claim to completely static-specialize every instrumentation path.

Every run must record baseline SHA, harness SHA, candidate patch hashes, SDK/runtime,
OS, architecture, CPU, runtime knobs, command line and exit status. A/C/B must use
identical harness source and build settings. Candidate patches are experimental
inputs, not automatically production changes.

## Current call graph and ownership

`RpcGenerator.ProxyEmitter.AppendProxyMethod` emits an `IRpcChannel` call with a
concrete request and stream writer. For a plain OneWay it still materializes the
zero-sized `RpcNoClientStreams` writer. The channel selects telemetry/interceptor
wrappers as configured and ultimately invokes the shared generic
`SharpLinkClient.InvokeOneWayCoreAsync<TRequest,TStreams>`.

The shared core checks `RpcMethodDescriptor.HasClientStreams` for request flags,
request-ID/pending-lease registration, producer cancellation, publication-table
selection, pre-send cancellation, untracked-call ownership, the emission-deadline
wait, producer execution, terminal arbitration, lease observation and final cleanup.

Plain OneWay owns an untracked call and, with a deadline, an explicit emission race.
Streaming OneWay owns a pending lease and its producer token/publication barrier;
that owner arbitrates local completion, cancellation, deadline and connection failure.
C removes the other shape's state/branches from each async method, leaving all of
those ownership boundaries unchanged. This does not change Protocol v2 or #399.

The baseline's early `!PendingCalls.Contains(requestId)` branch after stream
registration is preserved in all arms. Any separate concern about observing the
pooled operation on that race is not silently fixed in only one performance arm.

## Measurement plan

1. Characterize exact closed state-machine field lists, managed struct sizes,
   entry/MoveNext IL bytes and conditional branches, including generated 0/1/2
   stream writer/request types. Record aggregate code growth, not only the smaller
   individual method.
2. Benchmark actual generated client invocation on a connected in-memory test
   transport, with observable serialized request/stream completion counts. Include
   synchronous completion and deliberately suspended controls. Do not substitute a
   branch-only toy or time a discarded/unobserved ValueTask.
3. Run paired independent warmed processes in balanced A/C/B order. Retain every
   attempted run, failure and raw sample. Report dispersion and paired changes;
   don't discard unfavorable samples or silently retry them into the final result.
4. Run tiny full-RPC TCP/SharedMemory with 0/1/2 streams, idle/wake and bounded
   throughput loads. Count server-completed, payload-validated work rather than
   local queue admission. Use identical completed work, warm/drain/settle boundaries
   and enough measured duration for CPU accounting.
5. Check deadline on/off, cancellable/non-cancellable token, interceptor, telemetry
   and endpoint admission individually and together. Success-path deadlines must
   not expire; terminal/race tests separately check expected failures.
6. Capture JIT disassembly and NativeAOT image/code size and compile time. Run the
   native full-RPC sanity cases. Label any missing or unavailable evidence rather
   than treating a build as a runtime pass.
7. Run focused ownership/terminal tests, generator tests, source/ABI guards and
   exact-head CI. Independent review is required before any production promotion.

Prefer C if it captures the useful local benefit without expanding ABI. B requires
specific additional evidence commensurate with its extra interface entries, wrapper
maintenance and generated/native code. No merge, release or issue closure is part of
this investigation's authorization.

## Initial characterization (not a timing claim)

All sizes below are managed **state-machine struct** bytes, not a promise of the
same per-call heap allocation delta. Heap boxing only occurs on suspension, and
must be measured separately. Linux x64, .NET 10.0.2/SDK 10.0.102, Release:

| Closed request/stream shape | A struct bytes | C struct bytes |
| --- | ---: | ---: |
| Empty request, no stream | 240 | 192 |
| Scalar request, no stream | 248 | 192 |
| Reference request, no stream | 248 | 192 |
| Four-long request, no stream | 272 | 216 |
| Scalar request, one stream | 256 | 248 |
| Scalar request, two streams | 272 | 264 |

The unified core has 16 fields for the empty/no-stream closure; C's plain core
has 11, and its streaming core has 15. MoveNext IL changes from 1781 bytes to
986 bytes (plain) or 1092 bytes (streaming). Counting both C methods, IL grows
by 297 bytes (16.7%); the additional synchronous dispatcher also has a cost.
Conditional-only IL branches change from 42 to 23/18. Counting all branch-flow
instructions, including unconditional branches/switch, the counts are 72 to
41/29. The 13 shape-getter reads disappear from the async cores, while the
non-async dispatcher retains one.

Diagnostic FullOpts JIT captures (tiering and ReadyToRun disabled) report
MoveNext native bodies of 3946→2337 bytes for no streams, 4035→2588 for one,
and 4040→2634 for two in the generated scalar fixture. These short diagnostic
runs establish code shape, not latency or throughput benefit. C's public API
surface hashes match A for both Client and Abstractions.

Independent differential correctness checks passed all 58 focused unit tests
on both A and C. C additionally passed 25 applicable TCP/drain/race/interceptor
integration cases locally; the SharedMemory integration case was blocked by
sandbox AF_UNIX permissions before connection. Hosted CI must supply that
coverage. Two new descriptor-shape ownership tests pass on C. Local builds used
cached packages with NuGet audit disabled; hosted CI must use normal audit.
