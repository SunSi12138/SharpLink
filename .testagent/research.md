# 0.8.5 regression-test research

## Verified candidates

- `SharpLinkClientAccessor.SetClient` checks `_stopped` separately from publishing `_client`. A concurrent `Stop`/`Fail` can clear the slot and then lose to the later compare-exchange, resurrecting a client after terminal shutdown. `GetClientAsync` also reads the client before checking terminal state.
- Per-call and per-connection service activation rollback awaits scope disposal from a bare `catch`; a scope cleanup failure replaces the activation failure and removes its root cause.
- `ServiceLease` and `ConnectionServiceInstance` deliberately suppress scope disposal failure when service disposal already failed, losing terminal cleanup evidence.
- Fixed-client initial pool rollback disposes already-connected sessions from a bare `catch`. A disposal failure replaces the later connection failure, aborts remaining rollback, and skips the transition from `Connecting` to `Faulted`.
- `InvokeServiceWithLeaseAsync` suppresses the entire lease cleanup failure whenever the RPC handler has already failed. Exception mappers and server diagnostics therefore cannot observe both terminal causes.

## Audit guardrails

The full performance-pattern and static source-to-test scans were already completed once for this repository state during 0.8.4. The 0.8.5 pass uses their recorded results as navigation evidence and does not inflate confidence by rerunning the same heuristic scans.
