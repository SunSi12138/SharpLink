# Ready-writer send-queue admission boundary

This increment continues #735 and the existing Draft #742 from
`be54089d1dc5a0e97006879ef293b4640f8ec629` (tree
`9c233e945bbb85481af239c6b879c47dd8346b6f`). It changes the opt-in research
integration, not shipping runtime files, flow-control windows or protocol encoding.

## A demonstrated integration error, separate from the old JIT stall

The source previously removed a ready frame and, for B3, debited its flow credit
before SendPump attempted to reserve send-queue bytes. Any failed reservation
then rejected the frame and stopped the connection. This incorrectly treated
transient occupancy of an unfinished batch as a permanently invalid frame.

Four preloaded Pipe controls first compiled with zero warnings/errors and failed
on that original implementation with `Experimental ready frame exceeded normal
queue capacity`. They cover both A-ready and B3-ready:

- 32 KiB send queue, 4 KiB DATA items, two streams with eight items each;
- 8 KiB send queue, 16 KiB DATA items, two streams with two items each.

All frames are prepared before the source is attached. The flush threshold is
128 KiB, intentionally larger than queue capacity. Reproduction does not depend
on a sleep or on producers racing the writer. The second case preserves the
existing small-queue oversized-frame exception: one frame may borrow an empty
queue, but a second frame must wait for its release.

This is **not** a demonstrated cause of the old hosted JIT timeout. The old
performance configuration did not use these queue limits. Its failed artifact
and the independent diagnostic investigation remain valid, separate evidence.

## Ownership and budget ordering

SendPump now implements an internal, preallocated `IReadyFrameAdmission` view.
The source asks it to reserve the exact serialized frame length while the
selected stream still owns that frame. A failed *transient* reservation returns
false without dequeuing, B3 debit, refund, re-enqueue or head bypass. A-ready
retains its already admitted producer-side credit until eventual send or stop.

The pump leaves the inner drain and flushes its current batch to release bytes.
While no capacity is available, the ready source does not cause an idle busy
loop. The budget-wait flag is armed before a second reservation check; a racing
release therefore either wakes the armed wait or makes the second check succeed.
Release tests the flag before performing an exchange, so ordinary release does
not acquire an additional atomic RMW. The existing send-budget CAS and release
RMW still occur per frame, and performance reports retain that lower bound.

The same original `TryReserve` implementation remains the authority. Frames
that can *never* fit the normal allowance fail promptly rather than waiting
forever. A 30 KiB payload with a 32 KiB queue is below total queue capacity but
above its normal allowance; tests require rejection without consuming the
protocol-progress reserve or transferring a frame to the writer.

This is queue backpressure, not a larger credit window, a larger memory budget,
or a replacement of per-item locks with a new per-item asynchronous completion.
It does add an internal admission call; fresh performance evidence is required.
The single-stream preparation cap remains a separate bound and still excludes
the producer's held frame, the in-flight batch and transport memory.

## Regression coverage and fixture boundaries

The eight additional checks include the four actual-pump controls, two denied-
admission ownership/order checks and two permanently oversized rejection checks.
They validate full serialized length, untouched ready heads on denial, exactly
one successful admission, release of every transmitted frame, returned credit,
zero remaining prepared bytes and preservation of the progress allowance.

The queue fixture validates actual Pipe DATA bytes and sends balanced return
commands to the owner. It releases its input ReadResult *before* awaiting the
bounded return mailbox: holding the read while waiting for a writer that is
itself awaiting Flush would introduce a fixture-local cycle. An intermediate
fixture exposed that cycle; the final fixture was rerun against the original
pump policy and still failed all four original queue-pressure cases. This is
not represented as full negotiated receiver, TCP or SharedMemory coverage.

All previous self-checks remain. The full writer check count is now 78 in the
ordinary build (90 with all currently inherited diagnostic captures). Both default and
disposable experimental UnitTests passed 1897/1897 with successful process exits.
The minimal reflection-disabled managed host also compiled with zero warnings/
errors and passed the writer checks; it is not called a native execution.

Local restore uses cached packages and disables NuGetAudit. That is not an
online package audit. New-head hosted CI still must publish and execute the
native host, run the complete JIT/native transport populations, and preserve
failed attempts. No performance thresholds, source guards or tests are removed.

## Evidence and acceptance boundary

Before this change, the exact `be54089d` Ready Writer run `36013600956` completed
both the 48-process/768-row JIT matrix and the 8-process/128-row native matrix.
Those archives were downloaded and their source/configuration/credit/budget
checks independently rerun. That later success does not establish a root-cause
fix for the earlier `727fb20d` stall, and none of its timings belongs to this
queue-admission change.

The concurrent diagnostic implementation is preserved unchanged. No diagnostic
timer or reflection is added to measured builds by this queue fix.

Dynamic stream retirement/reuse, global FIFO compatibility, duplicate-credit
policy, cancellation/deadline service during blocked transport and full default
RPC integration remain open. This regression fix does not promote B3 to the
shipping path or establish Ready for Review. Keep #735 open and #742 Draft.

## Final rebase and validation record

Publication preserves the subsequent collector, retained-audit, TCP-window and
standalone-owner diagnostic commits through
`13237a20b346bb1cf5f26d5bf5ffaad8334029b4`. No diagnostic file is overwritten.
The normal C# budget/admission code is identical to the checked increment.
The additional inherited cold/settled captures increase diagnostic checks from
80 to 90 without changing the 78 ordinary checks.

Before that final rebase, combined `9628bbc6` validation compiled default and
diagnostic Benchmark and experimental UnitTests with zero warnings/errors.
82 diagnostic checks passed, as did 69 Python guards. However, that particular
complete unit run finished 1896/1897: `RetryDelayBeyondDeadlineShouldNotOverflow`
timed out at its unchanged 30-second test limit. This result is retained, not
replaced by the earlier 1897/1897 runs. The retry/deadline source is not changed
by the queue fix, and a causal explanation is not claimed. New-head CI must
still verify the combined tree; no failing assertion or timeout is relaxed.
