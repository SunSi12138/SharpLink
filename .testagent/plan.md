# 0.8.13 regression-test plan

1. [x] `DisposeShouldJoinWriterAfterTheInitialCloseTimeout` covers control-writer convergence after forced stream close.
2. [x] `CancellationTokenShouldWakeAControlWaitWithoutAnExternalPulse` covers token-driven wakeup.
3. [x] `RejectedSecondReadShouldNotBreakTheActiveReadCancellation` covers per-wait reader cancellation ownership.
4. [x] `RejectedSecondReadShouldNotBreakTheActiveReadNotification` covers read-operation ownership and peer notification integrity.
5. [x] `WriterCompletionShouldJoinAnActiveFlushBeforeReturning` covers flush/completion convergence.
6. [x] Complete performance A/B, assertion and pseudo-mutation review, non-incremental build, full tests, package smoke, and documentation.
7. [x] Create the local 0.8.13 audit commit.
