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

## Extended cross-platform AOT, trim and NuGet consumer proof (all four jobs PASS)

[Completed GitHub Actions #38107400102 — four jobs succeeded](https://github.com/SunSi12138/SharpLink/actions/runs/38107400102). Each OS job independently applied the exact runner-only **same production + test migration** script before building and running the original repository NativeAOT smoke suite.

| Check | Actual result |
| --- | --- |
| `ubuntu-latest` / `linux-x64` NativeAOT | PASS: Release build (0 warnings/errors), shared-memory process, TCP, generated service, SharpPack sidecar, PreCredit TCP/SharedMemory |
| `windows-latest` / `win-x64` NativeAOT | PASS: same full native smoke suite |
| `macos-14` / `osx-arm64` NativeAOT | PASS: same full native smoke suite |
| Linux full project-reference `TrimMode=full` consumer | PASS: `PROJECT_REFERENCE_TRIM_SMOKE_PASS` |
| `dotnet pack` all SharpLink 2.0.3 NuGet packages | PASS |
| All eight NuGet package metadata/symbol/dependency checks | PASS |
| Full public/protected API comparison against frozen 2.0.0 baseline | PASS: `Verified the complete public/protected API, including SDK type forwards, of all eight packages.` |
| External package consumers: full/partial trim, late-trim configuration, NativeAOT, QuickStart+ProductionTemplate | PASS: `PACKAGE_TRIM_SMOKE_PASS`, `QUICKSTART_PACKAGE_SMOKE_PASS version=2.0.3` |

Independent raw artifact archives:
- [Linux NativeAOT](https://github.com/SunSi12138/SharpLink/actions/runs/38107400102/artifacts/11689339403)
- [Windows NativeAOT](https://github.com/SunSi12138/SharpLink/actions/runs/38107400102/artifacts/11690054273)
- [macOS ARM64 NativeAOT](https://github.com/SunSi12138/SharpLink/actions/runs/38107400102/artifacts/11689759701)
- [Full Trim + NuGet package and external consumption](https://github.com/SunSi12138/SharpLink/actions/runs/38107400102/artifacts/11690420156)

**Caveat:** this is runtime/API proof for the single test-only decode compatibility queue subsystem after migrating tests to the real reserved production path. It is **not** part of the earlier 45 unrelated dead methods experiment. Real allocation/throughput evidence remains separately tracked.

## Controlled constructor allocation microprobe (quantitative evidence, not throughput proof)

[Successful GitHub Actions #38107583355](https://github.com/SunSi12138/SharpLink/actions/runs/38107583355), [raw baseline + experiment JSON](https://github.com/SunSi12138/SharpLink/actions/runs/38107583355/artifacts/11690283711). The [standalone reflection-based benchmark](https://github.com/SunSi12138/SharpLink/tree/audit/issue-801-dev-3x-deadcode/eng/issue801/DecodeQueueCostProbe) ran **50 warmups + 300 samples** on a single Ubuntu 24.04 runner, .NET 10.0.12. Baseline and modified `ServerDecodeExecutor(workerCount:1, queueCapacity:4)` were compiled/run in the same job, with the exact same caller and reflection argument allocation outside each measured section.

| Metric | Pristine `dev` | Runner-only reserved-only executor |
| --- | ---: | ---: |
| Constructor thread allocation median (300 samples) | **832 B** | **584 B** |
| P10 / P90 (current-thread allocation) | 832 / 832 B | 584 / 584 B |
| Reflection-visible instance field count | 17 | 13 |
| Legacy compatibility instance fields | 4 | 0 |
| Compatibility static scheduling key | Exists | Absent |

**Observed reduction: 248 bytes per executor construction (about 29.8% of the baseline constructor allocation in this probe)**. The original four compatibility fields are `_compatibilityOperations`, `_compatibilityOperationsDrained`, `_compatibilitySlots`, `_compatibilityStop`; the reserved-only code removes all four.

Measurement limitations: reflective `ConstructorInfo.Invoke`, task-worker creation and ThreadPool scheduling may contribute; numbers refer to `GC.GetAllocatedBytesForCurrentThread` only and are **not** retained heap size, complete server startup allocation, RPC throughput, or request-latency improvements. Capture an in-process connection/decompression workload performance benchmark before claiming a request-path performance win.
