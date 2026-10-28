> 技术报告副本：仅省略内部审核者任务路径；正文与证据结论未改。完整原报告 SHA-256：`c66d659a1b29d2e07bd353efffd0398d0cd316a3aeebc7b0dafeb515c2d7e97b`。

# PR #961 independent final technical-gate review

**Verdict: PASS for the currently applicable PR technical gates at exact HEAD `1a76e1b858b9b306f8d09ed6f85bc3c5319e2da5`.** No current-head applicable job remains pending or failed. This is not GitHub APPROVE, merge authorization, a release decision, browser acceptance, or a performance-gain claim. User confirmation to merge #961 is still required.

Evidence checked 2026-10-28 UTC. Read-only source/remote/CI audit; no CLR execution, CI rerun, product edit, approval, or merge performed during this audit.

## Exact source binding

- PR HEAD: `1a76e1b858b9b306f8d09ed6f85bc3c5319e2da5`; sole parent `b7a42bb2c994d2b7472d404e686ed7020d58f418`.
- Actual checked-out test merge: `2f6c19233ac3896df6f92193519c8e4bc78e310c`; parents `e3808b65e694bf1ed250d55fa2ad48a6ef88d285` and the PR HEAD.
- Both full trees equal `e5b72625377ffdcf5f5470e2ddd69be12a449d8e`, exactly matching independently reviewed local `555cdb9f8e7230b555257ddc86ce8fcb4dc0181d`.
- Remote refs were fetched read-only and commit parents/trees inspected. PR API identity and main Extended checkout log agree (`extended-job.log`, lines 129–132; `git-identities.txt`, `pr-info.json`). Snapshot is open, unmerged, Ready.

## Extended: actual jobs and raw test output

[Run 40706353475](https://github.com/SunSi12138/SharpLink/actions/runs/40706353475), main [job 122879432677](https://github.com/SunSi12138/SharpLink/actions/runs/40706353475/job/122879432677).

- Actual 24 jobs: **16 success, 8 skipped**. Main Extended, six native-platform producers, six verifiers, codec summary and final summary all succeeded. The eight browser jobs were optional and skipped with browser evidence disabled; they are not counted as passed.
- Main job built Debug and Release; Release reported zero warnings/errors. Debug rollback test: 1/1, zero failure/skip.
- Full Integration command had **no test filter**: `dotnet run -c Release --no-build --project test/SharpLink.IntegrationTests -- --maximum-parallel-tests 1 --timeout 120s`.
- Raw native runner summary: **491 total, 491 succeeded, 0 failed, 0 skipped**, duration 36.755 seconds (`extended-job.log`, lines 431–435).
- Strengthened `SharedMemoryHandshakeShouldRejectMismatchedAcknowledgementNonceAndCleanMapping` remains an ordinary `[Test]` in exact source (`test/SharpLink.IntegrationTests/SharedMemoryTransportConnectionIntegrationTests.ProcessAndHandshake.cs`, lines 268–356). No skip or filtering excludes it from this full run.
- **Evidence precision:** Extended does not upload its Integration per-case native JSON/HTML, and its stdout does not enumerate passing names. Inclusion is supported by exact source plus the unfiltered full-suite invocation and zero-skip census, not a claimed inspection of an unavailable full per-case report. Separate same-tree hosted diagnostic native JSON directly names this strengthened test as passed: `nonce-focused-native-report.json`, 1 total/1 passed/0 failed/skipped/cancelled/timedOut/flaky, .NET 10.0.14, commit `2f6c192…`.
- Main log additionally confirms shared-memory process/topology/sidecar/precredit NativeAOT smokes; nine package public/protected API and exact SDK forwarding inventories; nine package/symbol pairs at the same merge commit; package/trim/AOT, Quickstart and Demo smokes. Local shared-memory add smoke at concurrency 1 and 4 recorded zero failures/retries. These are correctness/smoke evidence, not a performance improvement finding.

## Six native codec reports: independently checked artifact contents

Downloaded aggregate artifact `12823688360` and all six consumer verification artifacts; each ZIP SHA-256 matched the Actions artifact metadata. Each report binds to merge `2f6c192…`, .NET 10.0.14 / SDK 10.0.114 and its native OS/architecture.

Platforms: Linux x64/arm64, Windows x64/arm64, macOS x64/arm64. Each consumer has 348 rows spanning all six producers. The six raw reports and aggregate match **row-for-row**, not merely by success status: **2088 unique producer/consumer/fixture rows = 36 ordered pairs × 58 fixtures**. Zero blocking failures; every row is `IDENTICAL_BYTES_AND_COMPATIBLE`, with cross-deserialization, logical equality and byte equality true. Segmented checks are true for 2016 rows; 72 Byte/ByteEnum rows are explicitly null/not applicable, not represented as passed segmented checks.

Artifacts: `codec-verification-*.zip`, `codec-summary.zip`, `codec-summary.json`; independently calculated checks in `codec-independent-verification.json`. Remote metadata in `extended-artifacts.json`; local evidence hashes in `SHA256SUMS.json`.

## Other applicable CI at this exact HEAD

Remote exact-commit workflow query returned the following 11 runs. Every actual job was queried, not only the aggregate badges. All are terminal success except the eight explicitly optional browser skips above. Empty legacy combined-status entries are not treated as a substitute for Actions results.

| Run | Workflow | Terminal state |
|---|---|---|
| [40706353475](https://github.com/SunSi12138/SharpLink/actions/runs/40706353475) | PR Extended | completed / success |
| [40704347752](https://github.com/SunSi12138/SharpLink/actions/runs/40704347752) | Generated Stream Snapshot Ownership | completed / success |
| [40704347825](https://github.com/SunSi12138/SharpLink/actions/runs/40704347825) | Send Credit Terminal Accounting | completed / success |
| [40704347751](https://github.com/SunSi12138/SharpLink/actions/runs/40704347751) | Nonce Recovery Deterministic Diagnostic | completed / success |
| [40704347992](https://github.com/SunSi12138/SharpLink/actions/runs/40704347992) | PR Fast | completed / success |
| [40704347770](https://github.com/SunSi12138/SharpLink/actions/runs/40704347770) | RPC Session Flush Performance Evidence | completed / success |
| [40704347755](https://github.com/SunSi12138/SharpLink/actions/runs/40704347755) | Send Credit Lease Race | completed / success |
| [40704348022](https://github.com/SunSi12138/SharpLink/actions/runs/40704348022) | API Inventory | completed / success |
| [40704347880](https://github.com/SunSi12138/SharpLink/actions/runs/40704347880) | CodeQL | completed / success |
| [40704347766](https://github.com/SunSi12138/SharpLink/actions/runs/40704347766) | PR Package Smoke | completed / success |
| [40704347764](https://github.com/SunSi12138/SharpLink/actions/runs/40704347764) | Packaged Trim and NativeAOT | completed / success |

Raw PR Fast log additionally confirms production reference-boundary guard (10 projects, 15 declarations/references), whitespace formatting, strict census parser/offline checks, maintainability gate (1261 files; 17 historical oversized files within baseline), and Unit **2188/2188**, Generator **294/294**, LoadTest tests **77/77**, all zero failure/skip. API Inventory and allocation jobs succeeded. Three packaged consumer raw logs confirm full/partial trimming and NativeAOT on linux-x64, win-x64 and osx-arm64, including generation-control serialization. Workflow success for RPC flush evidence is not independently asserted here as a numeric optimization benefit.

See `head-runs.json`, `extended-jobs.json`, `jobs-*.json` and selected `job-*.log`. Repository branch-protection policy/approval satisfaction was not audited or modified; this verdict concerns technical gates, not permission to merge.

## Prior failure and auditability limits remain visible

1. **Old Extended remains failed:** run `40697027111`, job `122846441414`, 491 total/490 pass/1 fail/0 skip, genuine nonce-recovery Broken pipe. Preserved as `historical-extended-122846441414.log`; it is not relabeled a census failure, unrelated failure, or success.
2. Hosted deterministic diagnostic run `40704347751`, artifact `12822822111` proved a possible Unix old-listener-backlog race and fixture sensitivity: gated early client failed with Unavailable/IOException; waiting for rearm succeeded bidirectionally, one connection per arm; fixed fixture passed; removing nonce rejection and blocking rearm each caused the specific expected negative failure. It does not prove the unique historical cause. Artifact ZIP digest `a80184adb09045abe7cf7ee2d51203628e7831ad88f03ba0e238d911e0a7d225` and all 51 manifest entries were previously independently verified; copy retained here. Shipping transport source was not changed by diagnostic overlays.
3. **First local native report is missing:** focused001 failed an initial iteration timeout, and its native JSON/HTML was overwritten by focused002 before preservation. Raw stdout/stack/build logs remain; no reconstruction or rerun claimed. Second local attempt failed with sandbox Permission denied before the first accept hook, with its native reports retained. See `historical-local-nonce-attempts/manifest.json` and copied logs/reports. Its phrase “hosted pending” is the historical snapshot, superseded by the distinct later hosted result, not silently rewritten.
4. Full Integration lacks archived native per-case reports, as described above. Optional browser execution remains unperformed in this run. No universal transport no-replay, exactly-once delivery, automatic reconnect success during teardown, all-platform release readiness, or 3.0 release completion is inferred.
5. Nonblocking publication discrepancy: observed PR body still labels `b7a42bb2…` as current HEAD and describes nonce diagnosis as pending. Actual API HEAD and CI evidence are newer; author should update the body to these exact results while retaining the historical failures and evidence gap.

## Disposition

No additional correctness blocker found in this final gate/evidence audit. **Technical PASS is limited to this exact source tree and current applicable CI.** Preserve all historical outcomes; update the stale PR narrative. Root/user must separately authorize merge. No approval or merge action was taken.
