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
