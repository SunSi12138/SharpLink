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

Runner labels are infrastructure selectors, not compatibility identities. Each producer manifest records the actual OS, process/OS architecture, pointer size, .NET SDK/runtime, runtime family, RID, endianness, compilation mode, execution environment, and SharpLink commit. The manifest is the source of truth.

A self-roundtrip failure, fixed-width size/layout mismatch, deserialize rejection, or logical-value mismatch is a release blocker. A byte-only difference with successful semantic cross-decode is reported as evidence and is not automatically a blocker.

The six-platform desktop expansion was exercised as a 6 producer × 6 consumer × 48 fixture matrix: 1,728 verification entries with zero blocking failures.

### Verified / evidence-backed

A combination is Verified when retained compatibility evidence exists for an exact commit/runtime/platform but the environment is not part of every release hard gate.

The current evidence-backed lanes include:

- Browser WebAssembly: `browser-wasm`, wasm32, Mono, Interpreter, executed in a real headless Chrome instance;
- Android x64 emulator: Mono;
- Android x64 emulator: .NET 10 CoreCLR experimental runtime;
- iOS Simulator x64: Mono, Interpreter;
- iOS Simulator arm64: Mono, Interpreter.

Browser evidence is produced and consumed by the optional Browser jobs in `.github/workflows/codec-compatibility.yml`. Mobile evidence is defined by `.github/workflows/codec-mobile-compatibility.yml` and is intentionally a separate reusable/manual workflow rather than a release hard gate.

The mobile workflow executes the same shared fixture corpus in the target runtime. Android runs both Mono and the .NET 10 experimental CoreCLR runtime inside an x64 emulator. iOS runs Mono inside x64 and arm64 iOS Simulators. These are runtime-executed results, not build-only claims.

Evidence is tied to the environment recorded in the artifact manifest. In particular, simulator/emulator evidence must not be presented as physical-device evidence, and successful execution of an experimental runtime does not turn that runtime into a SharpLink product guarantee.

Validation run `32448182736` exercised the trim-safe portable artifact path and completed successfully for Android Mono, Android CoreCLR, iOS Simulator x64, and iOS Simulator arm64. Each iOS simulator produced all 48 fixtures and returned 96 producer/fixture verification entries with zero blockers. Android produced both 48-fixture corpora and both consumers completed with zero blockers.

### Investigational / not guaranteed yet

Platforms or runtime combinations that have not been executed by the release gate or reviewed evidence lane remain Investigational. Current examples include:

- physical Android and iOS devices;
- Android arm64 device/emulator execution;
- NativeAOT compatibility beyond existing dedicated smoke coverage;
- future .NET major versions and unreviewed servicing/runtime combinations;
- other pointer-width, runtime-family, or architecture combinations not represented by retained evidence.

"Investigational" means "not yet verified". It should not be rewritten as "unsupported" unless SharpLink explicitly makes that product decision.

## Probe and artifacts

The desktop probe lives at `test/SharpLink.CodecCompatibility` and directly exercises the internal `UnsafeBlitCodec<T>` through a test-only friend-assembly relationship. Production serialization code is unchanged.

Supported desktop commands:

```text
SharpLink.CodecCompatibility describe
SharpLink.CodecCompatibility produce --output <dir>
SharpLink.CodecCompatibility verify --input <producer-root> --output <verification.json>
SharpLink.CodecCompatibility self --output <dir>
SharpLink.CodecCompatibility summarize --input <verification-root> --output <dir>
```

Portable hosts reuse the same fixture and verification implementation:

- `test/SharpLink.CodecCompatibility.Browser`
- `test/SharpLink.CodecCompatibility.Android`
- `test/SharpLink.CodecCompatibility.iOS`

These workload-specific host projects are deliberately not added to the normal solution build. Their dedicated workflows install the required WebAssembly/Android/iOS workloads and execute them in their actual host environments.

A producer writes a versioned `manifest.json` plus one raw binary file per logical fixture. The manifest records layout metadata, raw-wire hashes, runtime identity, execution environment, and padding-poison evidence. The logical fixture definitions in source are the source of truth; observed bytes are evidence, not a permanent wire specification.

Portable Browser/mobile hosts exchange the same corpus and verification schema through a JSON envelope. The portable artifact contract uses `System.Text.Json` source-generated metadata so trimming/linking on mobile hosts cannot silently remove manifest fields.

Consumers report, per producer/fixture pair:

- producer and consumer runtime/platform tags;
- producer and consumer `Unsafe.SizeOf<T>()`;
- producer and consumer field offsets where applicable;
- producer and local raw-wire hashes;
- cross-deserialize success and logical equality;
- byte-for-byte equality and first differing byte offset;
- exception information when decode fails;
- a classification such as `IDENTICAL_BYTES_AND_COMPATIBLE`, `DIFFERENT_BYTES_BUT_CROSS_COMPATIBLE`, `SIZE_OR_LAYOUT_MISMATCH`, `DESERIALIZE_REJECTED`, `DESERIALIZED_VALUE_MISMATCH`, `EXPECTED_ARCH_DEPENDENT`, or `PROBE_UNAVAILABLE`.

The desktop and mobile aggregators emit both `compatibility-summary.json` and `compatibility-summary.md` as retained GitHub Actions artifacts.

## Corpus scope

The 2.0 baseline corpus includes fixed-width controls, internal and tail padding, multiple alignment classes, nested structs, sequential/explicit layout controls, Pack 1/2/4/8 controls, native-width canaries, enums, 64/256/1024-byte structs, user-like DTO/value structs, and direct raw-layout probes for selected built-in semantic structs.

Built-in raw-layout fixtures are explicitly labeled `builtin-semantic-raw`. Their results are evidence about direct `UnsafeBlitCodec<T>` behavior and must not be confused with the stability of SharpLink's specialized production codecs selected by `RpcCodecProvider`.

Portable consumers do not blindly materialize framework-owned raw semantic structs produced by another runtime. Safe fixtures perform real cross-deserialize in the target Browser/mobile runtime. `builtin-semantic-raw` fixtures are compared separately as raw representation evidence and reported as `IDENTICAL_RAW_REPRESENTATION` or `RAW_BUILTIN_REPRESENTATION_MISMATCH`.

This distinction is already useful evidence: Android Mono/CoreCLR and iOS Mono runs observed a `DateTimeOffsetRaw` representation difference relative to another runtime while the logical fixture value remained the same. That representation-only observation is retained as non-blocking evidence rather than converted into an unsafe semantic materialization.

Native-width fixtures remain in the corpus even when pointer-width pairs differ. A mismatch is classified as `EXPECTED_ARCH_DEPENDENT` only when the producer and consumer pointer widths actually differ; the workflow does not use a blanket allow-failure switch.

## Padding poison evidence

Padding-sensitive fixtures are also constructed over backing memory prefilled with different byte patterns before assigning the same logical fields. The probe records whether equal logical values produce equal raw wire bytes, the differing offsets, the known padding offsets, and source/wire hashes.

This is evidence only. A padding difference does not by itself imply a production fix, mandatory `Pack=1`, a new attribute, or removal of the raw blit fallback. Any product restriction or canonicalization change requires a separate implementation decision and performance evaluation.

## Expanding the matrix

Adding a runtime/platform should normally require only a new workflow matrix target or host wrapper that can run the same probe and exchange the same artifacts. The logical fixture corpus must not be forked per platform.

The preferred progression is:

1. execute the actual target runtime as a producer and consumer;
2. retain a self-describing artifact and verification report;
3. classify it as Verified / evidence-backed;
4. promote it to Guaranteed / release-gated only when SharpLink intentionally accepts the infrastructure cost and product commitment.

Build-only, emulator, simulator, and physical-device results must always be labeled as the environment that actually executed the probe.
