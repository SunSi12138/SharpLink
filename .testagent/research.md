# 0.7.11 test research

## Baseline

- Base branch: `dev`
- Base commit: `2dd4e84870b2694640ecd4ba61bec51f461e7226`
- SDK: .NET SDK 10.0.102, Microsoft Testing Platform, TUnit 1.14.0
- Release solution build: passed with zero warnings and zero errors
- Last known unit tests before this P0 phase: 313 passed
- Last known generator tests before this P0 phase: 59 passed

## Target inventory

- `SharpLink.Abstractions`: adapter runtime SPI and generated factory contract
- `SharpLink.Sdk`: adapter registration and explicit selection attributes
- `SharpLink.Runtime`: manifest-owned adapter scopes, generation-aware codec cache, disposal
- `SharpLink.Generator`: metadata-only adapter discovery, selection, diagnostics, closed factory emission
- client/server dynamic registration: transactional publish, drain, replace, scope disposal
- serializer extension: migrate `SharpLink.Serializer.MemoryPack` to `SharpLink.Serializer.SharpPack`
- contract manifest: required wire-format identity with no pre-1.0 legacy fallback
- package/AOT/integration paths: automatic adapter selection without runtime resolver

## Acceptance checklist

- Public adapter registration/selection APIs validate all declared constraints.
- Native codecs remain preferred unless a selector or explicit adapter binding applies.
- Adapter factories use closed `CreateCodec<T>()` calls and contain no runtime generic construction.
- One scope is shared per runtime context, manifest instance, and adapter ID.
- Registration and replacement publish transactionally; failed candidates dispose scopes.
- Cache entries are bound to registration identity and old cleanup cannot remove new codecs.
- Context/module disposal releases adapter scopes exactly once and preserves user-owned codecs.
- SharpPack 1.0.1 is the only serializer dependency; MemoryPack package/API/product remains are removed.
- MemoryPack 1.21.4 and SharpPack 1.0.1 golden payloads match before declaring `memorypack-binary/v1`.
- Generator output is deterministic under reference and attribute reordering.
- Missing, null, blank, or whitespace-only `wireFormatId` values make a baseline invalid.
- Unit, generator, integration, concurrency, ALC, AOT, pack, and package-smoke coverage passes.
- No remote state is changed; final branch contains four local commits and a clean worktree.

## Existing conventions

- Tests use TUnit and executable Microsoft Testing Platform projects.
- Generator tests compile in-memory source and inspect diagnostics/generated text.
- Runtime tests use small in-file fake manifests/codecs rather than a mocking library.
- Integration tests exercise real client/server transports and dynamic assembly registration.

## P0 static pairing and gap audit

- The required Roslyn static-pairing scan ran once over this checkout: 254 source files,
  66 test files, 53 paired files, and 201 unpaired files.
- This is a parse-only heuristic rather than line or branch coverage. It undercounts
  indirect generator and integration coverage, but it identified no direct pairing for
  the new SDK attributes, adapter SPI, runtime provider, or SharpPack codec source.
- The existing Generator additions cover one valid selector, type binding, named tuple
  binding, unmanaged selector priority, one `SHARPLINK043` shape, and one
  `SHARPLINK045` conflict. P0 must cover the remaining diagnostic and determinism matrix.
- The user explicitly rejected compatibility with development-time manifests that omit
  `wireFormatId`; the field is required and an invalid baseline must report
  `SHARPLINK024`.

## Final observed totals

- Unit tests: 327 passed.
- Generator tests: 76 passed.
- Integration tests: 226 passed.
- Local packages: seven 0.7.11 packages, including `SharpLink.Serializer.SharpPack`
  and excluding `SharpLink.Serializer.MemoryPack`.
