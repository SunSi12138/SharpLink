# 08 — Failures, Gaps, and Limitations

This file is deliberately not a summary of “green tests”. Every known failure and missing capability is listed.

## 8.1 Environment failures (not candidate failures)

### P Integration full suite

`528 total / 482 passed / 46 failed / 0 skipped`, exit `2`. Unique names are preserved in `evidence/p-baseline/integration-failure-names.txt`.

- **SharedMemory**: mapping file access denied (`UnauthorizedAccessException`) or handshake EOF. Examples: `BasicRpcAndStreamingShouldWork(True)`, all `SharedMemory*`, `StaticSharedMemoryEndpointsShouldServeRpc`.
- **TLS**: PFX import denied (`CryptographicException: 拒绝访问`). Examples: `TlsShouldRejectExpiredCertificate`, `MutualTlsShouldPreserveClientCertificateContext`.
- **UDS**: bind/delete denied (`SocketException` / `UnauthorizedAccessException`). Examples: `UdsConnectAndBasicRpcShouldWork`, `UdsListenerShouldDeleteOnlyItsBoundPathOnDispose`.

No TCP functional failure was observed in this run. That does not certify the failed tests.

### P Unit / Generator / Load

- Unit `2255 / 2229 / 26 / 0`, exit 2.
- Generator `295 / 294 / 1 / 0`, exit 2 (`ConfiguredManifestOutputShouldWriteTheExactJsonArtifact`).
- Load `77 / 73 / 4 / 0`, exit 2 (temporary-directory/report validation failures).

Failure names are in `evidence/p-baseline/suites/*/failure-names.txt`. These are baseline environment failures; none is attributed to S.

### Runner limitation

`dotnet test --project ...` fails before test discovery with `UnauthorizedAccessException` on the Microsoft.Testing.Platform named pipe. All tests were executed through the compiled test executable (`SharpLink.IntegrationTests.exe`). This is a harness/OS confinement limitation, not a test pass.

## 8.2 Test-oracle failures and corrections

These are preserved, not hidden:

| Attempt | Failure | Classification |
|---|---|---|
| F05 `attempt-1` | plugin loaded before client construction caused `DuplicateAssembly`; rerun reused the same attempt directory and mixed events | **test harness invalid**, not a candidate result |
| F03 `attempt-1` | looked for a contract id in the replacement service-module manifest; sequence had no match | **test oracle invalid** |
| F03 `attempt-2` | compared service-module `RpcAssemblyHash` with contract hash; wrong oracle for service-only replacement | **test oracle invalid** |
| F10 `attempt-1` | second client built after plugin load saw static duplicate; cleanup exception masked the first failure | **test setup invalid** |
| F18 `attempt-1` | test held old `Assembly`/ALC locals; ALC correctly remained alive | **test root, not product leak** |
| F18b `attempt-final-4f92` | same attempt name used for two identical runs, producing two event sequences | **attempt naming invalid**; preserved as `F18b-mixed-attempt`; canonical rerun `attempt-canonical-4f92` |
| 14-row QueuedSelection focused run | `--filter-uid` ran only 1 row (parent UID); the all-row run needed `--treenode-filter` | **selector mistake**, corrected |

Corrected attempts are clearly named `attempt-final-4f92` / `attempt-canonical-4f92` or `attempt-2/3`; the failed/mixed attempts remain in the tree. No assertion was removed to make a failing product path pass. F03's hash assertion was replaced because it was measuring the wrong assembly; the contract-hash observation is now recorded as a fact (`contractHash`, `serviceModuleHash`) rather than asserted as equality.

## 8.3 S candidate failures / regressions

1. **Dynamic registration after connect is broken.**
   - `IndependentControlReplyMustNotAcknowledgeDelayedDataManifest` fails with `Service 5605908966493612427 is not implemented`.
   - `QueuedSelectionRetirementShouldPreserveEveryRpcShape(unary, True)` times out waiting for the admission queue because the request never reaches R1.
   - Cause: the session snapshot is frozen at handshake; the existing client flow registers dynamic assemblies after connecting, and S has no retirement notification on `RegisterAssembly`.
2. **Whole-session collateral.** F09 shows unrelated B is moved to a new physical session even though only A changed.
3. **F18 is now covered for Replace/duplex, not all subcases.** F18b holds a real duplex enumerator across the cut, shows the old session/ALC stays alive and the replacement waits, then releases it and collects the ALC. The Unregister subcase, server-stream `MoveNext`/`Dispose`, producer/CTS tails and codec-fact roots remain `NOT_RUN`.
4. **Control-frame loss is unrecovered.** Source sends one `SessionRefreshRequested`; there is no ack/resend/reconciliation. A lost frame leaves the old session pinned to a draining R, so new calls return `Unavailable` until an external reconnect.
5. **Register/Unregister parity.** `RegisterAssembly` sends no session notification. Unregister retirement was added in `525a5aed` but has no dedicated F-case.
6. **AnonymousPipe.** `SessionRefresh` is not negotiated; retirement is silently skipped.
7. **Full S suite hangs.** At `525a5aed`, the full Integration executable emitted 63 unique failure names and then stopped producing output for >13 minutes after `QueuedSelectionRetirementShouldPreserveEveryRpcShape(client-stream, True)`; it was force-stopped. No final total exists, so the run is `INCOMPLETE/HANG`, not a pass rate. Partial log: `evidence/full-suite/`.
8. **Full F01–F24 not implemented.** F11–F17/F19–F24 are absent; F01/F02/F04 are not implemented as S cases. F08 and a held-duplex F18b were added later and pass, but do not complete the matrix.

## 8.4 Unimplemented arms and stages

- **N**: no `BindingId`, `BindContract`, `BindAck`, `BindingInvalidated`, `BindingRelease`, `RequestAccepted`, wire minor 6, handshake `ServerInstanceId/RegistryEpoch`, receipt or ABA implementation. `NOT_IMPLEMENTED`.
- **Q**: no client send gate, freeze/drain/Resume state machine, FreezeAck, or queue fence. `NOT_IMPLEMENTED`/`NO-GO`.
- **H**: no binding/prewarm candidate. `NOT_IMPLEMENTED`.
- **Performance**: `NOT_RUN`, no A/A, no ABBA/BAAB, no samples.
- **G5/G6**: not entered.

## 8.5 Evidence gaps

- No client-side “SessionRefreshRequested received” event; only the later new-session observation.
- No frame-level control send/ack counters.
- No `Events.jsonl` for the P full suite; only stdout and failure names. The attempted JUnit file was not emitted.
- No GitHub Actions run URLs or job attempts; all tests ran locally. There is no CI artifact.
- No SHA-256 for every raw log yet; the final ZIP manifest will provide a checksum of the bundle.
- No evidence for SharedMemory/TLS/UDS; those transports are capability-limited on this host.
- No Linux, macOS, NativeAOT or Trim run.
- `global.json` deviates from the repo's `10.0.102` to the installed `10.0.404`; all results are therefore not byte-for-byte the frozen SDK of #998.
- The repository was not pushed to GitHub: `gh` is not authenticated and no credential helper/token was available. The audit materials are local plus a planned ZIP.

## 8.6 Untested hazards that could invalidate a future “S works” claim

- A server-stream `MoveNext`/synchronous `Dispose` that blocks after the logical deadline, and the Unregister retirement path; F18b covers a held duplex Replace but not these tails.
- Control frame lost/reordered/duplicate; no test.
- Two replacements back-to-back while the first refresh is in flight; no test.
- Replacement during a long client stream or duplex producer; no test.
- Unregister path retirement; no dedicated test.
- Session refresh failing to connect (server unreachable) and the old session still pinned; no test.
- `Stop` while a pinned session/refresh task is pending; no test.
- Server restart / epoch reuse; no test.

## 8.7 What cannot be concluded

- S is not proven correct.
- S is not proven faster or slower.
- N is not compared.
- Q is not proven impossible; it is simply unimplemented.
- P’s 482 passing integration tests do not prove the 46 failed tests passed, and do not prove N0.
- The C-NEG “PASS” is a successful detection of a deliberately invalid design, not a product pass.
