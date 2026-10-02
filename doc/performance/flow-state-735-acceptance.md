# #735 acceptance review

This page is the reviewer-facing acceptance map for #735. It intentionally
checks observable protocol/lifecycle contracts rather than requiring the new
implementation to reproduce the frozen controller's internal dictionary, lock
or waiter representation.

## Review scope

The production change resolves generation-bound send/receive flow-control state
once and carries the resolved handle through steady-state streaming paths. The
Phase B work demonstrates why removing only the lookup exposes the connection
gate, and validates a single-writer credit/scheduling shape without making that
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
| 1 | same-key old lifecycle handle cannot hit reused pooled state | `ResolvedSendLeaseShouldNotReachReusedSameKeyState`, `ResolvedReceiveLeaseShouldNotReachReusedSameKeyState` |
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

Latest exact-head correctness on `c9fc3858`: build 0 warnings / 0 errors,
171/171 ready-writer checks and full UnitTests 1897/1897.

## Performance acceptance

### Phase A: resolved handle removes lookup

Exact baseline is dev `56c643cd308f294cb03df79d9f1214fb8292affb`.
The latest Resolved Flow State Performance Evidence run succeeds.

NativeAOT microkernel medians:

| Scenario | ns/item improvement |
|---|---:|
| send no-wait | +16.49% |
| send periodic update | +12.84% |
| receive accept | +12.43% |
| receive consume | +18.58% |
| receive accept+consume | +23.84% |
| long-lived aggregate | +13.08% |
| short-stream control | -0.33% |

The microkernel max allocation delta is +0.000 B/item.

Receive-pair c32 attribution:

| Runtime | keyed instructions/item | resolved instructions/item |
|---|---:|---:|
| JIT | 5560.04 | 4652.42 |
| NativeAOT | 611.36 | 513.99 |

Static attribution changes from two stream-state dictionary lookups/item to zero
after one lifecycle resolve, while still entering the same controller gate twice.
The contention controls regress after lookup removal, which is the evidence that
the connection-wide gate becomes the next bottleneck and justifies Phase B rather
than invalidating the resolved-handle result.

### Phase B: stable writer-owned control

The accepted research configuration is quantum 16 with the same 8 KiB prepared
byte cap on A-ready and B3-ready. The stable JIT gate keeps SharedMemory unchanged
and uses an explicit 262144-byte loopback TCP receive buffer on both A/B endpoints
to remove hosted-runner receive-window autotuning noise. It does not change any
flow-control window or product socket default.

Stable JIT, c128 / 16-byte items / 8 KiB cap / q16:

| Transport | PGO | throughput | CPU | A B/item -> B3 B/item |
|---|---:|---:|---:|---:|
| SharedMemory | off | +50.57% | -33.78% | 183.448 -> 25.454 |
| SharedMemory | on | +57.03% | -39.18% | 158.089 -> 25.590 |
| TCP | off | +58.32% | -38.16% | 175.294 -> 23.480 |
| TCP | on | +69.02% | -44.38% | 168.723 -> 28.111 |

The same 8 KiB cap / q16 4 KiB controls are +0.74% to +2.63% throughput in
the stable JIT matrix and also reduce measured B/item substantially.

NativeAOT, same 8 KiB cap:

| Transport | Item | q | throughput | CPU | allocation delta B/item |
|---|---:|---:|---:|---:|---:|
| SharedMemory | 16 | 16 | +77.79% | -44.82% | -159.947 |
| TCP | 16 | 16 | +57.05% | -37.28% | -153.459 |
| SharedMemory | 4096 | 16 | -0.92% | -1.28% | -435.312 |
| TCP | 4096 | 16 | -1.66% | -0.73% | -386.434 |

The large-item native controls retain small throughput negatives rather than
hiding them; CPU and allocation do not regress in q16. The issue's Go condition
is met by large, repeated tiny-item throughput/CPU gains and by the Phase A
microkernel result.

## Diagnostic versus gate

Default loopback TCP runs and separate stalled-owner/stalled-JIT captures remain
in CI artifacts and are never retried to green. Hosted Linux receive-window
autotuning has produced both completion and slow-progress timeout on the same
code. Those jobs are diagnostic observations, not protocol-correctness gates.

Hard gates remain: production correctness, full UnitTests, exact-source guards,
Phase A evidence, stable JIT, NativeAOT, package/code quality, matched transport,
TCP explicit control, and the other repository workflows. On `c9fc3858` every
top-level workflow is successful.

## Review conclusion

#735's specified correctness and Go/No-Go questions are covered. Remaining ideas
such as replacing the experimental writer hook with a different product-wide
scheduler or redesigning generated full-duplex RPC are follow-up integration
choices, not acceptance criteria invented after the issue was filed. Review
should focus on the resolved-handle production changes, their lifecycle
integration, and the evidence that motivates the Phase B ownership direction.
