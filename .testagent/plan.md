# 0.7.11 test plan

1. Add focused Generator P0 validation for adapter registration, selection, diagnostics,
   metadata discovery, closed factory output, and determinism.
2. Add runtime tests for scope sharing/isolation, transactional rollback, identity-aware cache replacement, and idempotent disposal.
3. Extend dynamic client/server tests for register, unregister, replace, cancellation, and stale-cache races.
4. Add SharpPack codec tests for payload shapes, invalid input, explicit contexts, concurrency, and exception mapping.
5. Add MemoryPack-to-SharpPack golden payload fixtures and byte-for-byte/read compatibility assertions.
6. Extend collectible ALC integration tests to retain weak references to adapter-owned state through drain/release.
7. Extend NativeAOT and local-package smoke projects to use `[SharpPackable]` without manual codec registration.
8. Re-open tests, run gap/assertion review, and record final evidence in `.testagent/status.md`.

All eight steps are complete. The final Release build, test suites, package smoke,
NativeAOT smoke, chaos run, and alternating performance gates passed.

## P0 exact test map

Implementation state: complete. The open-generic production defect exposed by P0 was
fixed in `HasTypeParameter`; all 76 Generator tests now pass.

| Requirement | Planned test evidence |
| --- | --- |
| `SHARPLINK042` invalid registration and unregistered selection | `InvalidAdapterRegistrationShapesShouldReportSharplink042`; `UnregisteredSelectedAdapterShouldReportSharplink042` |
| `SHARPLINK043` invalid adapter implementation shapes | `InvalidAdapterTypeShapesShouldReportSharplink043` |
| `SHARPLINK044` selector ownership conflict | `ConflictingSelectorRegistrationsShouldReportSharplink044` |
| `SHARPLINK045` candidate conflict and same-adapter idempotence | existing `ConflictingAdapterSelectionShouldReportSharplink045`; new `EquivalentAdapterCandidatesShouldBeIdempotent` |
| `SHARPLINK046` invalid attribute constructor/location | `InvalidAdapterAttributeFormsShouldReportSharplink046` |
| `SHARPLINK047` open target | `OpenGenericAdapterTargetShouldReportSharplink047` |
| `SHARPLINK048` adapter type/ID/wire conflicts | `AdapterIdentityConflictsShouldReportSharplink048` |
| `SHARPLINK049` built-in override | `BuiltinAdapterBindingShouldReportSharplink049` |
| No automatic fallback | `RegisteredAdapterShouldNotReplaceSupportedNativeDto`; `InstalledUnselectedAdapterShouldNotFallbackForUnsupportedDto` |
| Metadata-only discovery | `TransitiveAdapterRegistrationShouldBeDiscoveredFromMetadata` |
| Deterministic output and one holder per adapter | `AdapterOutputShouldBeDeterministicAcrossReferenceAndAttributeOrder`; `MultipleTargetsShouldShareOneGeneratedAdapterHolder` |
| Required manifest wire identity | `ManifestShouldRecordRequiredWireFormatsAtEveryPayloadPosition`; existing missing-field tests; `NullBlankOrWhitespaceWireFormatShouldInvalidateBaseline` |
| Adapter implementation/ID does not define compatibility | `AdapterIdentityChangeWithStableWireFormatShouldRemainCompatible` |

P0 validation command:

`dotnet test --project test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj --configuration Release --no-restore`

## External serializer deep-review plan

1. Audit compile-time Adapter discovery, shape, selection, emission, and compatibility.
2. Audit runtime factory identity, Scope ownership, rollback, disposal, and dynamic
   module publication/release.
3. Audit SharpPack 1.1.0 source behavior, exception classification, Context isolation,
   dependency resolution, and MemoryPack-removal boundaries.
4. Add failing regressions only for confirmed defects, fix production code, and retain
   exact requirement evidence.
5. Run warnings-as-errors build, Generator/Unit/Integration suites, local package JIT
   smoke, NativeAOT publication, collectible ALC coverage, and performance regression
   checks.
6. Update architecture/migration/change evidence and perform a final diff audit.

All six steps are complete.
