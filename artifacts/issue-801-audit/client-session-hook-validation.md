# Issue #801 — lazy storage for ten client session refresh / failure test hooks

## Current production cost

`SharpLinkClient` (an internal client implementation) currently contains **10 separate per-instance test-only delegate reference fields** in [`src/SharpLink.Client/SharpLinkClient.SessionRefresh.cs`](https://github.com/SunSi12138/SharpLink/blob/dev/src/SharpLink.Client/SharpLinkClient.SessionRefresh.cs#L10). They support deterministic UnitTests for session eligibility, replacement, cut publication, fatal-error publication, worker release and admission reservation. They are read by production control-flow methods, including through the static-cluster and dynamic-cluster clients, but the hook values are installed only by tests. Ordinary production client instances still reserve storage for all 10 pointers.

## Runner-only experiment

A [reproducible Python transformation](https://github.com/SunSi12138/SharpLink/blob/audit/issue-801-dev-3x-deadcode/eng/issue801/compact-client-session-hooks.py) modified **three** source files **only inside a GitHub Actions runner**:

1. Replace 10 `Action?/Action<ClientConnection>?/Action<ClientConnection,ClientConnection>?` *fields* with **one nullable `Delegate?[]?` pointer**.
2. Expose the original 10 `internal` member names as corresponding strongly typed properties with getter/setter, so the existing race tests can assign callbacks and clear them without changing test sources.
3. Allocate the 10-slot backing array only when a non-null test callback is first installed. Normal production clients never initialize it.
4. Retain volatile publication/read semantics, CAS-protected array initialization and the existing 10 race checkpoint invocations.

This is an internal member layout change, *not* an external 3.0 public API change. On x64, `(10 - 1) × 8 B` gives a **theoretical 72-byte per-client field reduction**, not a measured full managed-object size or throughput improvement.

## Real execution

[GitHub Actions #38107812236 — all stages successful](https://github.com/SunSi12138/SharpLink/actions/runs/38107812236)

| Check | Result |
| --- | --- |
| Full `Sharplink.slnx` Release build | **PASS — 0 warnings / 0 errors** |
| Full UnitTests, including client session-refresh race tests | **PASS — 2,018 / 2,018** |
| Generator Tests | **PASS — 269 / 269** |
| Full IntegrationTests | **PASS — 453 / 453** |
| Linux x64 NativeAOT publish/run, including TCP, shared memory, generated service, SharpPack and pre-credit streaming | **PASS** |
| Restore original `src/` and rebuild pristine baseline | **PASS — 0 warnings / 0 errors** |

[Complete build/test/AOT proof ZIP](https://github.com/SunSi12138/SharpLink/actions/runs/38107812236/artifacts/11690154884)

## Limitations and next decision

- This run did **not** measure per-client allocations, full object size, throughput or tail latency. A callback read now checks a lazily populated delegate-array reference and indexes the array only when installed; benchmark client admission/session-refresh hot paths before adoption.
- NativeAOT was checked on Linux only for this separate hook refactor. Cross-platform AOT and NuGet public API checks should accompany any implementation PR.
- Do not replace the hooks with `#if DEBUG` because unit tests currently execute in Release and rely on these deterministic race points.
- The hooks should be retained as internal properties while test helpers still reference them. This experiment demonstrates that their production *storage* can be compacted without losing test coverage.
- No production source was committed; `dev` remains untouched.

This is separate from the prior nine-field `PooledAsyncStreamDispatcher<T>` proof and from the 45 zero-direct-reference internal methods or the `ServerDecodeExecutor` compatibility queue teardown.
