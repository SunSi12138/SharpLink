# #735 acceptance review

This page is the reviewer-facing acceptance map for #735. It checks observable
protocol/lifecycle contracts rather than requiring the new implementation to
reproduce the frozen controller's internal dictionary, lock, or waiter layout.

## Evidence identity

The latest production-runtime code under test is
`e62cb674c6b640d29c4a612808df2c6423fc1b1d`.

Later review-head commits may change tests, workflow gating, or this document
without changing runtime behavior. Performance numbers below therefore identify
their exact workflow run/artifact instead of calling the documentation commit an
"exact-head" runtime measurement.

Current retained evidence for runtime SHA `e62cb674`:

- correctness: Ready Writer run `36965020947`, build 0 warnings/errors,
  **171/171** ready-writer checks and full UnitTests **1898/1898**;
- stable JIT: artifact `11209965696`,
  SHA256 `e887d67a8261e84f27603755065a2b704fa7fb530cd1c76c99cd8c625f7b46da`;
- NativeAOT: artifact `11209129203`,
  SHA256 `6b021b5023966ac267e8d124e0e616618fd0bc094a1ea30322ef781528b918a0`;
- resolved-state evidence: run `36965020943`, artifact `11209877876`,
  SHA256 `ae174ec4792cda308d9e0d0b3f84813fd45d21a6603b430c24c8cbab66d6c5cc`.

The stable JIT/NativeAOT disposable measured tree is
`d474c43ee68b94edd78060d96eb033dacbab6b01`.

## Review scope

The production change resolves generation-bound send/receive flow-control state
once and carries the resolved handle through steady-state streaming paths. Phase
B evidence demonstrates why removing only lookup exposes the connection gate and
validates a single-writer credit/scheduling direction without making that
research hook a product default.

Hard requirements retained from #735:

- no second stream-state map;
- Protocol v2 wire format and credit meaning unchanged;
- stream and connection hard windows unchanged;
- connection-credit FIFO/fairness behavior preserved; a stream-local blocked
  waiter may not strand an otherwise eligible stream;
- oversized-item borrow/repay preserved;
- completed tombstones, exactly-once unsent return, terminal handling and
  generation/lease ABA protection preserved;
- no default flow-control-window change and no stream-item batching.

The ready-writer TCP receive-buffer control is benchmark-only under
`SHARPLINK_READY_WRITER_EXPERIMENT`; product socket defaults are unchanged.

## Correctness checklist from the issue

| # | #735 requirement | Direct coverage |
|---|---|---|
| 1 | same-key old lifecycle handle cannot hit reused pooled state | `ResolvedSendLeaseShouldNotReachReusedSameKeyState`, `ResolvedReceiveLeaseShouldNotReachReusedSameKeyState`, `SameKeyRegistrationCannotCaptureReceiveLeaseBeforeOldRoutePublishesTerminal` |
| 2 | `CompleteSendStream` + late `WindowUpdate` | `LateWindowUpdateForRemovedStreamShouldBeDiscarded`, `FailedSendStreamShouldAcceptInFlightCreditBeforeReusingCapacity`, completed-send tombstone tests |
| 3 | `ReturnUnsentCredit` exactly once | `ResolvedUnsentCreditShouldBeReturnedExactlyOnce`, `UnsentFrameShouldReturnCreditAndAdmitTheNextWaiter`, writer queue-rejection boundary test |
| 4 | `AbortSendStreams` | `AbortSendStreamsShouldPoisonResolvedLeaseWithoutDoubleReturn` |
| 5 | stream / connection exhaustion | `ResolvedReceiveLeaseShouldPreserveStreamAndConnectionExhaustion`, one-byte window and oversized tests |
| 6 | receive stream-threshold batching | `ResolvedReceiveLeaseShouldPreserveStreamThresholdBatching` |
| 7 | connection-threshold cross-stream flush | `ResolvedReceiveThresholdsShouldPreserveCrossStreamFlush`, multi-key receive flush tests, real receiver wire-boundary test |
| 8 | completed receive tombstone | `CompletedReceiveTombstoneShouldKeepResolvedLeaseUntilFinalCredit` |
| 9 | state-pool reuse 100k | `ResolvedReceiveStatePoolShouldSurviveOneHundredThousandReuses`; ready-writer lifecycle suite also exercises same-state generation reuse |
| 10 | disconnect / terminal with retained handle | `TerminalShouldInvalidateResolvedHandlesWithoutCreditingAReplacement`, `TerminalShouldRejectFirstAdmissionWaiterWithOriginalException` |
| 11 | waiter cancellation | `ResolvedSendWaiterCancellationShouldPreserveCurrentLease`, `FirstAdmissionShouldPreserveFifoAndCancellation` |
| 12 | max concurrent stream pressure | `ResolvedReceiveLeaseShouldRespectConcurrentStreamLimit`, retained-send tombstone capacity tests |
| 13 | multiple handles cannot double debit / return | `MultipleResolvedSendHandlesShouldNotDoubleReturnCredit`, `MultipleResolvedReceiveHandlesShouldNotDoubleReturnCredit` |

The production receive route now keeps a retiring route key mapped until the
entry's own generation-bound `FlushConsumed(in lease)` terminal publication
completes. Same-key registration checks the registry key before resolving flow
state, so it cannot capture an old generation and publish it as a new route.
The deterministic regression pauses old terminal publication without sleeps,
proves the overlapping registration does not even call the resolver, then proves
the replacement resolves a different generation and accepts its first frame.

Additional contract controls:

- `ConnectionCreditShouldBeSharedInFifoOrder` and
  `FirstAdmissionShouldPreserveFifoAndCancellation` protect connection-credit
  ordering.
- `StreamCreditBlockedHeadShouldNotBlockAnEligibleStream` and
  `FirstAdmissionShouldNotLetBlockedStreamHeadStallEligibleStream` preserve
  stream-local fairness.
- mixed-size ready-writer checks validate 4/8/20-byte per-frame credit, connection
  head blocking, stream-local skip, exact conservation and oversized borrow/repay.
- Phase B writer-boundary tests preserve queue-rejection refund, early-credit
  writer pinning, cancellation ownership and visible-byte failure semantics.
- Phase B wire-boundary tests cover actual receiver credit, segmentation,
  duplicate/excess credit characterization and key-only generation limits.
- blocked-Flush stream-abort tests preserve writer-owned DATA while immediately
  cleaning only unadmitted prepared buffers.

## Performance acceptance

### Phase A — resolved handle removes lookup

Exact baseline is dev `56c643cd308f294cb03df79d9f1214fb8292affb`.
Resolved Flow State Performance Evidence run `36965020943` succeeds.

NativeAOT microkernel medians:

| Scenario | ns/item improvement |
|---|---:|
| send no-wait | +16.79% |
| send periodic WindowUpdate | +11.80% |
| send starved control | +8.53% |
| receive accept | +12.21% |
| receive consume | +19.44% |
| receive accept+consume | +23.45% |
| long-lived aggregate | +13.45% |
| short-stream control | -0.14% |

Maximum measured micro allocation delta is **+0.000 B/item**.

Dynamic-PGO ON medians include send no-wait **+24.70%**, receive accept
**+30.85%**, receive pair **+27.73%**, and long-lived aggregate **+23.85%**.
PGO OFF send no-wait is **+22.11%**, receive pair **+28.83%**, and long-lived
aggregate **+20.04%**.

Receive-pair c32 attribution:

| Runtime | keyed instructions/item | resolved instructions/item |
|---|---:|---:|
| JIT | 5559.86 | 4738.87 |
| NativeAOT | 611.36 | 513.70 |

Static attribution changes from two stream-state dictionary lookups/item to zero
after one lifecycle resolve, while still entering the same controller gate twice.
Contention controls regress after lookup removal, which is the evidence that the
connection-wide gate becomes the next bottleneck and motivates Phase B rather
than invalidating Phase A.

The production end-to-end 100-item median is deliberately reported separately:
TCP **+0.57%** throughput and SharedMemory **+2.12%**. Therefore the much larger
Phase B tiny-item gains are not attributed to lookup removal alone.

### Phase B — stable writer-owned research control

Accepted comparison is quantum 16 with the same 8 KiB prepared-byte cap on
A-ready and B3-ready. The stable JIT gate keeps SharedMemory unchanged and uses
an explicit 262144-byte loopback TCP receive buffer on both A/B endpoints to
remove hosted-runner receive-window autotuning noise. It does not change any
flow-control window or product socket default.

Stable JIT, c128 / 16-byte items / 8 KiB cap / q16:

| Transport | PGO | throughput | CPU | A B/item -> B3 B/item |
|---|---:|---:|---:|---:|
| SharedMemory | off | +64.56% | -39.34% | 185.281 -> 17.600 |
| SharedMemory | on | +69.10% | -41.85% | 174.425 -> 17.904 |
| TCP | off | +55.78% | -35.81% | 177.118 -> 19.493 |
| TCP | on | +61.67% | -37.17% | 181.642 -> 17.721 |

Matching 4 KiB / 8 KiB cap / q16 controls remain visible:

| Transport | PGO | throughput | CPU |
|---|---:|---:|---:|
| SharedMemory | off | +1.40% | -5.73% |
| SharedMemory | on | -5.09% | +3.70% |
| TCP | off | +1.55% | +0.36% |
| TCP | on | -1.02% | +0.38% |

NativeAOT, same 8 KiB cap:

| Transport | Item | q | throughput | CPU | allocation delta B/item |
|---|---:|---:|---:|---:|---:|
| SharedMemory | 16 | 16 | +72.01% | -38.90% | -159.278 |
| TCP | 16 | 16 | +61.23% | -35.96% | -154.371 |
| SharedMemory | 4096 | 16 | -1.48% | +2.42% | -431.387 |
| TCP | 4096 | 16 | +1.08% | +1.32% | -388.704 |

Large-item negative controls are retained rather than hidden. The issue's Go
condition is met by repeated tiny-item throughput/CPU improvements and by the
Phase A microkernel result.

## Diagnostic versus gate

Default loopback TCP runs and separate stalled-owner/stalled-JIT captures remain
in CI artifacts and are never retried to green. Hosted Linux receive-window
autotuning has produced both completion and slow-progress timeout on the same
code. Those jobs are diagnostic observations, not protocol-correctness gates.

The older B1/B2 `Phase B Matched Transport Control` workflow is also retained as
research evidence. Its own upload step already states that it is **not a
production Go gate**; a failed negative/default TCP cell remains in its artifact
but does not override the stable B3 JIT/NativeAOT gates.

Hard gates are production correctness, full UnitTests, exact-source guards,
Phase A evidence, stable JIT, NativeAOT, allocation/package/code-quality gates,
the explicit TCP control, and the ordinary repository validation workflows.

## Review conclusion

#735's specified correctness and Go/No-Go questions are covered. Remaining ideas
such as replacing the experimental writer hook with a different product-wide
scheduler or redesigning generated full-duplex RPC are follow-up integration
choices, not acceptance criteria invented after the issue was filed. Review
should focus on the resolved-handle production changes, their lifecycle
integration, and the evidence that motivates the Phase B ownership direction.
