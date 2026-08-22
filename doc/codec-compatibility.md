# UnsafeBlitCodec compatibility contract

`UnsafeBlitCodec<T>` is SharpLink's high-performance fallback for value types that do not contain managed references. It serializes the current runtime's managed representation directly, so it is intentionally treated as ABI-sensitive rather than assumed to be stable across every OS, architecture, runtime family, pointer width, or future .NET major.

This document defines the 2.0 compatibility evidence model. It does not replace, restrict, or slow down the production codec hot path.

## Compatibility tiers

### Guaranteed / release-gated

A runtime/platform combination is Guaranteed only when the release gate actually executes the compatibility probe on that runtime and the full desktop producer/consumer matrix passes for the exact release commit.

The release-gated desktop matrix in `.github/workflows/codec-compatibility.yml` currently contains six hosted identities:

- Linux x64 CoreCLR;
- Linux arm64 CoreCLR;
- Windows x64 CoreCLR;
- Windows arm64 CoreCLR;
- macOS arm64 CoreCLR;
- macOS x64 CoreCLR.

The workflow is invoked by both PR Quick and Release Gate. Every desktop target is both a producer and a consumer: each consumer downloads all six producer corpora and invokes its own `UnsafeBlitCodec<T>` to deserialize producer bytes. A central Linux summary job only aggregates the per-runtime reports; it does not stand in for Windows or macOS decode execution.

Runner labels are infrastructure selectors, not compatibility identities. Each producer manifest records the OS, process/OS architecture, pointer size, .NET SDK/runtime, runtime family, runtime-family provenance, RID, endianness, compilation mode, execution environment, and SharpLink commit. Compilation mode is observed in-process. Runtime-family provenance is explicit: desktop uses runtime reflection, Android inspects loaded runtime libraries, while Browser/iOS record Mono as derived from the selected platform/runtime pack rather than presenting that platform fact as an independent runtime-family assertion. Expected lane values never overwrite recorded identity fields. The manifest and its provenance fields are the evidence source of truth.

A self-roundtrip failure, fixed-width size/layout mismatch, deserialize rejection, segmented-deserialize rejection, or logical-value mismatch is a release blocker. A byte-only difference with successful semantic cross-decode is reported as evidence and is not automatically a blocker.

The six-platform desktop expansion is exercised as a 6 producer × 6 consumer × 49 fixture matrix: 1,764 verification entries. A run is only considered green if every expected producer fixture is present exactly once and all 1,764 blocking matrix entries complete without blockers.

### Verified / evidence-backed

A combination or explicitly named producer/consumer edge is Verified when retained compatibility evidence exists for an exact commit/runtime/platform but the environment is not part of every release hard gate.

The current evidence-backed environments include:

- Browser WebAssembly: `browser-wasm`, wasm32, Mono, Interpreter, executed in a real headless Chrome instance;
- Android x64 emulator: Mono;
- Android x64 emulator: .NET 10 CoreCLR experimental runtime;
- iOS Simulator x64: Mono, Interpreter;
- iOS Simulator arm64: Mono, Interpreter.

Browser evidence in `.github/workflows/codec-compatibility.yml` is bidirectional with the six desktop identities. The Browser consumer downloads all six desktop corpora plus its own corpus. Separately, six non-gating desktop evidence consumers download the Browser-produced corpus and execute the safe fixtures on Linux x64/arm64, Windows x64/arm64, and macOS x64/arm64. Framework-owned raw fixtures are compared as representation evidence rather than unsafe semantic materialization. The Browser gate additionally requires the observed wasm32 identity (`pointerSize=4`, `runtimeIdentifier=browser-wasm`, and `targetFramework=net10.0/browser-wasm`) rather than relying on the platform tag alone.

Mobile evidence is defined by `.github/workflows/codec-mobile-compatibility.yml`. It is intentionally an evidence graph rather than an all-to-all five-platform matrix. The currently documented edges are:

- Linux x64 desktop reference -> Android Mono consumer;
- Linux x64 desktop reference -> Android CoreCLR consumer;
- Android Mono -> Android Mono and Android CoreCLR;
- Android CoreCLR -> Android Mono and Android CoreCLR;
- Linux x64 desktop reference -> iOS Simulator x64 consumer;
- iOS Simulator x64 -> itself;
- Linux x64 desktop reference -> iOS Simulator arm64 consumer;
- iOS Simulator arm64 -> itself.

There is currently no retained Android <-> iOS, iOS x64 <-> iOS arm64, or mobile -> desktop evidence in that workflow. Those absent edges must not be described as verified matrix compatibility. The mobile summary aggregates only the explicitly exercised reports.

The mobile workflow executes the same shared fixture corpus in the target runtime. Android runs both Mono and the .NET 10 experimental CoreCLR runtime inside an x64 emulator. iOS runs Mono inside x64 and arm64 iOS Simulators. These are runtime-executed results, not build-only claims.

Evidence is tied to the environment recorded in the artifact manifest. In particular, simulator/emulator evidence must not be presented as physical-device evidence, and successful execution of an experimental runtime does not turn that runtime into a SharpLink product guarantee.

Evidence claims must be backed by successful current-head workflow artifacts after probe or evidence-contract changes; older successful artifacts do not validate newer harness behavior.

### Investigational / not guaranteed yet

Platforms, runtime combinations, or producer/consumer edges that have not been executed by the release gate or reviewed evidence lane remain Investigational. Current examples include:

- physical Android and iOS devices;
- Android arm64 device/emulator execution;
- Android <-> iOS cross-runtime edges;
- iOS Simulator x64 <-> arm64 cross-architecture edges;
- mobile producer -> desktop consumer edges;
- NativeAOT compatibility beyond existing dedicated smoke coverage;
- future .NET major versions and unreviewed servicing/runtime combinations;
- other pointer-width, runtime-family, or architecture combinations not represented by retained evidence.

`Codec Android ARM64 Device Evidence` provides a manual path for a prepared self-hosted ARM64 runner with one attached physical `arm64-v8a` Android device. The workflow rejects emulator devices before execution, while the Android host independently records its in-process RID and classifies the execution environment rather than hard-coding the x64-emulator identity. The uploaded artifact retains the desktop reference corpus, device-local corpora, verification reports, and aggregate summary. Until such a physical-device run is retained and reviewed, Android ARM64 remains Investigational.

"Investigational" means "not yet verified". It should not be rewritten as "unsupported" unless SharpLink explicitly makes that product decision.

## Probe and artifacts

The desktop probe lives at `test/SharpLink.CodecCompatibility` and directly exercises the internal `UnsafeBlitCodec<T>` through a test-only friend-assembly relationship. Production serialization code is unchanged.

Supported desktop commands:

```text
SharpLink.CodecCompatibility describe
SharpLink.CodecCompatibility produce --output <dir>
SharpLink.CodecCompatibility verify --input <producer-root> --output <verification.json>
SharpLink.CodecCompatibility self --output <dir>
SharpLink.CodecCompatibility summarize --input <verification-root> --output <dir> --profile <desktop|mobile|android-arm64-device>
```

Portable hosts reuse the same fixture and verification implementation:

- `test/SharpLink.CodecCompatibility.Browser`
- `test/SharpLink.CodecCompatibility.Android`
- `test/SharpLink.CodecCompatibility.iOS`

These workload-specific host projects are deliberately not added to the normal solution build. Their dedicated workflows install the required WebAssembly/Android/iOS workloads and execute them in their actual host environments.

A producer writes a versioned `manifest.json` plus one raw binary file per logical fixture. The manifest records layout metadata, raw-wire hashes, runtime identity, execution environment, padding-poison evidence, and fixture-registry metadata generated from the authoritative C# `FixtureRegistry`. Portable JS tooling derives the full fixture set, framework-raw subset, and native-width subset from that metadata and rejects registry drift; it does not maintain a second hand-written logical fixture registry. The logical fixture definitions in source are the source of truth; observed bytes are evidence, not a permanent wire specification. Schema-bearing artifacts require an explicit `schemaVersion`; schema-less input is rejected instead of being treated as version 1 by default.

Portable Browser/mobile hosts exchange the same corpus and verification schema through a JSON envelope. The portable artifact contract uses `System.Text.Json` source-generated metadata so trimming/linking on mobile hosts cannot silently remove manifest fields.

Consumers report, per producer/fixture pair:

- producer and consumer runtime/platform tags;
- producer and consumer `Unsafe.SizeOf<T>()`;
- producer and consumer field offsets where applicable;
- producer and local raw-wire hashes;
- contiguous cross-deserialize status and logical equality;
- segmented cross-deserialize status and logical equality when the value is large enough to split;
- byte-for-byte equality and first differing byte offset;
- exception information when decode fails;
- a classification such as `IDENTICAL_BYTES_AND_COMPATIBLE`, `DIFFERENT_BYTES_BUT_CROSS_COMPATIBLE`, `SIZE_OR_LAYOUT_MISMATCH`, `DESERIALIZE_REJECTED`, `DESERIALIZED_VALUE_MISMATCH`, `SEGMENTED_DESERIALIZE_REJECTED`, `SEGMENTED_DESERIALIZED_VALUE_MISMATCH`, `EXPECTED_ARCH_DEPENDENT`, or `PROBE_UNAVAILABLE`.

Semantic result fields are tri-state. `true` and `false` mean the semantic operation actually ran and produced that result; `null` / `not-run` means the operation was intentionally not executed. Raw representation-only evidence must never set logical equality to `true` merely because bytes match. Raw representation evidence also recomputes and validates the producer and local SHA-256 hashes before classifying byte identity. Strict gates require classification, byte equality, and first-difference metadata to agree with the validated semantic or raw-representation outcome.

The desktop aggregator emits both `compatibility-summary.json` and `compatibility-summary.md`. The mobile evidence aggregator emits the same report format over its explicitly documented edges; that aggregation is not an assertion that every listed mobile environment consumed every other producer.

## Corpus scope

The 2.0 baseline corpus includes fixed-width controls, internal and tail padding, multiple alignment classes, nested structs, sequential/explicit layout controls, Pack 1/2/4/8 controls, native-width canaries, enums, 64/256/1024/2048-byte structs, user-like DTO/value structs, and direct raw-layout probes for selected built-in semantic structs.

For every same-size fixture larger than one byte, blocking verification performs both a normal single-segment deserialize and a genuinely multi-segment `ReadOnlySequence<byte>` deserialize. The first segment is deliberately shorter than `Unsafe.SizeOf<T>()`, forcing `CodecHelpers.ReadUnmanaged<T>` through its cross-segment copy path. The 64/256/1024-byte fixtures exercise the stack-backed segmented copy path, while the 2 KiB fixture crosses the `>1024` threshold and exercises the `ArrayPool<byte>` segmented copy path.

Built-in raw-layout fixtures are explicitly labeled `builtin-semantic-raw`. Their results are evidence about direct `UnsafeBlitCodec<T>` behavior and must not be confused with the stability of SharpLink's specialized production codecs selected by `RpcCodecProvider`.

Portable consumers do not blindly materialize framework-owned raw semantic structs produced by another runtime. Safe fixtures perform real cross-deserialize in the target Browser/mobile runtime. `builtin-semantic-raw` fixtures are compared separately as raw representation evidence and reported as `IDENTICAL_RAW_REPRESENTATION` or `RAW_BUILTIN_REPRESENTATION_MISMATCH`. Their semantic decode/equality fields remain `not-run`.

This distinction is already useful evidence: Android Mono/CoreCLR and iOS Mono runs observed a `DateTimeOffsetRaw` representation difference relative to another runtime while the logical fixture definition was the same. That representation-only observation is retained as non-blocking evidence rather than converted into an unsafe semantic materialization. When framework semantic fixtures are decoded in the desktop matrix, temporal comparers include observable `DateTime.Kind` and `DateTimeOffset.Offset` state rather than relying on the framework's looser default equality semantics.

Native-width fixtures remain in the corpus even when pointer-width pairs differ. A mismatch is classified as `EXPECTED_ARCH_DEPENDENT` only when the producer and consumer pointer widths actually differ; the workflow does not use a blanket allow-failure switch.

## Padding poison evidence

Padding-sensitive fixtures are also constructed over backing memory prefilled with different byte patterns before assigning the same logical fields. The probe records whether equal logical values produce equal raw wire bytes, the differing offsets, the known padding offsets, and source/wire hashes.

This experiment has produced a concrete finding. In PR Quick run `32508067269`, the Linux x64 producer recorded equal logical values but different `UnsafeBlitCodec<T>` wire bytes for `ByteInt32` at offsets 1-3 and `Int64Byte` at offsets 9-15. Every differing byte was inside the fixture's known padding region. The current raw-blit fallback therefore transmits source padding state for these layouts; this behavior is observed evidence, not a hypothetical possibility. The separate security/product evaluation of information-disclosure risk and possible mitigations is tracked in #269.

This is evidence only. A padding difference does not by itself imply a production fix, mandatory `Pack=1`, a new attribute, or removal of the raw blit fallback. Any product restriction or canonicalization change requires a separate implementation decision and performance evaluation.

## Expanding the matrix

Adding a runtime/platform should normally require only a new workflow matrix target or host wrapper that can run the same probe and exchange the same artifacts. The logical fixture corpus must not be forked per platform.

The preferred progression is:

1. execute the actual target runtime as a producer and consumer;
2. retain a self-describing artifact and verification report;
3. classify only the actually exercised producer/consumer edges as Verified / evidence-backed;
4. promote an environment to Guaranteed / release-gated only when SharpLink intentionally accepts the infrastructure cost and product commitment.

Build-only, emulator, simulator, and physical-device results must always be labeled as the environment that actually executed the probe.
