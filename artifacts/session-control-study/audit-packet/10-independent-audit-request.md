# 10 — Independent Audit Request

Status: **PENDING_INDEPENDENT_AUDIT**. The experimenter’s conclusions must not be treated as confirmed until this audit is completed.

## 10.1 Links and exact sources

| Item | Value |
|---|---|
| Experiment issue | https://github.com/SunSi12138/SharpLink/issues/998 |
| Main design issue | https://github.com/SunSi12138/SharpLink/issues/775 |
| N0 contract | https://github.com/SunSi12138/SharpLink/blob/2948de82fff77d66fce51ecec9a5783b6ef3cb62/doc/issue-775-n0-contract.md |
| Repository | https://github.com/SunSi12138/SharpLink |
| Baseline `dev` SHA | `2948de82fff77d66fce51ecec9a5783b6ef3cb62` |
| Research branch | `study/998-session-control` |
| Branch URL | https://github.com/SunSi12138/SharpLink/tree/study/998-session-control |
| Toolchain-only commit | https://github.com/SunSi12138/SharpLink/commit/b499d2cfff22a424ea3448c5be73fbeba49bcb99 |
| S initial prototype | https://github.com/SunSi12138/SharpLink/commit/58c280f4ef02c0620d510cc1a78139f2ec5e80ce |
| S test matrix commit | https://github.com/SunSi12138/SharpLink/commit/5b8d5fd269cf7d1e48172be1e1d07e13593d74d1 |
| S final product source | `525a5aed918516ac1214a91c99fbb3fbb3a746e9` |
| S final test/evidence commit | `7d21b89a86d95f79b0c2d5397ca737bcadf668c6` |
| F18b test commit | `4f927b40ddac622d76cba45d23b173aa0c9da31c` |
| F08 test commit | `33fb48c232e7bf3843dbb3ab34420e97ca6435ff` |
| F04 test commit | `948fb44d00748e2c8ba1813aa31c44d23616aa57` |
| Final S product commit URL | https://github.com/SunSi12138/SharpLink/commit/525a5aed918516ac1214a91c99fbb3fbb3a746e9 |
| Final test commit URL | https://github.com/SunSi12138/SharpLink/commit/7d21b89a86d95f79b0c2d5397ca737bcadf668c6 |
| S vs baseline compare | https://github.com/SunSi12138/SharpLink/compare/2948de82fff77d66fce51ecec9a5783b6ef3cb62...525a5aed918516ac1214a91c99fbb3fbb3a746e9 |

The branch contains only source/tests/model changes. No `dev` branch commit was made and no PR was opened.

## 10.2 GitHub Actions

There are **no GitHub Actions runs** for this study. All runs were local Windows executions. Do not accept a claim of CI coverage; there is none. The absence is itself an audit finding for the “Actions URL/job/attempt/artifact” requirement.

## 10.3 Audit packet and raw evidence

Audit packet:

```text
artifacts/session-control-study/audit-packet/
  01-baseline.md
  02-execution-ledger.md
  03-source-review.md
  04-correctness-matrix.md
  05-concurrency-timeline.md
  06-performance.md
  07-resource-lifecycle.md
  08-failures-and-limitations.md
  09-final-comparison.md
  10-independent-audit-request.md
  evidence/
    g1-model/
    c-neg/
    s-core/
    regressions/
    p-baseline/
    source/
    binaries.sha256
```

Raw logs not suitable for GitHub are in the same study tree and in the planned ZIP. If the packet is not in the pushed branch, the ZIP plus its SHA-256 is the authoritative copy.

## 10.4 Exact commands and exit codes

The key commands are in `02-execution-ledger.md`. Summary:

| Command class | Exit |
|---|---:|
| `git rev-parse HEAD` / `git status --porcelain` | 0 |
| `dotnet restore Sharplink.slnx` | 0 |
| `dotnet build Sharplink.slnx -c Release --no-restore -v minimal` | 0 (0 warnings, 0 errors) |
| G1 model | 0 (`MODEL PASS`) |
| P full Integration suite | 2 (528/482/46/0; environment failures) |
| P Unit / Generator / Load | 2 / 2 / 2 (environment and fixture failures) |
| P focused `IndependentControlReply` | 0 (1/1) |
| P focused `ExplicitReplacement` | 0 (1/1) |
| P all 14 `QueuedSelectionRetirement...` rows | 0 (14/14) |
| P `QueuedSelectionShouldNotRoot...` | 0 (1/1) |
| C-NEG on P | 0 (test passed by detecting the deliberate hazard) |
| S core cases at `4f927b40` (F03/F05/F06/F07/F09/F10/F18/F18b) | 0 each |
| S F08 at `33fb48c2` | 0 |
| S F04 at `948fb44d` | 0 (fail-closed; no replacement session) |
| S F11 at `7d21b89a` | 0 (three sequential cycles; late ack NOT_RUN) |
| S full integration suite | **INCOMPLETE/HANG**: 63 unique failure names observed, no final summary; force-stopped after >13 min without output; see `evidence/full-suite/` |

## 10.5 Most important failures and risks

1. **C-NEG hazard is real on P.** After Replace commit, before the client manifest notification, a stale request executes R2 (`value=2`, R1 Created=0, R2 Created=1). This is not an inference.
2. **S is narrow, not general.** Seven required core cases, F18b held duplex, F08, F04 fail-closed, and F11 three-cycle replacement pass, but F01/F02 and F12–F17/F19–F24 are not implemented or not run.
3. **S breaks existing dynamic registration.** `IndependentControlReply...` returns `Service ... is not implemented`; `QueuedSelection...` times out. The session snapshot is frozen before the dynamic service is registered.
4. **S causes whole-session collateral.** F09 moves unrelated B to a new physical session.
5. **S control frame is single-shot.** No ack/retry/reconciliation; a lost refresh can leave the old session pinned to a draining R and permanently `Unavailable`.
6. **F18 is partial.** F18 idle and F18b held-duplex pass; Unregister, server-stream `MoveNext`/`Dispose`, producer/CTS tails and codec/facts roots are not tested.
7. **No performance evidence.** A/A, ABBA/BAAB, TCP/SHM comparison and all counters are `NOT_RUN`.
8. **Environment limits.** SharedMemory, TLS PFX and UDS fail on this host; 46 integration / 26 unit / 1 generator / 4 load baseline failures are preserved.
9. **Toolchain deviation.** `global.json` is `10.0.404`, not the requested `10.0.102`.

## 10.6 Specific questions for the independent auditor

Please do not simply accept the author’s summary. Independently verify:

1. **Process**: Does the branch actually follow #998 G0→G7? Are any assertions removed, budgets changed, or failed attempts overwritten? Is the “P” worktree really product code at `b499d2cf` with no S changes?
2. **Cut linearization**: Read `SharpLinkDynamicModule.TryBeginDraining`/`TryAcquire` and the replacement block. Is the cut-before-publish claim true on the final SHA? Is there any path that can acquire an old module lease after the cut? Does the test’s `PinnedSessionCutCommittedForTesting` event fire at the exact linearization point, or only after publish? If only after, what window is untested?
3. **S routing**: Does every request path use `PinnedContractServices`? Check OneWay, admission resume, interceptor/error mapping, stream pre-admission, and any remaining `Volatile.Read(ref _services)` uses. A missed path could retarget to R2.
4. **S retirement**: Is `RetirePinnedSessionsAsync` guaranteed to run for every affected session? What happens if `SessionRefresh` is not negotiated, the send queue is full, the frame is lost, or the client ignores it? Is the synthetic generation safe against ABA/dedup?
5. **Dynamic registration**: Reproduce the two existing-test failures on S. Are they genuine behavioral regressions or test-harness artifacts? If genuine, can S ever satisfy #775’s same-session multi-service expectation?
6. **F18**: Verify the held-duplex F18b events at `evidence/s-core/F18b/` and determine whether they truly hold a real stream, keep the old session/ALC alive, block replacement, and then collect after release. Also check the Unregister, server-stream `MoveNext`/`Dispose`, producer/CTS and codec/facts gaps that remain.
7. **Evidence**: Re-run at least F05/F06/F07/F18/F18b from `4f927b40`, F08 from `33fb48c2`, F04 from `948fb44d`, and F11 from `7d21b89a`; compare event order to the packet. Check that the C-NEG test is on P code, not S, and that its “PASS” is the expected hazard.
8. **Performance**: Is there any hidden or partial performance data? If not, confirm `INCONCLUSIVE`. If yes, test the A/A noise, pairing, sample completeness and cross-arm comparability.
9. **Architecture**: Does S truly avoid a per-request token, or does it merely move identity to the physical session? Does this satisfy #775’s exact-registration/receipt/queue-cut obligations, or is it a different, incompatible design?
10. **Conclusion**: Is `NO-GO for S as an N0 substitute` supported? Is `INCONCLUSIVE` the correct overall experiment result? What is the minimal next experiment that would change the decision?

## 10.7 Decision requested from the auditor

At the end, state:

- whether the execution process is credible;
- whether each arm’s correctness is sufficiently proven;
- whether the performance comparison is valid;
- which conclusions can be accepted and which cannot;
- whether there is evidence to modify #775’s architecture;
- one of: `APPROVE_RESEARCH_CONCLUSION / REQUEST_MORE_EVIDENCE / REJECT_CONCLUSION / INCONCLUSIVE`.

Until that happens, this packet remains **PENDING_INDEPENDENT_AUDIT**.
