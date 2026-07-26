# 0.7.11 test status

## P0 Generator and contract manifest

- Status: implemented and passing
- Allowed files: the two existing Generator test files and `.testagent` state only
- Production code changes: none authorized
- Validation command: `dotnet test --project test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj --configuration Release --no-restore`
- Result after production fix: 76 total, 76 passed, 0 failed, 0 skipped
- Defect found and fixed: Roslyn reports `typeof(List<>)` as an unbound generic;
  `HasTypeParameter` now checks `INamedTypeSymbol.IsUnboundGenericType` and
  `OpenGenericAdapterTargetShouldReportSharplink047` passes.

## P1 Runtime lifecycle and generation identity

- Validation command: `dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj --configuration Release --no-restore`
- Current result: 327 total, 327 passed, 0 failed, 0 skipped
- Direct evidence: `DifferentManifestsInOneContextShouldOwnSeparateAdapterScopes`,
  `DifferentAdaptersInOneManifestShouldOwnSeparateScopes`,
  `ThirdAdapterCodecFailureShouldDisposeCandidateScope`,
  `ScopeCreationFailureShouldRollbackEarlierScopes`,
  `AdapterIdentityMismatchShouldRejectAndDisposePreparedScopes`,
  `WrongTypedCodecShouldRejectAndDisposeCandidateScope`,
  `ExplicitCodecShouldWinAndRemainCallerOwned`,
  `ConflictingManifestCodecsShouldRollbackBothAdapterScopes`, and
  `TenThousandCodecPublicationRacesShouldPreserveRegistrationIdentity`.

## P2 SharpPack behavior

- Golden fixtures cover null root, nullable/string/non-ASCII, array/list/dictionary,
  nested values, empty collections, union/polymorphism, and circular references.
- `SharpPackCodecShouldRunConcurrentCallsAfterSharedStart` uses a materialized worker
  set and shared asynchronous start gate, so serialization calls genuinely overlap.
- `SharpPackCodecShouldRoundTripSingleAndMultiSegmentPayloads` covers a large payload
  across single- and three-segment `ReadOnlySequence<byte>` inputs.
- `SharpPackCodecShouldRejectMalformedPayloadWithoutLeakingContent` distinguishes
  truncated, trailing, and malformed payloads and asserts message hygiene.
- `SharpPackCodecShouldNotWrapSharpLinkOrFatalExceptions`,
  `SharpPackAdapterScopeDisposeShouldBeIdempotentAndRejectCreation`, and
  `SharpPackCodecShouldUseCallerCustomFormatterContext` cover exception policy,
  Scope lifecycle, caller ownership, and explicit formatter use.

## P3 collectible plugin state

- `CollectibleContextShouldUnloadAfterFrameworkReferencesAreReleased` performs a real
  SharpPack circular/collection RPC and weakly tracks the collectible ALC, Assemblies,
  Types, Manifest, generated factories, prepared Codecs, Adapter Scope, and
  `SharpPackSerializerContext`; all are collected after unregister.
- The existing replacement/drain, concurrent unregister, 100 replacement, and 10,000
  register/unregister integration tests remain part of the full Integration gate.

## Requirement evidence

| Requirement | Evidence |
| --- | --- |
| `SHARPLINK042` invalid ID/wire/selector registration | `InvalidAdapterRegistrationShapesShouldReportSharplink042` |
| `SHARPLINK042` selected but unregistered adapter | `UnregisteredSelectedAdapterShouldReportSharplink042` |
| `SHARPLINK043` interface/accessibility/sealed/constructor rules | `InvalidAdapterTypeShapesShouldReportSharplink043` |
| `SHARPLINK044` selector ownership | `ConflictingSelectorRegistrationsShouldReportSharplink044` |
| `SHARPLINK045` conflict and idempotent merge | `ConflictingAdapterSelectionShouldReportSharplink045`; `EquivalentAdapterCandidatesShouldBeIdempotent` |
| `SHARPLINK046` constructor/location rules | `InvalidAdapterAttributeFormsShouldReportSharplink046` |
| `SHARPLINK047` open generic target | `OpenGenericAdapterTargetShouldReportSharplink047` |
| `SHARPLINK048` type/ID/wire identity conflicts | `AdapterIdentityConflictsShouldReportSharplink048` |
| `SHARPLINK049` built-in target | `BuiltinAdapterBindingShouldReportSharplink049` |
| Native priority and no installed-package fallback | `RegisteredAdapterShouldNotReplaceSupportedNativeDto`; `InstalledUnselectedAdapterShouldNotFallbackForUnsupportedDto` |
| Metadata-only registration discovery | `TransitiveAdapterRegistrationShouldBeDiscoveredFromMetadata` |
| Closed factory and reflection-free output | strengthened `RegisteredSelectorShouldGenerateClosedAdapterFactoryWithoutReflection` |
| Determinism under reference and Attribute reorder | `AdapterOutputShouldBeDeterministicAcrossReferenceAndAttributeOrder` |
| One generated Adapter holder per Adapter ID | `MultipleTargetsShouldShareOneGeneratedAdapterHolder` |
| Required request/response/stream/member wire IDs | `ManifestShouldRecordRequiredWireFormatsAtEveryPayloadPosition` |
| Missing/null/blank wire IDs invalidate a baseline | `BaselineWithoutAdapterWireFormatShouldBeRejected`; `BaselineWithoutDtoMemberWireFormatShouldBeRejected`; `BaselineWithoutNativeWireFormatShouldBeRejected`; `NullBlankOrWhitespaceWireFormatShouldInvalidateBaseline` |
| Compatibility depends on wire ID, not Adapter implementation/ID | `AdapterIdentityChangeWithStableWireFormatShouldRemainCompatible`; `ExplicitWireFormatChangeShouldBeRejected` |

## Assertion review

- Diagnostic tests assert exact IDs; multi-shape tests also assert counts or
  distinguishing message text where diagnostic de-duplication permits it.
- Generated output tests assert closed target types, one holder, stable complete
  output, and absence of reflection/non-generic serialization constructs.
- Manifest tests parse JSON and assert exact wire IDs at unary, streaming, and
  nested DTO positions rather than relying on substring presence alone.
- Runtime tests assert Scope create/dispose counts, publication identity, rollback,
  ownership, and post-publication behavior rather than only checking non-null results.
- SharpPack tests cover equality/deep structure, negative/error behavior, state counts,
  message contents, and lifecycle transitions. No new test is assertion-free or
  trivial-only; all asynchronous assertion paths are awaited.
- Pseudo-mutation review killed the high-risk survived mutations identified during the
  audit: open-generic detection, wrong Codec acceptance, identity mismatch, lost Scope
  rollback, stale-cache cleanup, sequential "concurrency", trailing-byte acceptance,
  and payload leakage in errors.

## Final validation gate

- `dotnet restore Sharplink.slnx`: passed.
- Release solution build with warnings as errors: passed with zero warnings and zero errors.
- Tests: Unit 333/333, Generator 80/80, Integration 226/226.
- NativeAOT: TCP smoke, isolated local-package smoke, and SharedMemory smoke passed.
- Local package smoke: all seven 0.7.11 packages restored and ran from an isolated cache;
  the SDK contained the generator, SharpPack depended exactly on SharpPack 1.1.0, and no
  MemoryPack package remained.
- SharedMemory chaos: 120 seconds, 417,278 successful operations, 150,246 expected
  injected failures, zero unexpected failures, 11 restarts, and all final resource
  counters drained to zero.
- BenchmarkDotNet alternating medians retained 98.20% to 100.67% of baseline throughput
  with unchanged allocations for the adapter path. TCP load medians retained 97.35%
  and 99.81% throughput at concurrency 1 and 128, while P99 latency was 102.86% and
  99.23% of baseline. These satisfy the 97% throughput and 105% P99 gates.
- `git diff --check`: passed. No remote state was changed.

## External serializer deep review

- Status: confirmed defects fixed; final validation complete.
- Open-generic probe removed because existing behavior already reports `SHARPLINK043`.
- Different Adapter identities sharing a proven target/schema/wire contract were ruled
  compatible by design; no implementation identity was added to wire compatibility.

### Fixed regression evidence

| Requirement | Evidence |
| --- | --- |
| Effective public Adapter type | `AdapterNestedInNonPublicTypeShouldReportSharplink043` |
| Every factory instance matches generated identity | `EveryFactoryAdapterInstanceShouldMatchGeneratedIdentity` |
| Throwing Scope does not skip later Scopes | `ScopeDisposeFailureShouldNotSkipRemainingAdapterScopes` |
| Throwing registration does not skip later registrations | `ContextDisposeFailureShouldNotSkipRemainingManifestRegistrations` |
| Automatic Scope owns an isolated formatter graph | `SharpPackAdapterScopesShouldOwnIsolatedFormatterGraphs` |
| SharpPack interface writer remains NativeAOT-compatible | `SharpLink.AotSmoke` NativeAOT publish and TCP execution |
| Fatal and cancellation exceptions are preserved | `SharpPackCodecShouldNotWrapSharpLinkOrFatalExceptions`; `SharpPackCodecShouldNotWrapAccessViolationException`; `SharpPackCodecShouldNotWrapCancellationException` |
| Nested collection Adapter wire changes are rejected | `AdapterWireFormatChangeInsideNativeCollectionShouldBeRejected` |
| Missing/null reachable Codec inventory is invalid | `BaselineWithoutReachableCodecWireInventoryShouldBeRejected`; `BaselineWithNullReachableCodecWireInventoryShouldBeRejected` |

### Validation recorded so far

- Restore passed.
- Release solution build with warnings as errors passed with zero warnings/errors.
- Generator: 80/80 passed.
- Unit: 333/333 passed after the final exception-tree assertion rebuild.
- Integration: 226/226 passed, including collectible ALC release coverage.
- NativeAOT application publication and execution passed for `osx-arm64`.
- Seven local 0.7.11 packages were produced; the serializer nuspec contains exact
  `SharpPack [1.1.0]` and no MemoryPack package.
- Isolated local-package JIT restore/build/run passed with both SharpLink and SharpPack
  generated sources present; the final resolved graph contains SharpPack/Core/Generator
  1.1.0 and no MemoryPack.
- A local-package NativeAOT `osx-arm64` Mach-O executable published and ran.
- Five post-review BenchmarkDotNet rounds retained 101.35%–103.04% of the 0.7.10
  baseline throughput and 101.47%–104.94% of the original 0.7.11 candidate throughput;
  median allocations remain 1152/5952 B for Adapter payloads and 440/1400 B for native arrays.
