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
`SharpLink.Benchmarks/obj/Generated` tree for generated file/LOC/byte count and `Invoke*Async`
call sites. Because generated C# relies on type inference rather than spelling generic arguments,
the compiled benchmark assembly is then inspected for actual `IRpcChannel.Invoke*Async` MethodSpec
calls. The report records unique closed-generic instantiations by lifecycle entry point; CLR/JIT
canonical sharing may still reduce the amount of distinct native code.

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
runtime, generated server stub, and response path. OneWay latency is measured at its documented public
local-send completion boundary; response calls are measured to end-to-end completion. For OneWay
methods with client streams, the runner additionally paces every 64 completed local invocations (and
each phase boundary) until the server service has consumed those streams, then completes a tiny Unary
barrier on the same connection before starting the next batch. The pacing runs outside the per-call
latency timestamp, but remains inside process-wide throughput/CPU/allocation accounting. This prevents
the benchmark producer from accumulating completed send-state tombstones faster than peer credit can
return while preserving the public OneWay completion contract.

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

The A/B/C/D attribution workload also runs in PGO OFF, PGO ON, and NativeAOT. A RID-specific restore
is performed before build timing starts; the timed publish is allowed to run its normal no-op restore
phase so .NET 10 can populate NativeAOT runtime-pack items correctly. The full-RPC host records its
own NativeAOT executable size and publish time separately from the synthetic A/B/C/D code-growth
experiment.

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

- Evidence workflow: [run 37731525050](https://github.com/SunSi12138/SharpLink/actions/runs/37731525050).
- Measured head: `b98fa48c37340514a09db5eea53b8fa3373436d3`.
- Artifact: `client-call-shape-defolding-b98fa48c37340514a09db5eea53b8fa3373436d3`
  (SHA-256 `1d679feafff0660f82d6526d3b12ad5754050c1745f893b66a884e30e7a489f3`).
- This results commit changes documentation only. The same workflow must pass again on the final PR head;
  the PR description pins that exact-head rerun before Ready for review.

### Lattice and generated-client inventory

- Cartesian / exact reachable / lifecycle-equivalent: **40,960 / 8,224 / 224**.
- Production entry points and A/B/C/D entries: **5 / 5 / 7 / 112 / 8,224**.
- Current benchmark generated client: **25 files / 13,018 LOC / 638.5 KiB**, with **34** compiled
  closed-generic `Invoke*Async` MethodSpec call sites.

| Entry point | Call sites | Closed generics |
| --- | ---: | ---: |
| `InvokeUnaryAsync` | 18 | 18 |
| `InvokeOneWayAsync` | 5 | 5 |
| `InvokeClientStreamingAsync` | 5 | 5 |
| `InvokeServerStreamingAsync` | 3 | 3 |
| `InvokeDuplexStreamingAsync` | 3 | 3 |

### A/B/C/D prototype and codegen cost

| Variant | Entries | Source LOC | JIT DLL KiB | Payload fields | State B | MoveNext IL B | JIT native B | PGO off ns | PGO on ns | AOT ns | AOT image KiB | AOT build s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| A | 5 | 212 | 18.5 | 9 | 56 | 353 | 558 | 1227.9 | 1317.6 | 521.4 | 1585.0 | 4.1 |
| B | 7 | 245 | 20.0 | 8 | 56 | 330 | 538 | 1259.6 | 1305.8 | 523.9 | 1589.1 | 4.2 |
| C | 112 | 1,537 | 92.0 | 5 | 48 | 249 | 374 | 1163.3 | 1275.8 | 636.9 | 1805.5 | 4.6 |
| D | 8,224 | 88,445 | 6069.0 | 3 | 40 | 213 | 327 | 1166.5 | 1199.9 | 529.0 | 18370.1 | 182.2 |

| Variant vs A | PGO off | PGO on | NativeAOT | JIT DLL | AOT image | AOT build |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| B | +2.58% | -0.90% | +0.47% | +8.11% | +0.26% | +1.95% |
| C | -5.26% | -3.18% | +22.15% | +397.30% | +13.91% | +11.92% |
| D | -5.00% | -8.94% | +1.45% | +32705.41% | +1059.03% | +4333.33% |

C does shrink the representative async state from 9 payload fields / 56 B in A to 5 / 48 B and
reduces representative `MoveNext` IL/native code. That improvement does not survive the cost gate:
C is **22.15% slower under NativeAOT** and grows the AOT image by **13.91%**. D reduces the state
further to 3 fields / 40 B and reaches an **8.94%** best JIT speedup, but rooting 8,224 entry points
grows the JIT DLL by **32,705%**, the AOT image by **1,059%**, and AOT build time by **4,333%**.

### Full SharpLink RPC matrix

The same 24 scenarios run under TieredPGO off/on and NativeAOT. The NativeAOT full-RPC host is
**12,538.5 KiB** and publishes in **36.8 s** after restore. OneWay latency ends at local send
completion; streamed OneWay pacing remains outside that timestamp but inside process-wide
throughput/CPU/allocation accounting.

The table below is a compact per-shape median of the scenario medians. The uploaded `summary.md`
contains every **72 mode/scenario median rows**; the architecture decision uses those complete raw
scenario medians plus the isolated A/B/C/D attribution, not cross-scenario averages.

| Mode | Shape | Scenarios | median QPS | median P50 us | median P99 us | median CPU us/op | median B/op |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| pgo-off | Unary | 9 | 10085 | 96.13 | 149.88 | 333.08 | 976.0 |
| pgo-off | OneWay | 5 | 25607 | 10.82 | 36.20 | 117.49 | 1593.8 |
| pgo-off | ClientStreaming | 4 | 6028 | 160.04 | 244.80 | 607.29 | 3801.6 |
| pgo-off | ServerStreaming | 3 | 6159 | 158.00 | 239.34 | 587.37 | 2509.0 |
| pgo-off | DuplexStreaming | 3 | 5025 | 192.23 | 276.93 | 732.07 | 4732.7 |
| pgo-on | Unary | 9 | 8986 | 109.62 | 156.14 | 373.75 | 976.8 |
| pgo-on | OneWay | 5 | 23438 | 13.98 | 55.08 | 169.02 | 1594.5 |
| pgo-on | ClientStreaming | 4 | 5081 | 191.84 | 271.34 | 689.88 | 3886.5 |
| pgo-on | ServerStreaming | 3 | 5608 | 172.82 | 251.06 | 647.13 | 2488.4 |
| pgo-on | DuplexStreaming | 3 | 4088 | 237.92 | 340.57 | 901.39 | 4874.5 |
| native-aot | Unary | 9 | 11543 | 89.84 | 127.33 | 234.01 | 976.6 |
| native-aot | OneWay | 5 | 44610 | 3.39 | 9.21 | 47.86 | 1577.6 |
| native-aot | ClientStreaming | 4 | 8672 | 112.09 | 168.04 | 345.67 | 3791.6 |
| native-aot | ServerStreaming | 3 | 8860 | 110.23 | 165.18 | 348.20 | 2406.6 |
| native-aot | DuplexStreaming | 3 | 7610 | 128.19 | 188.42 | 399.20 | 4705.0 |

### Gate result and architecture decision

- **Full D: No-Go.** The best prototype speedup is **8.94%**, but AOT image/build cost grows by
  **1,059% / 4,333%**; the declared gate requires at least 5% speedup while both AOT costs remain
  at or below 20%.
- **Selective C: No-Go as a generic policy.** It clears the JIT speed threshold, but not the
  NativeAOT/code-size gate: AOT is 22.15% slower and the image is 13.91% larger than A.
- **Production default: keep the five folded `IRpcChannel` lifecycle entry points.** Static de-fold
  can shrink async state machines, but broad specialization does not justify its code-size/AOT cost.
  Future de-folding should be dimension-specific and require same-machine full-RPC evidence for the
  exact lifecycle/state-ownership fact being specialized.
- No shipping ABI, wire format, runtime entry point, or production behavior changes in this experiment.
