# Issue #801 — production per-dispatcher test-hook field compaction experiment

## Source and scope

Based on `dev` @ `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`, performed **only in an ephemeral GitHub-hosted Actions checkout**. Neither `src/` on the audit branch nor `dev` was modified.

- [Complete passing run #38103482749](https://github.com/SunSi12138/SharpLink/actions/runs/38103482749)
- [Exact Python mutation/probe](https://github.com/SunSi12138/SharpLink/blob/audit/issue-801-dev-3x-deadcode/eng/issue801/compact-test-hooks.py)
- [Build and test output archive](https://github.com/SunSi12138/SharpLink/actions/runs/38103482749/artifacts/11688488097)
- Source: [`PooledAsyncStreamDispatcher<T>`](https://github.com/SunSi12138/SharpLink/blob/dev/src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs#L53)

## Motivation and change

The pooled production dispatcher contains **nine per-object `Action?` test-only fields**, even when no callback is ever installed by a production stream. Both initial reset and return-to-pool reset write nine null values. The unit test hooks also have specific setter and invocation points which current Release concurrency tests need, so deleting hooks wholesale or wrapping them in `#if DEBUG` would discard valuable test coverage.

Ephemeral experiment replaced the nine reference fields with **one nullable `Action?[]?` instance reference**, lazily allocated only on the first non-null test callback installation. The nine existing internal setter method signatures, nine callback invocation points, volatile publication and pool reference release remained intact. The current `HasRetainedReferencesForTests` invariant still checks that no hooks survive pool return.

Theoretical x64 **instance field savings**: `(9 - 1) * 8 bytes = 64 bytes per instance` (not a measured object-size or throughput number). Test-only first-use allocates an array and one indirection; production receives only the single null array-field check. We have **not measured** the JIT-inlined fast path throughput change.

## Actual CI outcome

| Check | Outcome |
|---|---|
| Pristine full solution Release build | PASS, 0 warnings/errors |
| Experimentally modified full solution Release build | **PASS, 0 warnings/errors** |
| UnitTests with lazy hook storage | **PASS: 2,018 / 2,018** |
| Generator Tests with lazy hook storage | **PASS: 269 / 269** |
| Pristine baseline rebuild after source restoration | **PASS, 0 warnings/errors** |
| Pristine baseline UnitTests | **PASS: 2,018 / 2,018** |
| Dev branch / audit branch src touched? | **No** |

## Follow-up before production adoption

1. Compare end-to-end RPC/stream microbenchmarks, latency, retained heap and allocation profiles, not theoretical field layout alone.
2. Probe trim/NativeAOT after the compacted test-hook implementation (not done in this run).
3. Run ALC collectible unload/lifetime cases with pooled dispatcher churn.
4. Ensure all nine race callback modes remain usable in Release tests, and pooled re-rent cannot retain stale callback roots.
5. Consider a separate 3.0 PR for the lazy storage implementation. It is **not** one of the original 45 outright unreferenced methods; it is a separate test-only production-footprint improvement.
