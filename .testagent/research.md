# 0.8.7 regression-test research

## Verified candidates

- `ClientConnection.DisposeAsync` uses an exchange-only idempotence flag. A concurrent caller returns successfully while the winning caller can still be blocked in physical Session/transport cleanup.
- `SharpLinkRuntimeContext.Dispose` and each generated registration reach remaining Adapter scopes but expose only the first disposal failure, discarding the complete plugin cleanup diagnosis.
- `SharpLinkServerHostedService.StopAsync` exchanges away `_runCts` before awaiting shutdown, so concurrent Stop callers can return while listener/server cleanup is still blocked.
- `ServerConnectionState.CloseCoreAsync` continues after connection-cancellation failure but retains only that first exception, discarding a later Session cleanup failure.
- `ClientConnection.Fail` lets a throwing cancellation callback abort pending-call and stream completion, stranding RPC operations and preventing disconnect/reconnect cleanup.

## Audit guardrails

The full performance-pattern and static source-to-test scans were already completed once during 0.8.4. This pass continues targeted ownership and concurrency review without rerunning identical heuristics.
