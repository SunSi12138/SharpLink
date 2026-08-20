# UnsafeBlitCodec compatibility contract

`UnsafeBlitCodec<T>` is SharpLink's high-performance fallback for value types that do not contain managed references. It serializes the current runtime's managed representation directly, so it is intentionally treated as ABI-sensitive rather than assumed to be stable across every OS, architecture, runtime family, pointer width, or future .NET major.

This document defines the 2.0 compatibility evidence model. It does not replace, restrict, or slow down the production codec hot path.

## Compatibility tiers

### Guaranteed / release-gated

A runtime/platform combination is Guaranteed only when the release gate actually executes the compatibility probe on that runtime and the full desktop producer/consumer matrix passes for the exact release commit.

The current desktop gate is defined by `.github/workflows/codec-compatibility.yml` and is invoked by `.github/workflows/release-gate.yml`. The workflow records the actual OS, process/OS architecture, pointer size, .NET SDK/runtime, runtime family, RID, endianness, compilation mode, and SharpLink commit in each producer manifest. Runner labels are infrastructure selectors, not compatibility identities; the manifest is the source of truth.

For the gated desktop set, every runner is both a producer and a consumer. Each consumer downloads every producer corpus and invokes its own `UnsafeBlitCodec<T>` to deserialize producer bytes. A central Linux summary job only aggregates the per-runtime reports; it does not stand in for Windows or macOS decode execution.

A self-roundtrip failure, fixed-width size/layout mismatch, deserialize rejection, or logical-value mismatch is a release blocker. A byte-only difference with successful semantic cross-decode is reported as evidence and is not automatically a blocker.

### Verified / evidence-backed

A combination is Verified when there is retained compatibility-matrix evidence for an exact commit/runtime/platform but it is not part of every release hard gate. Nightly, manual, future cross-architecture, device, NativeAOT, Mono, and servicing-baseline runs may enter this tier after their artifacts are reviewed.

Verified does not mean SharpLink promises that every future runtime patch will keep the same managed representation. Evidence is tied to the environment recorded in the artifact manifest.

### Investigational / not guaranteed yet

Platforms or runtime combinations that have not been executed by the release gate or otherwise reviewed remain Investigational. This includes future runtime majors, mobile/device configurations, Browser WASM, 32-bit pointer-width combinations, and any other environment for which no current evidence exists.

"Investigational" means "not yet verified". It should not be rewritten as "unsupported" unless SharpLink explicitly makes that product decision.

## Probe and artifacts

The probe lives at `test/SharpLink.CodecCompatibility` and directly exercises the internal `UnsafeBlitCodec<T>` through a test-only friend-assembly relationship. Production serialization code is unchanged.

Supported commands:

```text
SharpLink.CodecCompatibility describe
SharpLink.CodecCompatibility produce --output <dir>
SharpLink.CodecCompatibility verify --input <producer-root> --output <verification.json>
SharpLink.CodecCompatibility self --output <dir>
SharpLink.CodecCompatibility summarize --input <verification-root> --output <dir>
```

A producer writes a versioned `manifest.json` plus one raw binary file per logical fixture. The manifest records layout metadata, raw-wire hashes, runtime identity, and padding-poison evidence. The logical fixture definitions in source are the source of truth; observed bytes are evidence, not a permanent wire specification.

Consumers report, per producer/fixture pair:

- producer and consumer runtime/platform tags;
- producer and consumer `Unsafe.SizeOf<T>()`;
- producer and consumer field offsets where applicable;
- producer and local raw-wire hashes;
- cross-deserialize success and logical equality;
- byte-for-byte equality and first differing byte offset;
- exception information when decode fails;
- a classification such as `IDENTICAL_BYTES_AND_COMPATIBLE`, `DIFFERENT_BYTES_BUT_CROSS_COMPATIBLE`, `SIZE_OR_LAYOUT_MISMATCH`, `DESERIALIZE_REJECTED`, `DESERIALIZED_VALUE_MISMATCH`, `EXPECTED_ARCH_DEPENDENT`, or `PROBE_UNAVAILABLE`.

The summary job emits both `compatibility-summary.json` and `compatibility-summary.md` as retained GitHub Actions artifacts.

## Corpus scope

The 2.0 baseline corpus includes fixed-width controls, internal and tail padding, multiple alignment classes, nested structs, sequential/explicit layout controls, Pack 1/2/4/8 controls, native-width canaries, enums, 64/256/1024-byte structs, user-like DTO/value structs, and direct raw-layout probes for selected built-in semantic structs.

Built-in raw-layout fixtures are explicitly labeled `builtin-semantic-raw`. Their results are evidence about direct `UnsafeBlitCodec<T>` behavior and must not be confused with the stability of SharpLink's specialized production codecs selected by `RpcCodecProvider`.

Native-width fixtures remain in the corpus even when future pointer-width pairs differ. A mismatch is classified as `EXPECTED_ARCH_DEPENDENT` only when the producer and consumer pointer widths actually differ; the workflow does not use a blanket allow-failure switch.

## Padding poison evidence

Padding-sensitive fixtures are also constructed over backing memory prefilled with different byte patterns before assigning the same logical fields. The probe records whether equal logical values produce equal raw wire bytes, the differing offsets, the known padding offsets, and source/wire hashes.

This is evidence only. A padding difference does not by itself imply a production fix, mandatory `Pack=1`, a new attribute, or removal of the raw blit fallback. Any product restriction or canonicalization change requires a separate implementation decision and performance evaluation.

## Expanding the matrix

Adding a runtime/platform should normally require only a new workflow matrix target or host wrapper that can run the same probe and exchange the same artifacts. The logical fixture corpus must not be forked per platform.

Cross-architecture, .NET servicing-baseline/latest, future .NET major, Mono/Android/iOS/NativeAOT, and Browser WASM evidence should be added incrementally. Build-only or simulator results must be labeled as such and must not be presented as executed compatibility evidence for a different runtime or physical-device architecture.
