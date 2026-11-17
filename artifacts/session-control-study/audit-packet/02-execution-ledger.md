# 02 — Execution Ledger (G0–G7)

All times are Asia/Hong_Kong unless stated. `P` means the frozen `dev` product code plus the toolchain-only `global.json` commit `b499d2cf`; `S` means the research prototype branch. A status is not upgraded because a later rerun passed: every failed attempt remains in the artifact tree.

## Environment / baseline

| Item | Value |
|---|---|
| Repository | `SunSi12138/SharpLink` |
| `dev` HEAD at start | `2948de82fff77d66fce51ecec9a5783b6ef3cb62` |
| Worktree at start | clean |
| Host | Windows 11 Pro for Workstations 10.0.26200 x64; Ryzen 9 7950X (16C/32T); 64 GiB |
| SDK requested by repo | `10.0.102` (`global.json`, `latestPatch`) |
| SDK installed | `10.0.404`, `11.0.100`; exact `10.0.102` absent |
| Resolution | user instructed use of local .NET 10; research commit `b499d2cf` changes only `global.json` to `10.0.404` |
| Runtime under test | `Microsoft.NETCore.App 10.0.15` (TUnit banner) |
| Test runner | TUnit 1.73.19 / Microsoft.Testing.Platform 2.5.1 |
| Direct runner | `test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.exe` (required because `dotnet test` IPC named pipes are denied by the sandbox) |

## G0 — baseline and source freeze

Commands and results:

| Command | Exit | Evidence |
|---|---:|---|
| `git rev-parse HEAD` | 0 | `2948de82...`; `logs/g0/git-rev-parse-head.txt` |
| `git status --porcelain` | 0 | empty; `logs/g0/git-status-porcelain.txt` |
| `dotnet --info` | 0 | `logs/g0/dotnet-info-system.txt` |
| `dotnet restore Sharplink.slnx` | 0 | `logs/g0/restore.log` |
| `dotnet build Sharplink.slnx -c Release --no-restore -v minimal` | 0 | 0 warnings, 0 errors; `logs/g0/build.log` |

Result: `G0 = PASS` for inventory. The baseline source has no N0 `BindingId`; `ReplaceAssemblyAsync` published the new `_services` at line 307 and only then called `oldModule.TryBeginDraining()` at line 310. This was recorded before any product edit.

## G1 — executable model

Command:

```text
dotnet run --project eng/session-control-study/Model/Model.csproj -c Release
```

Observed stdout:

```text
MODEL PASS: notification-only hazard observed; admission ordering holds
```

Exit: `0`. Artifact: `artifacts/session-control-study/g1-model/attempt-1/stdout.log`.

Interpretation: the model confirms (a) a notification-only client can dispatch an old request into a new registration and (b) a cut gate rejects new entrants while an already-admitted call survives. It does **not** exercise SharpLink frames, Sessions, ALC, queues or transport, and is not a candidate acceptance result.

## G2 — unchanged P real integration tests

### Full Integration suite

Command:

```text
SharpLink.IntegrationTests.exe --no-ansi --output Normal --show-test-results failed
  --minimum-expected-tests 500 --timeout 45m
  --junit-output-path artifacts/.../integration-junit.xml
```

Observed summary:

| Total | Passed | Failed | Skipped | Exit |
|---:|---:|---:|---:|---:|
| 528 | 482 | 46 | 0 | 2 |

The runner did not emit the JUnit file because the process ended with a non-success code; the full stdout is preserved. All 46 unique failure names are in `artifacts/session-control-study/g2-baseline/attempt-1/failure-names.txt`. Every failure is an environment/transport class:

- SharedMemory mapping file access denied (`UnauthorizedAccessException`), including `BasicRpcAndStreamingShouldWork(True)`, `SharedMemory*`, and `StaticSharedMemoryEndpointsShouldServeRpc`;
- TLS PFX import denied (`CryptographicException: 拒绝访问`), all `Tls*`/`MutualTls*` failures;
- UDS bind/delete access denied (`SocketException`/`UnauthorizedAccessException`), all `Uds*` failures.

No TCP functional test failure was observed. This does **not** mean the 46 tests passed; they are `FAIL` in this environment and SharedMemory/TLS/UDS support is therefore `INCONCLUSIVE`.

### Focused P tests cited by #998

| Test | Total | Passed | Exit | Artifact |
|---|---:|---:|---:|---|
| `IndependentControlReplyMustNotAcknowledgeDelayedDataManifest` | 1 | 1 | 0 | `g2-baseline/focused/IndependentControlReply/stdout.log` |
| `ExplicitReplacementShouldWaitForNewGeneratedHashAcrossIndependentConnections` | 1 | 1 | 0 | `.../ExplicitReplacement/stdout.log` |
| `QueuedSelectionRetirementShouldPreserveEveryRpcShape` (all 14 rows, `--treenode-filter`) | 14 | 14 | 0 | `.../QueuedSelectionRetirementAll14/stdout.log` |
| `QueuedSelectionShouldNotRootRetiredCollectibleService` | 1 | 1 | 0 | `.../QueuedSelectionCollectible/stdout.log` |

A first focused attempt of the 14-row test used `--filter-uid` with the parent UID and ran **1** row only; that run is retained as a selector mistake, and the all-row run used `--treenode-filter "/*/*/RuntimeAssemblyIntegrationTests/QueuedSelectionRetirementShouldPreserveEveryRpcShape"`.

### P Unit / Generator / Load

| Suite | Total | Passed | Failed | Skipped | Exit | Evidence |
|---|---:|---:|---:|---:|---:|---|
| Unit | 2255 | 2229 | 26 | 0 | 2 | `g2-baseline/suites/Unit/stdout.log` |
| Generator | 295 | 294 | 1 | 0 | 2 | `g2-baseline/suites/Generator/stdout.log` |
| Load | 77 | 73 | 4 | 0 | 2 | `g2-baseline/suites/Load/stdout.log` |

Failure names are preserved in the same directories. They are dominated by SharedMemory/TLS/UDS/temporary-path ACL failures, plus `ConfiguredManifestOutputShouldWriteTheExactJsonArtifact` and four load-report validator failures. These are baseline environment failures, not S regressions.

## G3 — S prototype

Commits:

| Commit | Purpose |
|---|---|
| `58c280f4ef02c0620d510cc1a78139f2ec5e80ce` | initial S prototype + F05/F06/F07 tests |
| `5b8d5fd269cf7d1e48172be1e1d07e13593d74d1` | F03/F09/F10/F18 + test harness fixes |
| `525a5aed918516ac1214a91c99fbb3fbb3a746e9` | final S product: cut before new snapshot publication; unregister retirement |
| `4f927b40ddac622d76cba45d23b173aa0c9da31c` | test-only: held-duplex ALC release control (F18b) |
| `33fb48c232e7bf3843dbb3ab34420e97ca6435ff` | test-only: start-claim vs cut control with handler gate (F08) |
| `948fb44d00748e2c8ba1813aa31c44d23616aa57` | test-only: changed-hash old-client fail-closed control (F04) |
| `7d21b89a86d95f79b0c2d5397ca737bcadf668c6` | test-only: three-cycle rapid replacement control (F11) |

Important first failures (all preserved):

| Case | Attempt | Result | Cause |
|---|---|---|---|
| F05 | `attempt-1` | invalid mixed record | plugin loaded before client build caused duplicate registration; first run failed, second run reused the same directory. Not used as evidence. |
| F03 | `attempt-1` | FAIL | test oracle searched replacement service-module manifest for a contract id; invalid oracle |
| F03 | `attempt-2` | FAIL | test compared the service-module hash to the contract hash; invalid oracle |
| F10 | `attempt-1` | FAIL | second client built after plugin load saw a static duplicate; cleanup then threw a dependency error and masked the first failure |
| F18 | `attempt-1` | FAIL | test held the old collectible `Assembly`/`AssemblyLoadContext` local, rooting the ALC |
| F18 | `attempt-2`/`attempt-final-1` | PASS | idle replacement only; held-stream subcase NOT_RUN |

Final S core results at test commits `4f927b40` / `33fb48c2` / `948fb44d` / `7d21b89a` (product source `525a5aed`):

| Case | Test method | Attempt | Status | Exit |
|---|---|---|---|---:|
| F03 | `Study_F03_SameContractSameHashReplacementMustChangeSession` | `attempt-final-4f92` | PASS | 0 |
| F04 | `Study_F04_OldClientWithChangedContractHashMustNotEnterR2` | `attempt-final-948f` | PASS (fail-closed; no replacement session) | 0 |
| F05 | `Study_F05_StaleRequestAfterCutBeforeNotificationMustNotEnterR1OrR2` | `attempt-final-4f92` | PASS | 0 |
| F06 | `Study_F06_AdmittedOldCallMustCompleteAfterCut` | `attempt-final-4f92` | PASS | 0 |
| F07 | `Study_F07_QueuedRequestLosingToCutMustEnterNeitherR1NorR2` | `attempt-final-4f92` | PASS | 0 |
| F08 | `Study_F08_StartClaimWinsCutWithHandlerSchedulingHeld` | `attempt-final-33fb` | PASS | 0 |
| F09 | `Study_F09_UnrelatedServiceOnSameSessionIsCollateralInS` | `attempt-final-4f92` | PASS (records collateral cost) | 0 |
| F10 | `Study_F10_TwoIndependentSessionsMustBothMoveToNewSnapshot` | `attempt-final-4f92` | PASS | 0 |
| F11 | `Study_F11_ThreeRapidReplacementsMustRetireEachSession` | `attempt-final-7d21` | PASS (three sequential cycles; late ack NOT_RUN) | 0 |
| F18 | `Study_F18_RetiredServiceAlcMustNotBeRootedBySessions` | `attempt-final-4f92` | PASS (idle) | 0 |
| F18b | `Study_F18b_HeldDuplexMustRetainThenReleaseOldAlc` | `attempt-canonical-4f92` | PASS (held duplex) | 0 |

F18b is the held-stream control that was missing earlier: it holds a real duplex enumerator across the cut, verifies the old session/ALC is retained and the replacement is blocked, then releases the stream and verifies the old ALC is collectible. F08 uses a real server interceptor to hold the handler after admission; the cut occurs while the call is pending, and release returns the original R1 result 2. F04 replaces the contract assembly with a changed hash; the old client remains on the old session, no replacement session becomes Ready within 2 s, and its call fails `Unavailable` without entering R2. The first F18b directory (`attempt-final-4f92`) accidentally contains two identical runs and is preserved as `F18b-mixed-attempt`; the canonical evidence is `attempt-canonical-4f92`.

### Full S integration suite at `525a5aed`

The full suite was started on the final S binary. It produced **no final summary** and did not terminate: after `QueuedSelectionRetirementShouldPreserveEveryRpcShape(client-stream, True)` failed at 3 s, the process remained alive with no new output for more than 13 minutes. It was force-stopped and the partial log preserved.

- Partial unique failure names: **63** in `evidence/full-suite/failure-names.txt`.
- Total executed count is unknown because no summary was emitted; do **not** compute a pass rate from the partial log.
- Classification: `INCOMPLETE / HANG`, not PASS, not a valid regression count.
- Important observed failures in the partial log include `IndependentControlReplyMustNotAcknowledgeDelayedDataManifest`, `ExplicitReplacementShouldWaitForNewGeneratedHashAcrossIndependentConnections`, all observed `QueuedSelectionRetirementShouldPreserveEveryRpcShape` rows, and many physical-tail/ALC tests that register dynamic services after connect.

This hang is itself a STOP finding for S.

### S regressions on unchanged P tests

| Test | Result | Evidence |
|---|---|---|
| `QueuedSelectionRetirementShouldPreserveEveryRpcShape(unary, True)` | FAIL — queue condition timed out because the session pinned a snapshot with no dynamically registered service | `g3-s/regression-queued-selection/stdout.log` |
| `IndependentControlReplyMustNotAcknowledgeDelayedDataManifest` | FAIL — `SharpLinkException: Service 5605908966493612427 is not implemented` | `g3-s/regression-independent-control/stdout.log` |

These are not optional styling regressions: they demonstrate that S changes the existing dynamic-registration-after-connect behavior.

## C-NEG — negative control on P

P worktree: `C:\Users\sunsi\Desktop\test\sharplink-study\SharpLink-P`, product commit `b499d2cf` (same product source as `dev`), plus a copied test-only C-NEG file.

Command:

```text
SharpLink.IntegrationTests.exe --no-ansi --output Normal --show-test-results failed
  --filter-uid "SharpLink.IntegrationTests.RuntimeAssemblyIntegrationTests.1.1.C_NEG_StaleRequestAfterReplaceCommitBeforeNotificationHitsR2.1.1.0"
  --timeout 2m
```

Observed: 1 total, 1 passed, exit 0. This “pass” means the test **successfully caught the intentional hazard**:

- stale request result: `2` (R2 business result);
- `oldR1Created = 0`; `newR2Created = 1`;
- broadcast barrier was held after the registry commit.

Evidence: `SharpLink-P/artifacts/session-control-study/C-NEG/C-NEG-F05/attempt-1/` (copied into the audit packet as `evidence/c-neg/`).

## G4 — architecture review

Performed after the final S behavior was observed. Result: **S is NO-GO as a same-session replacement for N0**. The detailed reasons are in `09-final-comparison.md` and `03-source-review.md`; the main ones are the permanent-`Unavailable` behavior when a refresh frame is lost, the lack of Register/Unregister parity, whole-session collateral loss, and the existing dynamic-registration regressions.

## G5/G6 — not run

- N: `NOT_IMPLEMENTED` — no N0 binding frames or exact-token implementation exists on `dev`; the historical N1 hosted run in #775 is not a current candidate.
- Q: `NOT_IMPLEMENTED` / `NO-GO` — no freeze/send-gate/drain/FreezeAck/Resume state machine exists.
- H: `NOT_IMPLEMENTED`.
- S performance: **NOT_RUN**. S did not complete the full F01–F24 matrix; #998 forbids performance before the full correctness matrix. No A/A or ABBA run was started.
- C-NEG performance: forbidden by definition.

## G7 — audit packet

This directory. No conclusion is marked as confirmed engineering; the final decision remains `PENDING_INDEPENDENT_AUDIT`.
