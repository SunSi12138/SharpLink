# 0.8.13 regression-test research

## Target inventory and verified candidates

- `SharedMemoryControlChannel.DisposeCoreAsync`: the initial 250 ms writer grace period was followed by stream disposal and reader convergence, but the writer task was never joined afterward.
- `SharedMemoryControlChannel.WaitAsync`: cancellation was inspected only before and after a pulse; cancellation itself could not wake a blocked wait.
- `SharedMemoryPipeReader.ReadAsync`: a shared cancellation-registration field was replaced before a concurrent-read rejection, disconnecting the active read from its token.
- `SharedMemoryPipeReader.ReadAsync`: that rejected read also entered the shared wait `finally` and cleared the accepted read's `ReaderWaiting` flag, allowing peer data publication to omit the required notification.
- `SharedMemoryPipeWriter.CompleteAsync`: spill disposal raced an active flush, allowing completion to return while the flush still touched the spill buffer.
- Existing TUnit conventions support controlled pipe streams, real named-pipe pairs, direct lifecycle assertions, and `[NotInParallel]` for the process-global mapping directory.

## Acceptance checklist

- Control disposal does not return before its writer loop exits after the underlying stream is closed.
- A cancellation token wakes a control wait without any external transport pulse.
- Rejecting a second read cannot detach cancellation from the active read.
- Rejecting a second read cannot clear notification state or strand the active read after peer data arrives.
- Writer completion never returns before an active flush has converged.
- No test relies on arbitrary skipping or broad exception acceptance; the two timing tests expose state before releasing their controlled blocker.

## Audit guardrails

This pass reviews shared-memory control-loop convergence and pipeline cancellation/completion. The previously completed project-wide scans are not re-labelled as new findings. A proposed mapping-age guard was withdrawn after review showed that live SharpLink mappings retain a conflicting file handle; its test-only stale-file race is serialized but is not counted as a product finding.
