# 0.8.9 regression-test research

## Verified candidates

- `SharedMemoryControlChannel.DisposeAsync` skips reader-task convergence when stream disposal throws unexpectedly.
- `SharpLinkClientHostedService.StopAsync` exchanges away its client before awaiting stop, so a concurrent caller returns early.
- `SharpLinkMultiClusterClientHostedService.StopAsync` has the same independent early-return defect.
- Asynchronous server transport listeners use exchange-only idempotence and let concurrent disposal callers return before pending resources drain.
- `AnonymousPipeServerTransportListener.DisposeAsync` stops at the first queued connection disposal failure and skips later queued connections and token cleanup.

## Audit guardrails

The full performance-pattern and static source-to-test scans were already completed once during 0.8.4. This pass continues targeted ownership and concurrency review without rerunning identical heuristics.
