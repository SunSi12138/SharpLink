# UnsafeBlit padding security assessment

This document records the security decision for issue #269. It is deliberately separate from the cross-runtime compatibility matrix in #253/#265: byte compatibility and confidentiality are different questions.

## Decision

For the current 2.x raw-managed-representation contract, **retain `UnsafeBlitCodec<T>` and document/constrain its security contract; do not add automatic padding canonicalization to the production hot path in this change**.

The codec is intentionally an ABI-sensitive raw blit fallback. Its wire representation includes every byte in `Unsafe.SizeOf<T>()`, including padding. Therefore padding is part of the observable raw representation even though it is not part of the logical field value.

This decision is safe only with the following contract:

- raw-blit values whose storage provenance is ordinary zero-initialized managed storage are the expected use case;
- values populated through unsafe code, `stackalloc`, `Unsafe.SkipInit`, skipped local initialization, native interop, pooled/native buffers, or other mechanisms that can leave non-field bytes tainted must be treated as potentially carrying those bytes onto the wire;
- when such a value crosses a confidentiality boundary, use a generated/registered field-wise codec or explicitly sanitize the source representation before serialization;
- SharpLink does not promise canonical wire bytes for padding-bearing raw-blit structs.

A future major-version contract may choose explicit opt-in or fallback restrictions for raw blit. That is a compatibility decision and should not be smuggled into this security assessment without separate migration design.

## Threat model

### What the codec reads

`UnsafeBlitCodec<T>.Serialize` requests `Unsafe.SizeOf<T>()` bytes and performs one `Unsafe.WriteUnaligned` of the complete managed representation. It does not read beyond the struct representation. The issue is therefore not an out-of-bounds read of adjacent memory; it is propagation of bytes already present inside the representation's padding.

### When padding can contain non-logical state

Ordinary `default`/`new` managed construction starts from zeroed storage, and the evidence workflow verifies zero padding for representative controls before field assignment. In those ordinary paths, padding does not expose prior process state.

The confidentiality risk appears when callers construct or receive a struct through mechanisms that can preserve arbitrary non-field bytes. Representative examples include:

- a struct reinterpreted over uninitialized or reused stack/native storage;
- `Unsafe.SkipInit` or skipped local initialization followed by assignment of only logical fields;
- native/P/Invoke or memory-mapped input whose padding is not normalized;
- pooled/native buffers reused for struct-shaped storage;
- any unsafe helper that writes fields but intentionally does not clear the whole representation.

In those cases, the raw blit can move padding across an RPC/process/network boundary. Repeated serialization can therefore expose a small number of source-storage bytes per value. This is a **real but conditional information-disclosure primitive**, not merely a canonicalization concern.

The practical risk is low for ordinary safe managed construction and materially higher for unsafe/native-origin values crossing trust boundaries.

## Reproduction coverage

The `Codec Padding Security Evidence` workflow runs the actual internal `UnsafeBlitCodec<T>` from `SharpLink.Runtime` on three representative release environments:

- Linux x64 / CoreCLR / .NET 10;
- Windows x64 / CoreCLR / .NET 10;
- macOS ARM64 / CoreCLR / .NET 10.

Each job constructs two logically equal values over backing storage poisoned with different bytes, assigns the same logical fields, serializes through the production codec, and asserts that every differing wire byte is exactly a known padding byte.

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

The workflow also asserts that zeroing only the known padding offsets makes the two poisoned wires identical and that the ordinary default-initialized control has zero padding.

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

### 4. Require raw-blit opt-in

Security effect: makes the representation/confidentiality tradeoff explicit and prevents accidental fallback.

Compatibility cost: source/runtime behavior change for every unmanaged value type that currently relies on automatic fallback. This is a plausible future major-version design, not a targeted 2.x security fix.

### 5. Reject padding-bearing fallback types

Security effect: strong for the automatic fallback path.

Compatibility cost: breaks existing DTOs and still requires a reliable managed-layout padding detector. It also discards the established performance/compatibility envelope for types that intentionally use raw blit.

## Performance evidence

`UnsafeBlitPaddingBenchmarks` compares the current production `UnsafeBlitCodec<T>` hot path with a representative post-blit padding-clear candidate for `ByteInt32`, `ByteInt64`, `Int64Byte`, and `NestedPadding`.

The benchmark uses BenchmarkDotNet `ShortRun` jobs and records allocation data. Each CI job uploads both the machine-readable padding report and BenchmarkDotNet artifacts. Final review should use the same-run raw/canonicalized ratios rather than compare absolute nanosecond values across different hosted runners.

Because the candidate assumes an already-known padding map, any measured regression is the minimum steady-state cost of this mitigation shape. A production generic implementation would also carry layout-discovery, caching, NativeAOT/trimming, and maintenance complexity.

## Product/security conclusion

The observed behavior is not an arbitrary-memory over-read: only bytes inside `T`'s managed representation are copied. However, padding bytes can hold non-logical source-storage state and can cross a meaningful confidentiality boundary when values originate from unsafe/uninitialized/native storage. That makes the risk practical under a specific provenance condition.

For 2.x, the selected balance is therefore **retain + document/constrain**:

1. keep the current allocation-free raw-blit production path;
2. explicitly document that padding is wire-visible and non-canonical;
3. classify unsafe/native/uninitialized source provenance as unsuitable for confidential raw-blit RPC boundaries unless the representation is sanitized;
4. direct security-sensitive callers to generated/registered field-wise codecs;
5. keep automated multi-runtime poison evidence and canonicalization-cost evidence so a future opt-in/restriction decision has regression data.

This decision can be revisited if future evidence shows non-zero padding arising from ordinary safe SharpLink DTO construction, if a runtime changes initialization/layout behavior, or if a low-cost/AOT-safe canonicalization mechanism becomes available.
