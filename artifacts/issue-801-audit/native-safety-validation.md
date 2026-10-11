# Issue #801 — NativeAOT, Trim and dynamic-module validation of 45 removal candidates

## Reproducible baseline

- Source: `dev` @ `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`; audit branch `audit/issue-801-dev-3x-deadcode`.
- Experimental script: `eng/SharpLink.DeadCodeAudit/RemovalProbe.cs`, fed by `artifacts/issue-801-audit/tier-A.csv`. **Each independent runner removed exactly 45 / 46 requested private/internal methods**; 1 skipped because it was not an ordinary method body.
- Source modifications existed **only in each GitHub Actions runner**, were not committed, and the experiment does not change `dev`.
- [Passing three-job workflow run](https://github.com/SunSi12138/SharpLink/actions/runs/38103364542); all three `ubuntu-latest` jobs on .NET 10 completed successfully. Every runner recompiled the full Release solution after removal: `0 Warning(s)`, `0 Error(s)`.

## Results

| Validation path | Actual executed checks | Result |
|---|---|---|
| Linux NativeAOT | Native publish and execute `eng/run-shared-memory-aot-process-smoke.sh`; out-of-process shared memory, referenced generated service, static readiness, TCP, SharpPack sidecar, pre-credit TCP and shared-memory streams | **PASS** |
| Full trim publish | `eng/run-project-reference-trim-smoke.sh`, `PublishTrimmed=true` + `TrimMode=full`, generated UnsafeBlit ABI and codec round-trip | **PASS** |
| Dynamic AssemblyLoadContext / Plugin | 9 selected IntegrationTests: 100 replacement registrations, route-before-drain, incompatible dynamic registration cleanup, collectible framework-reference unload, five Api4 streaming terminal modes | **PASS 9/9** |

### Exact pass markers from logs

- NativeAOT: `REFERENCED_SERVICE_PASS`, `AOT_SMOKE_CLIENT_PASS`, `STATIC_READINESS_PASS`, `AOT_SMOKE_PASS transport=tcp`, `SHARPPACK_SIDECAR_AOT_PASS`, `PRE_CREDIT_AOT_PASS transport=tcp`, `PRE_CREDIT_AOT_PASS transport=sharedmemory`.
- Trim: `PROJECT_REFERENCE_TRIM_SMOKE_PASS`.
- Dynamic: `Selected 9 dynamic lifecycle cases`, `DYNAMIC_ALC_PLUGIN_TESTS_PASS count=9`.

### Auditable GitHub Actions artifact archives

- [NativeAOT logs](https://github.com/SunSi12138/SharpLink/actions/runs/38103364542/artifacts/11689011560)
- [Trim logs](https://github.com/SunSi12138/SharpLink/actions/runs/38103364542/artifacts/11688821725)
- [Dynamic/ALC logs and selected test identifiers](https://github.com/SunSi12138/SharpLink/actions/runs/38103364542/artifacts/11688981486)

## Relation to earlier validation

The same 45-method set had previously passed [Actions #38064370899](https://github.com/SunSi12138/SharpLink/actions/runs/38064370899), including 2,018/2,018 UnitTests, 269/269 Generator Tests and pristine-`dev` rebuild plus 2,018/2,018 baseline UnitTests.

NativeAOT/trim/ALC checks here **add** runtime and publishing evidence, not merely a C# compilation proof. Still not verified: Windows/macOS NativeAOT, all packaging combinations, external reflection/host uses and comparative GC/performance. **Do not treat the other Tier A/B/C symbols as safe to delete without analogous review.**

## Fifth evidence layer: Windows and macOS NativeAOT

[Passing two-platform Actions #38103915186](https://github.com/SunSi12138/SharpLink/actions/runs/38103915186) independently removed the **same 45 / 46 requested methods** on both operating systems; each completed a full Release solution build with zero warnings/errors and executed the repository's actual NativeAOT smoke suite.

| Platform and RID | Build | Real NativeAOT process smoke |
|---|---|---|
| `windows-latest` / `win-x64` | PASS, 0 warnings/errors | **PASS** (TCP, SharedMemory client/server, generated service, SharpPack adapter, pre-credit TCP and SharedMemory) |
| `macos-14` / `osx-arm64` | PASS, 0 warnings/errors | **PASS** (same process/runtime smoke surfaces) |

- [Windows proof artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38103915186/artifacts/11688578528)
- [macOS proof artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38103915186/artifacts/11688339439)

The first Linux result above therefore forms a three-platform native test matrix for this specific private/internal deletion set.

## Sixth evidence layer: packaged public API and external NuGet consumers

[Passing GitHub Actions #38104003811](https://github.com/SunSi12138/SharpLink/actions/runs/38104003811) independently removed **45 / 46** candidates and completed:

1. Full `Sharplink.slnx -c Release` compile (0 warnings/errors).
2. `dotnet pack Sharplink.slnx --no-build --no-restore -c Release`, yielding SharpLink 2.0.3 NuGet artifacts.
3. [`verify-packages`](https://github.com/SunSi12138/SharpLink/blob/dev/.github/actions/verify-packages/action.yml) for **all eight NuGet packages**, `snupkg` symbols, generator analyzer presence, metadata and declared dependency contracts.
4. [`verify-public-api.sh`](https://github.com/SunSi12138/SharpLink/blob/dev/eng/verify-public-api.sh), matching the **entire public/protected API of all eight packages against the frozen 2.0.0 baseline**, including SDK type forwarding.
5. [`package-smoke`](https://github.com/SunSi12138/SharpLink/blob/dev/.github/actions/package-smoke/action.yml): external package consumer, full trim consumer, partial trim consumer, late-bound trim configuration, packaged NativeAOT consumer and isolated QuickStart / production-template package builds plus QuickStart client/server RPC round-trip. Output contains `PACKAGE_TRIM_SMOKE_PASS` and `QUICKSTART_PACKAGE_SMOKE_PASS version=2.0.3`.

[Package smoke logs artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38104003811/artifacts/11688394425)

**Interpretation:** the exact 45 method deletion set has compelling build/runtime/native/package/ABI evidence across the currently-tested platforms. This does **not** approve a separate public API breaking change such as `IRpcChannel` inheritance or removal of `AddCodec<T>` under the 2.0.0 API baseline; those require a 3.0 major-version migration plan.
