# UnsafeBlit padding security assessment

This document records the security decision for issue #269. It is deliberately separate from the cross-runtime compatibility matrix in #253/#265: byte compatibility and confidentiality are different questions.

## Decision

For the current 2.x raw-managed-representation contract, **retain `UnsafeBlitCodec<T>` and document/constrain its security contract; do not add automatic padding canonicalization to the production hot path in this change**.

The codec is intentionally an ABI-sensitive raw blit fallback. Its wire representation includes every byte in `Unsafe.SizeOf<T>()`, including padding. Therefore padding is part of the observable raw representation even though it is not part of the logical field value.

This decision is safe only with the following contract:

- representative ordinary managed-construction controls produced zero padding on the tested runtimes, but zero-valued padding is an observed implementation behavior here, **not** a C#/.NET representation guarantee and not a supported sanitization mechanism;
- values populated through unsafe code, `stackalloc`, `Unsafe.SkipInit`, skipped local initialization, native interop, pooled/native buffers, or other mechanisms that can leave non-field bytes tainted must be treated as potentially carrying those bytes onto the wire;
- when such a value crosses a confidentiality boundary, explicitly bind that payload type to a non-`UnsafeBlitCodec<T>` custom/adapter Codec, or explicitly sanitize the complete source representation before serialization;
- SharpLink does not promise zero padding or canonical wire bytes for padding-bearing raw-blit structs, regardless of how their logical fields were initialized.

Issue #311 tracks a compile-time routing extension to make this escape path practical at Contract scope without adding runtime codec-selection cost. Its planned `Unmanaged` route means “for payload types that SharpLink would otherwise resolve to `UnsafeBlitCodec<T>`, bind the selected third-party adapter at Contract compilation instead.” Explicit per-type `RpcCodec` / `RpcCodecAdapter` / serializer-selector bindings remain higher priority than such a route. Until #311 is implemented, callers that need to leave the raw-blit domain should use the existing explicit per-type binding mechanisms.

A future compatibility decision may still choose stronger raw-blit restrictions or opt-in semantics, but that should remain separate from this evidence PR.

## Threat model

### What the codec reads

`UnsafeBlitCodec<T>.Serialize` requests `Unsafe.SizeOf<T>()` bytes and performs one `Unsafe.WriteUnaligned` of the complete managed representation. It does not read beyond the struct representation. The issue is therefore not an out-of-bounds read of adjacent memory; it is propagation of bytes already present inside the representation's padding.

### When padding can contain non-logical state

In the tested Linux x64, Windows x64, and macOS ARM64 controls, ordinary `default`/`new` construction followed by field assignment produced zero bytes at the known padding offsets. That is empirical evidence about these representative executions, not a language/runtime contract for padding. C#/.NET define the logical value of the fields; padding bytes are not logical fields and SharpLink must not promise that every JIT/AOT/runtime/copy path preserves them as zero. Consequently, constructing `new T()` or assigning all logical fields must not be treated as a confidentiality-boundary sanitizer for a raw representation.

The confidentiality risk is clearest when callers construct or receive a struct through mechanisms that can preserve arbitrary non-field bytes. Representative examples include:

- a struct reinterpreted over uninitialized or reused stack/native storage;
- `Unsafe.SkipInit` or skipped local initialization followed by assignment of only logical fields;
- native/P/Invoke or memory-mapped input whose padding is not normalized;
- pooled/native buffers reused for struct-shaped storage;
- any unsafe helper that writes fields but intentionally does not clear the whole representation.

In those cases, the raw blit can move padding across an RPC/process/network boundary. Repeated serialization can therefore expose a small number of source-storage bytes per value. This is a **real but conditional information-disclosure primitive**, not merely a canonicalization concern.

The tested ordinary managed-construction controls did not expose non-zero padding, while unsafe/native/uninitialized provenance demonstrably can. The latter therefore carries materially higher confidentiality risk. The former observation should not be elevated into a guarantee that padding is always zero.

## Reproduction coverage

The `Codec Padding Security Evidence` workflow runs the actual internal `UnsafeBlitCodec<T>` from `SharpLink.Runtime` on three representative release environments:

- Linux x64 / CoreCLR / .NET 10;
- Windows x64 / CoreCLR / .NET 10;
- macOS ARM64 / CoreCLR / .NET 10.

Each job first invokes the dedicated `--unsafe-blit-padding-evidence` CLI mode. That process constructs two logically equal values over backing storage poisoned with different bytes, assigns the same logical fields, serializes through the production codec, and asserts that every differing wire byte is exactly a known padding byte. Any failed assertion escapes the CLI process and produces a non-zero `dotnet run` exit before BenchmarkDotNet starts. BenchmarkDotNet is used only for performance measurements and is not the security assertion gate.

Fixtures cover more than the original `ByteInt32` and `Int64Byte` controls:

| Fixture | Purpose | Expected padding on the tested 64-bit layouts |
| --- | --- | --- |
| `ByteInt32` | internal alignment gap | 1-3 |
| `ByteInt64` | wider internal alignment gap | 1-7 |
| `Int64Byte` | tail padding | 9-15 |
| `ByteDouble` | floating-point alignment gap | 1-7 |
| `NestedPadding` | nested internal + outer tail padding | 1-3, 9-11 |
| `ExplicitGap` | explicit layout with an intentional hole | 1-3 |
| `PackedByteInt32` | `Pack=1` no-padding control | none |

The explicit-layout fixture is important: **requiring `LayoutKind.Explicit` alone does not remove disclosure risk** because explicit layouts can still contain holes.

The workflow also asserts that zeroing only the known padding offsets makes the two poisoned wires identical and records whether the ordinary managed-construction control happens to contain zero at those offsets on each tested runtime. That control is evidence, not a contractual assertion about all .NET executions.

Evidence snapshot `32627839187` passed on all three platforms. The original workflow metadata identified source head `50996b10f3d365c3c8b64cc458018ed5e1ab1563`, while the default `pull_request` checkout actually executed synthetic merge commit `0524ff554034d2ba7605533ba093142a1f83c244` against base `75bc3815454329233fe19de029c33b1c340721d3`. That historical provenance mismatch is recorded explicitly rather than hidden. The workflow now records the actual checked-out `github.sha` separately from the PR source-head SHA.

A permanent machine-readable copy of the reviewed snapshot is checked in at [`evidence/unsafe-blit-padding-32627839187.json`](evidence/unsafe-blit-padding-32627839187.json). It retains the three poison reports, the BenchmarkDotNet mean/error/stddev/ratio summaries, artifact IDs and SHA-256 digests, and the recovered checkout/base provenance so the evidence remains inspectable after Actions artifacts expire.

For every fixture in that snapshot, observed poisoned-wire differences exactly matched the expected padding offsets; the representative ordinary managed-construction controls observed zero at all known padding offsets; and zeroing only those padding bytes made the poisoned wires identical. The packed control produced no differing bytes.

## Candidate mitigations

### 1. Post-blit zero/canonicalize known padding

Security effect: strong for padding disclosure when the padding map is correct; it also makes equal logical values canonical with respect to padding.

Cost/complexity: the benchmark models the lower bound by keeping the padding map already known/cached, performing the same raw write, then clearing the known padding ranges. Generic production use would additionally need a runtime/AOT-safe way to derive and validate managed-layout padding across nested, explicit, native-width, and runtime-specific layouts.

The benchmark deliberately excludes padding-map discovery cost. Therefore its reported overhead is a lower bound for a generic runtime implementation, not an upper bound.

### 2. Require explicit layout

Security effect: insufficient. `ExplicitGap` proves that explicit layout can still contain wire-visible holes.

### 3. Require `Pack=1`

Security effect: removes alignment padding for simple sequential fixtures such as the included packed control.

Compatibility/performance cost: changes the user's managed/native ABI and can introduce unaligned field access. It is not a reasonable blanket requirement for an RPC library.

### 4. Explicit per-type Codec/Adapter binding

Security effect: removes the selected payload type from the SharpLink `UnsafeBlitCodec<T>` domain when the chosen custom Codec/Adapter uses an appropriate representation.

Current availability: SharpLink already supports compile-time per-type custom Codec and Adapter/selector bindings. This keeps serializer ownership in the Contract and does not require Client/Server runtime `UseCodec<T>` configuration.

Limitation: it is repetitive when many payload types need the same third-party serializer policy.

### 5. Compile-time payload-scope routing (#311)

Security effect: planned `RpcCodecScope.Unmanaged` routing can move every wire-reachable payload that would otherwise resolve to SharpLink `UnsafeBlitCodec<T>` to a selected `IRpcCodecAdapter` during Contract compilation.

Design constraint: route selection is compile-time only. The generated Manifest/ContractCodecSet records the final binding, so Serialize/Deserialize should not gain route lookup, reflection, allocation, or per-call branching. Explicit per-type bindings override the broader route rather than conflicting with it.

Scope: #311 also plans `Managed`, `Unmanaged`, and `Native` flags so the mechanism is a general codec-routing feature rather than an UnsafeBlit-specific switch.

Limitation: SharpLink can guarantee only that the selected payload no longer uses SharpLink `UnsafeBlitCodec<T>`; a third-party serializer can still choose its own raw-copy representation internally.

### 6. Require raw-blit opt-in / reject padding-bearing fallback types

Security effect: stronger global restriction of the automatic raw fallback.

Compatibility cost: source/runtime behavior change for existing unmanaged payloads, and padding-based rejection additionally requires a reliable managed-layout padding detector. This remains a separate compatibility-policy option rather than the selected mitigation in this PR.

## Performance evidence

`UnsafeBlitPaddingBenchmarks` compares the current production `UnsafeBlitCodec<T>` hot path with a representative post-blit padding-clear candidate for `ByteInt32`, `ByteInt64`, `Int64Byte`, and `NestedPadding`.

The benchmark uses BenchmarkDotNet `ShortRun` jobs and records allocation data. Each CI job uploads both the machine-readable padding report and BenchmarkDotNet artifacts; the reviewed `32627839187` summary is additionally retained in-repo at [`evidence/unsafe-blit-padding-32627839187.json`](evidence/unsafe-blit-padding-32627839187.json). Final review should use the same-run raw/canonicalized ratios rather than compare absolute nanosecond values across different hosted runners.

Evidence snapshot `32627839187` produced the following same-run results (raw -> canonicalized; ratio):

| Runtime | `ByteInt32` | `ByteInt64` | `Int64Byte` | `NestedPadding` |
| --- | --- | --- | --- | --- |
| Linux x64, .NET 10.0.11 | 7.169 -> 7.560 ns; **1.05x** | 5.880 -> 7.957 ns; **1.35x** | 5.814 -> 8.096 ns; **1.39x** | 6.178 -> 11.298 ns; **1.83x** |
| Windows x64, .NET 10.0.11 | 6.829 -> 9.019 ns; **1.32x** | 6.867 -> 9.275 ns; **1.35x** | 7.040 -> 8.759 ns; **1.24x** | 7.014 -> 11.554 ns; **1.65x** |
| macOS ARM64, .NET 10.0.11 | 4.278 -> 5.137 ns; **1.20x** | 4.268 -> 5.113 ns; **1.20x** | 4.126 -> 5.294 ns; **1.28x** | 4.482 -> 7.441 ns; **1.66x** |

No benchmark variant allocated managed memory. The hosted-runner `ShortRun` configuration has only three measured iterations and some individual confidence intervals are wide, so the figures are directional rather than a release-grade microbenchmark budget. The consistent result across all three environments is that clearing already-known padding is not free; nested/multiple padding ranges are the most expensive case in this sample.

Because the candidate assumes an already-known padding map, any measured regression is the minimum steady-state cost of this mitigation shape. A production generic implementation would also carry layout-discovery, caching, NativeAOT/trimming, and maintenance complexity.

## Product/security conclusion

The observed behavior is not an arbitrary-memory over-read: only bytes inside `T`'s managed representation are copied. However, padding bytes can hold non-logical source-storage state and can cross a meaningful confidentiality boundary when values originate from unsafe/uninitialized/native storage. That makes the risk practical under a specific provenance condition.

For 2.x, the selected balance is therefore **retain + document/constrain**:

1. keep the current allocation-free raw-blit production path;
2. explicitly document that padding is wire-visible and non-canonical, and that zero padding observed from representative `new`/`default` controls is not a supported representation guarantee;
3. classify unsafe/native/uninitialized source provenance as unsuitable for confidential raw-blit RPC boundaries unless the complete representation is sanitized; do not treat ordinary logical field initialization alone as a padding sanitizer;
4. for affected payloads today, use existing explicit per-type custom Codec/Adapter/selector binding to leave the SharpLink raw-blit domain;
5. track #311 as the Contract-level, compile-time scaling mechanism: an `Unmanaged` route can replace the whole SharpLink UnsafeBlit domain with a chosen third-party Adapter while explicit per-type bindings remain higher priority;
6. keep automated multi-runtime poison evidence and canonicalization-cost evidence so future routing/restriction/canonicalization decisions have regression data.

This decision can be revisited if broader runtime/JIT/AOT evidence changes the observed padding-risk profile, if a runtime changes initialization/layout behavior, or if a low-cost/AOT-safe canonicalization mechanism becomes available.
