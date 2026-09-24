# Client call-shape de-folding evidence

Issue: #734  
Parent tracker: #728

## Scope and non-goals

This investigation quantifies how much of the generated client call shape should be selected by the
source generator instead of re-tested by the runtime. It does **not** change the shipping
`IRpcChannel` ABI, protocol, wire format, method identity, retry semantics, timeout semantics, or
stream ownership.

The production ABI currently exposes five generated-client entry points:

1. Unary
2. OneWay
3. ClientStreaming
4. ServerStreaming
5. DuplexStreaming

The generated proxy already materializes a `RpcMethodDescriptor` with static method facts. This
experiment measures the upper bound of specializing more of those facts before deciding whether a
production ABI expansion is justified.

## Reachable call-shape lattice

The lattice is generated and self-checked by `eng/client-call-shape-defolding.py`. The maximum
client-stream count is 127 because the generator rejects counts greater than `sbyte.MaxValue`.

The raw Cartesian product is:

```text
5 kinds
* 2 request-payload states
* 4 response states (none / value / required ref / nullable ref)
* 128 client-stream counts (0..127)
* 2 timeout states
* 2 idempotency states
* 2 cancellation-contract states
= 40,960 theoretical combinations
```

Most combinations are illegal. In particular:

- Unary and ServerStreaming have no client-stream parameters.
- ClientStreaming and DuplexStreaming require at least one client stream.
- OneWay has no response payload.
- ServerStreaming and DuplexStreaming always have a stream item response.
- `[Idempotent]` is a Unary retry contract; non-Unary shapes normalize it to false.

The exact reachable lattice is:

| Kind | Exact reachable | 0/1/many stream-equivalent |
| --- | ---: | ---: |
| Unary | 64 | 64 |
| OneWay | 1,024 | 24 |
| ClientStreaming | 4,064 | 64 |
| ServerStreaming | 24 | 24 |
| DuplexStreaming | 3,048 | 48 |
| **Total** | **8,224** | **224** |

The 224-count equivalence lattice collapses client-stream count to `0 / 1 / many`. That is the
relevant ownership split for the generated stream writer: zero has no writer, one can directly await
one pump, and many coordinate multiple pumps. Exact arity still affects generated code and is
therefore retained by the full-D prototype.

## Static-fact inventory

| Static fact | Generator representation today | Runtime/codegen consequence today |
| --- | --- | --- |
| Kind | Generator selects one of five `IRpcChannel` methods | Lifecycle is mostly split, but call-control still reasons about streaming kind |
| Request payload absent | `RpcEmptyRequest` + `RpcEmptyRequestCodec` | Empty-request carrier/codec still flows through the generic entry point |
| Response payload absent | `RpcMethodDescriptor.HasResponsePayload` | Pending response allocation/flags and request flags branch on the descriptor |
| Client streams present | `HasClientStreams`, `ClientStreamCount`, generated `RpcNoClientStreams`/writer | OneWay and streaming paths retain stream/request-id/lease decisions; one-vs-many writer differs |
| Method timeout | `HasMethodTimeout`, `MethodTimeout` | `ResolveCallControlForInvocation` selects timeout/deadline work |
| Idempotent | `IsIdempotent` | Unary chooses the optional retry path; non-idempotent methods cannot retry |
| Response nullable | `ResponseNullable` | Pending unary and stream dispatcher setup retain nullable-response state |
| Cancellation contract | Proxy passes the declared token or `default` | Runtime still observes `CancellationToken.CanBeCanceled` and resolved deadline state |

Request-payload absence is intentionally left dynamic in C because it is mainly serialization/carrier
shape rather than lifecycle/state ownership. It is still fully specialized in D. This keeps C aligned
with "all static facts that change control flow/state ownership" while D remains the exhaustive
experiment.

## A/B/C/D prototypes

The evidence script generates four standalone async state-machine probes from the same reachable
lattice. Every probe executes the same seven representative legal shapes.

| Variant | Entry points | Selection |
| --- | ---: | --- |
| A | 5 | Current major lifecycle shape only |
| B | 7 | A + Unary retryable/non-retryable + OneWay no-stream/stream |
| C | 112 | All control-flow/state-ownership facts; request-payload and exact `many` arity remain dynamic |
| D | 8,224 | Every exact reachable shape, including request payload and exact client-stream count |

The generated methods force one async suspension and consume the selected facts after the suspension.
That makes the experiment sensitive to state-machine payload fields, `MoveNext` IL/native code,
runtime branches, and NativeAOT rooting. It is an attribution prototype, not a synthetic RPC latency
claim.

Before the A/B/C/D probes run, the harness also inventories the real
`SharpLink.Benchmarks/obj/Generated` tree: generated file/LOC/byte count, total generated
`Invoke*Async` call sites, and unique emitted closed-generic `Invoke*Async<...>` spellings by
lifecycle entry point. That is an emitted-source generic-instantiation proxy; CLR/JIT canonical
sharing can reduce the native instantiation count.

For each variant the harness records:

- generated source lines and bytes;
- JIT assembly IL size;
- representative async state-machine field count and managed struct size;
- representative `MoveNext` IL bytes;
- JIT `MoveNext` native bytes from `DOTNET_JitDisasm` (used as the hosted-runner
  instruction-footprint/locality proxy; no PMU i-cache counter is claimed);
- identical-workload latency/allocation with TieredPGO OFF and ON;
- identical-workload NativeAOT latency/allocation;
- NativeAOT executable size;
- NativeAOT publish time.

All generated probe methods are rooted for NativeAOT, so D pays for its full 8,224-entry code surface
rather than measuring only the seven methods exercised at runtime.

## Full SharpLink RPC matrix

`ClientCallShapeEvidenceRunner` uses the real generated proxy, client runtime, TCP transport, server
runtime, generated server stub, and response path. OneWay is measured at its documented public
local-send completion boundary; response calls are measured to end-to-end completion.

The matrix contains:

### Unary

- idempotent value response, no timeout;
- non-idempotent value response, no timeout;
- idempotent value response with method timeout;
- explicit cancellable value response;
- no request payload;
- no response payload;
- required reference response;
- nullable reference response;
- realistic 4 KiB byte payload.

### OneWay

- no client stream, no timeout;
- no client stream with timeout;
- explicit cancellation contract;
- one client stream;
- two client streams with timeout.

### Streaming

- ClientStreaming one stream;
- ClientStreaming two streams;
- ClientStreaming explicit cancellation;
- ClientStreaming 4 KiB payload;
- ServerStreaming value response;
- ServerStreaming explicit cancellation;
- ServerStreaming 4 KiB payload;
- Duplex required-reference response;
- Duplex explicit cancellation;
- Duplex 4 KiB payload.

Each scenario runs three repetitions. Odd/even repetitions reverse order and alternate PGO
OFF/ON first to reduce monotonic host drift. The report uses per-scenario medians.

## JIT and NativeAOT modes

The full SharpLink RPC matrix runs from `SharpLink.CallShapeAotEvidence`, a minimal host that
links the exact same contract, service, and `ClientCallShapeEvidenceRunner` source used by
`SharpLink.Benchmarks`. The same 24 scenarios therefore run in all three modes:

- `DOTNET_TieredCompilation=1`, `DOTNET_TieredPGO=0`;
- `DOTNET_TieredCompilation=1`, `DOTNET_TieredPGO=1`;
- NativeAOT `linux-x64`.

The A/B/C/D attribution workload also runs in PGO OFF, PGO ON, and NativeAOT. NativeAOT restore is
performed before build timing starts, so package acquisition is not charged to A. The full-RPC host
records its own NativeAOT executable size and publish time separately from the synthetic A/B/C/D
code-growth experiment.

## Predeclared decision gates

The gates are declared before collecting PR evidence so the conclusion is not selected after seeing
the numbers.

### Full D

A full reachable de-fold is a candidate only if:

- best observed prototype speedup across PGO OFF / PGO ON / NativeAOT is at least **5%**; and
- both NativeAOT executable growth and NativeAOT build-time growth remain at or below **20%**
  relative to A.

Failing either side is a No-Go for full de-fold.

### Selective C

C is a candidate for targeted production experiments only if:

- best observed prototype speedup is at least **3%**; and
- NativeAOT executable growth is at or below **10%** relative to A.

Even if C clears the prototype gate, it does not authorize a 112-method public ABI. The profitable
dimensions must still be tested as internal/generated specialization against the real runtime.

## ABI interpretation

If the prototype entry count were naively mapped one-to-one onto public `IRpcChannel` methods, the
surface would grow from 5 to:

- B: 7 methods;
- C: 112 methods;
- D: 8,224 methods.

That expansion is **not** part of this branch. A future selective implementation can instead use
internal runtime helpers or generated code while keeping the stable public generated ABI small.
Any public ABI proposal must separately justify compatibility and generated-assembly dependency
cost.

## Reproduction

On Linux with the repository's pinned .NET SDK:

```bash
bash eng/run-client-call-shape-defolding-evidence.sh \
  artifacts/performance/client-call-shape-defolding
```

The script fails on:

- lattice/count drift;
- Python or shell harness errors;
- benchmark project build failure;
- missing JIT native-code evidence;
- NativeAOT publish/run failure;
- RPC validation mismatch;
- missing evidence files.

The PR workflow `Client Call Shape De-folding Evidence` runs the same command on the exact PR head
and uploads the full evidence directory.

## Results

The exact-head CI artifact is the source of truth. Before the PR is marked Ready for review, this
section is updated with:

- exact head SHA and workflow run;
- A/B/C/D source/IL/state-machine/JIT native metrics;
- TieredPGO OFF/ON and NativeAOT prototype measurements;
- NativeAOT image/build deltas;
- full SharpLink RPC medians;
- the gate result and final folded/selective/full conclusion.

No result is inferred from an older branch or a different host.
