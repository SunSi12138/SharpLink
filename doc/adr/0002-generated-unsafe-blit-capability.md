# ADR 0002: Generated UnsafeBlit owns its ABI capability

- Status: Proposed for 3.0
- Date: 2026-10-10
- Related: [#762](https://github.com/SunSi12138/SharpLink/issues/762), [3.0 migration](../unsafe-blit-3.0-migration.md)

## Context

The 2.x generated path and arbitrary unmanaged JIT fallback share a Type-based resolver. Package feature switches, embedded linker substitution and partial-trim suppressions compensate for a compiled reflection branch. Marking all of Runtime trimmable previously regressed the blocking iOS CoreCLR `TrimMode=copy` evidence.

## Decision

Each finalized generated UnsafeBlit plan emits an owner-local codec factory with its native-pointer width and framework raw-ABI requirement. The generated factory uses the Abstractions-only `IRpcGeneratedUnsafeBlitCodecProvider` capability. Runtime validates the supplied requirement directly, then constructs its internal raw codec. Missing metadata is an error; neither the process catalog nor an endpoint codec resolver is consulted.

Remove the arbitrary unmanaged reflective fallback in 3.0. Do not ship an optional reflection package. This supersedes #762's original goal of retaining automatic standalone JIT fallback, by explicit maintainer decision. Builtins/enums, generated contracts and explicitly registered codecs remain supported. Generated dynamic modules use the same owner-local factories and do not need arbitrary Type reflection.

Factories and their Type references live in the existing manifest-generation ownership graph. No new process-global Type cache is introduced. Existing registration replacement, retirement and collectible ownership rules continue to apply. Request-only raw payloads resolve their generated factory during request-codec construction; builtin inline wire layout is unchanged.

Runtime is not globally trimmable. Generated correctness no longer depends on final consumer publish properties, `SHARPLINK_NATIVEAOT`, a feature switch, an embedded linker substitution or a warning-suppression file. ProjectReference partial trimming therefore uses the same generated path as untrimmed JIT, full trimming and NativeAOT.

## Compatibility and consequences

3.0 uses generated API 5 and exact ABI identity `sharplink-3.0-api5-generated-unsafe-blit-v1`, a single increment from released 2.0.3/API4. The exact identity rejects incompatible development artifacts that also happen to use API 5. This work does not incorporate the independent #754 method-shape proposal.

The raw wire representation, physical ABI requirements and CodecHash remain unchanged. The public process-wide `SharpLinkGeneratedUnsafeBlitCatalog` is removed. Arbitrary unmanaged types without a generated or explicit codec now fail in ordinary JIT as well as trimmed/AOT applications. See the migration guide for the distinction between standalone runtime registration and generated RPC policy.

## Alternatives considered

- A separate reflection package: unnecessary compatibility surface without an identified essential consumer; requires explicit trim/dynamic-code restrictions and ongoing maintenance.
- A globally trimmable Runtime: changes unrelated mobile linker semantics and repeats an observed regression.
- Retaining feature-switch plumbing: leaves correctness dependent on linker configuration rather than absence of the reflective branch.

## Validation

Keep the existing blocking Android/iOS CoreCLR evidence, including iOS copy-mode behavior. Verify source and package full/partial trimming, NativeAOT, untrimmed JIT fail-closed behavior, generated dependency boundaries, owner isolation and collectible registration lifetime. Passing local Linux tests does not establish mobile compatibility.
