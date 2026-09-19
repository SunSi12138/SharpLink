# UnsafeBlitCodec compatibility contract

`UnsafeBlitCodec<T>` is SharpLink's high-performance fallback for value types that do not contain managed references. It serializes the current runtime's managed representation directly, so it is intentionally treated as ABI-sensitive rather than assumed to be stable across every OS, architecture, runtime family, pointer width, or future .NET major.

This document defines the current compatibility contract and evidence model. The primary compatibility matrix is CoreCLR. Mono is tracked as auxiliary evidence with narrower, type/layout-specific claims. This model does not replace, restrict, or slow down the production codec hot path.

## Compatibility tiers

### Core compatibility matrix

SharpLink's primary codec compatibility matrix is **CoreCLR**. A platform is only part of this matrix when the compatibility probe actually executes on that runtime and retains producer/consumer evidence; build-only support is not compatibility evidence.

The release hard gate is the .NET 10 hosted-desktop CoreCLR matrix in `.github/workflows/codec-compatibility.yml`:

- Linux x64 CoreCLR;
- Linux arm64 CoreCLR;
- Windows x64 CoreCLR;
- Windows arm64 CoreCLR;
- macOS x64 CoreCLR;
- macOS arm64 CoreCLR.

Every desktop identity is both a producer and a consumer, yielding a full 6 x 6 directed matrix. Each consumer downloads all six producer corpora and invokes its own `UnsafeBlitCodec<T>` to perform the cross-deserialize. The central summary only aggregates those reports; it does not substitute Linux execution for another runtime.

A self-roundtrip failure, fixed-width size/layout mismatch, deserialize rejection, segmented-deserialize rejection, or logical-value mismatch on a release-gated edge is a blocker. A byte-only difference with successful semantic cross-decode is retained as evidence and is not automatically a blocker.

The manifest is the identity source of truth. It records the exact SharpLink commit, target framework, SDK/runtime versions, OS and process architecture, pointer size, runtime family and provenance, RID, endianness, compilation mode, and execution environment.

### Verified CoreCLR expansion

CoreCLR environments outside the six hosted-desktop release gate are evidence-backed rather than automatically promoted to the release guarantee.

Current CoreCLR evidence includes:

- Browser WebAssembly: wasm32, .NET 11 CoreCLR, executed in real headless Chrome with `UseMonoRuntime=false`;
- Android x64 emulator: .NET 10 CoreCLR;
- iOS Simulator x64: .NET 11 CoreCLR;
- iOS Simulator arm64: .NET 11 CoreCLR, currently an experimental lane;
- .NET 10 baseline/latest servicing compatibility on Linux x64 CoreCLR.

Browser CoreCLR is exercised in both directions against the six desktop CoreCLR identities: all six desktop corpora are consumed in Browser CoreCLR, Browser CoreCLR verifies itself, and all six desktop consumers independently consume the Browser CoreCLR corpus. The Browser manifest must identify `browser-wasm-browser-coreclr-net11`, `runtimeFamily=CoreCLR`, `runtimeFamilySource=build-runtime-selection`, `pointerSize=4`, and `targetFramework=net11.0/browser-wasm`.

The current non-Mono mobile workflow is intentionally an evidence graph rather than an all-to-all mobile matrix:

- Linux x64 desktop CoreCLR -> Android x64 emulator CoreCLR, plus Android CoreCLR self;
- Linux x64 desktop CoreCLR -> iOS Simulator x64 CoreCLR, plus iOS x64 CoreCLR self;
- Linux x64 desktop CoreCLR -> iOS Simulator arm64 CoreCLR, plus iOS arm64 CoreCLR self.

There is currently no retained Android <-> iOS, iOS x64 <-> iOS arm64, or mobile -> desktop codec evidence. Those missing edges must not be described as verified compatibility. Simulator/emulator evidence must not be presented as physical-device evidence.

The .NET 10 servicing workflow keeps the platform identity fixed to Linux x64 CoreCLR and varies the exact SDK/runtime pair. Baseline and latest each act as producer and consumer, yielding baseline -> baseline, baseline -> latest, latest -> baseline, and latest -> latest. Exact runtime versions are pinned and validated in the manifests.

### Mono compatibility boundary

Mono is **not part of the core compatibility matrix** and is not a blanket SharpLink cross-runtime ABI guarantee. Mono evidence is retained only where an actual Mono runtime executed the shared probe, and claims are scoped to the exercised edge and fixture category.

For Mono-related evidence, "compatible" means that the consumer can deserialize the producer bytes and recover the same logical value. It does **not** imply that the two runtimes emit canonical byte-for-byte identical representations.

#### Types/layouts that can be semantically compatible

On retained Mono evidence edges, the strict compatibility path covers unmanaged value types whose size/layout contract agrees between producer and consumer, including the current fixture categories:

- fixed-width/no-padding primitives and controls such as integers, floating-point values, `Half`, `Int128`, `UInt128`, `Guid`, and fixed-width value structs;
- sequential and explicit-layout value structs;
- explicit Pack 1/2/4/8 controls;
- enums and enum-containing structs;
- ordinary nested/alignment/user-like unmanaged value structs when producer and consumer report compatible size/offsets;
- large unmanaged value structs, including the segmented read paths.

Internal or tail padding does not by itself make a type semantically incompatible. The cross-decode can still succeed when the field layout agrees. However, source padding bytes are not a canonical wire contract: equal logical values can serialize to different raw bytes because the fallback copies the managed representation, including padding state.

#### Types/layouts that are not portable contracts

The following categories must not be described as generally Mono/CoreCLR-compatible raw ABI:

- **Auto layout / runtime-owned layout.** The `auto-layout-release-scoped` fixtures intentionally remain evidence-only across Browser <-> desktop and Browser Mono <-> Browser CoreCLR edges. `LayoutKind.Auto` and framework-owned nested layouts are runtime implementation details. Known evidence includes size/layout differences and decode/value mismatches. These failures remain visible but are not a supported cross-runtime raw ABI contract.
- **Native-width values across pointer widths.** `nint`, `nuint`, `IntPtr`, `UIntPtr`, and structs containing them are architecture-dependent. A wasm32 Mono producer/consumer and a 64-bit CoreCLR peer do not have a general same-layout guarantee; such rows are classified `EXPECTED_ARCH_DEPENDENT` only when the pointer widths actually differ.
- **Framework semantic structs as raw representation.** `DateOnlyRaw`, `DateTimeRaw`, `DateTimeOffsetRaw`, `TimeOnlyRaw`, `TimeSpanRaw`, `IndexRaw`, `RangeRaw`, `RuneRaw`, and `DecimalRaw` are representation evidence only on portable cross-runtime edges. They are not materialized blindly as a semantic compatibility promise. This does not reduce the stability of SharpLink's specialized production codecs selected for those semantic types.
- **Canonical padding bytes.** Padding-bearing structs may be semantically cross-decodable while still producing different raw hashes. Byte equality therefore must not be used as the sole compatibility definition.

Browser Mono remains useful as an auxiliary canary because it combines a different runtime family with wasm32 pointer width, but a green Mono evidence lane does not enlarge the CoreCLR release guarantee. Conversely, a known Mono-only layout mismatch must not block a CoreCLR release unless the same failure exists on a CoreCLR edge that is part of the relevant contract.

Evidence claims must always be tied to the exact environment recorded in the artifact manifest. Historical Mono mobile artifacts do not become current-head guarantees after the mobile workflow changes; current mobile compatibility claims follow the CoreCLR lanes actually executed by `.github/workflows/codec-mobile-compatibility.yml`.

### Investigational / not guaranteed yet

Platforms, runtime combinations, or producer/consumer edges that have not been executed by the relevant release/evidence workflow remain Investigational. Current examples include:

- physical Android and iOS devices without retained current-head evidence;
- Android arm64 device/emulator execution without a reviewed current-head device artifact;
- Android <-> iOS cross-platform edges;
- iOS Simulator x64 <-> arm64 cross-architecture edges;
- mobile producer -> desktop consumer edges;
- NativeAOT compatibility beyond the existing dedicated smoke coverage;
- future .NET major/servicing combinations not represented by retained evidence;
- other pointer-width, runtime-family, or architecture combinations not represented by retained artifacts.

`Codec Android ARM64 Device Evidence` provides a manual path for one prepared self-hosted ARM64 runner with an attached physical `arm64-v8a` Android device. Until such a run is retained and reviewed for the current probe contract, Android ARM64 remains Investigational.

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

The desktop aggregator emits both `compatibility-summary.json` and `compatibility-summary.md`. The mobile evidence aggregator emits the same report format over its explicitly documented edges; that aggregation is not an assertion that every listed mobile environment consumed every other producer. The .NET 10 servicing evidence lane emits `servicing-compatibility-summary.json` and `servicing-compatibility-summary.md` after validating the four exact baseline/latest edges and their recorded SDK/runtime identities.

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

## Core compatibility boundary

The compatibility center of gravity is CoreCLR.

The six hosted-desktop .NET 10 CoreCLR identities form the blocking release matrix. Browser CoreCLR and mobile CoreCLR extend the evidence envelope, but they remain explicitly scoped to the edges that their workflows execute. Mono is auxiliary evidence rather than a second core matrix.

For Mono, compatibility claims are type/layout-specific: fixed-width and explicit/sequential layouts can be semantically cross-compatible when size and offsets agree; Auto layout is not a portable ABI contract; native-width values depend on pointer width; framework semantic structs are raw-representation evidence rather than a generic semantic wire promise; and padding bytes are not canonical.

Do not let an explicitly non-contractual Mono/AutoLayout mismatch block a CoreCLR release. Do not, however, generalize a green CoreCLR matrix into untested mobile/device/NativeAOT edges or treat evidence-only rows as guaranteed ABI.
