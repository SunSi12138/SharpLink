# Issue #801 — `ServerDecodeExecutor` test-only compatibility queue removal experiment

## Fixed baseline

- `dev` @ `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`.
- [Executed and passing GitHub Actions run #38107144740](https://github.com/SunSi12138/SharpLink/actions/runs/38107144740).
- [Exact standalone runner-only source/test transformation script](https://github.com/SunSi12138/SharpLink/blob/audit/issue-801-dev-3x-deadcode/eng/issue801/remove-decode-compatibility.py).
- [Build, UnitTests, Generator Tests, IntegrationTests logs and diff/artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38107144740/artifacts/11690108628).
- **No production or test source was committed**. All `src/` and `test/` changes existed only in the GitHub Actions checkout; `dev` and the audit branch production sources are untouched.

## Why it was more than a dead method

Production decompression dispatch uses `TryReserveQueueSlot` and connection-keyed `EnqueueReservedAsync`, which fails to acquire global capacity immediately, before retaining frame bytes. The former `ServerDecodeExecutor.EnqueueAsync` overloads implement a separate compatibility/test-only path: queue publishers **wait** on another SemaphoreSlim when capacity is full. This extra behavior is not the production scheduler's contract and must not be preserved inadvertently through a test wrapper.

Even if the `EnqueueAsync` overloads have no production callers, their infrastructure exists inside every **production ServerDecodeExecutor instance**: a compatibility `SemaphoreSlim`, a `CancellationTokenSource`, a `TaskCompletionSource`, an operation counter, the compatibility key, a queue-entry flag/branch, and disposal/stop/drain logic. The source cleanup removes these as a **cohesive subsystem**, not just public method declarations.

## Exact experiment and migrated tests

- Remove both test compatibility `EnqueueAsync` overloads, `EnqueueCoreAsync` / `EnqueueTrackedCompatibilityAsync`, and their compatibility-only scheduling key, task tracking, CTS, semaphore, TCS, queue-entry flag and release special case.
- Make `ServerDecodeQueueEntry` always hold a non-null `ServerDecodeQueuePermit`; release it unconditionally.
- Simplify `StopAccepting`, `CompleteAsync` and `DisposeAsync` to join only the real worker lifecycle.
- Update all original scheduler race and fairness tests in `test/SharpLink.UnitTests/Server/ServerDecodeExecutorTests.cs` and `ServerDecodeExecutorSchedulerRaceTests.cs` to call the **real** `TryReserveQueueSlot + EnqueueReservedAsync` production path.
- Replace blocked compatibility writer tests with **actual production** global capacity-rejection, queue reservation ownership, stop-before-publication, cancelled queued item and worker-wins-cancellation semantics. Keep fair connection scheduling and 128-cycle ready-wakeup cancellation tests.
- The new tests explicitly distinguish `_queueDepth` published work vs `_queueReservations` pre-publication plus published capacity.
- Delta in the runner was **3 changed files; 137 inserted lines, 318 deleted lines (net −181 lines)**.

## Actual test results

| Verification | Result |
|---|---|
| Complete Release `Sharplink.slnx` build with test-only compatibility path absent | **PASS — 0 warnings / 0 errors** |
| Full UnitTests against migrated production queue API | **PASS — 2018/2018** |
| Full Generator Tests | **PASS — 269/269** |
| Full IntegrationTests with `--maximum-parallel-tests 1 --timeout 120s` | **PASS — 453/453** |
| Restore original `src/` and `test/` and rebuild pristine dev code | **PASS — 0 warnings / 0 errors** |

## Next safety gates

- Cross-platform NativeAOT and real external NuGet/trim packaged consumers on the modified code: [Actions #38107400102](https://github.com/SunSi12138/SharpLink/actions/runs/38107400102).
- Actual benchmark evidence for idle executor construction, allocations/GC and hot path queue metadata reclamation is not yet available; improvements are anticipated but **not measured**.
- Wire protocol, timing-sensitive shutdown and cross-platform thread scheduling are critical invariants; retain the new production-path race tests.
- Once all gates pass, open a separate reviewable cleanup PR against `dev`, showing exactly the production code and test changes, without committing the provisional experiments directly to `dev`.

This is a separate clean-up group from the independently validated **45 zero-direct-reference private/internal methods**; neither result licenses deleting all Tier A or Tier B symbols.
