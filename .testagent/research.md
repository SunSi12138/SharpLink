# 0.8.6 regression-test research

## Verified candidates

- `StreamTransportConnection.DisposeCoreAsync` awaits writer then reader then stream without cleanup isolation. An unexpected writer/reader completion failure can skip every later resource.
- `RpcSession.DisposeAsync` similarly skips remaining pipeline and transport cleanup after an unexpected pump or pipe completion failure. Its completion signal is always successful, so concurrent disposal callers may disagree about whether teardown failed.
- `ServerConnectionState.CleanupServicesWhenCallsDrainAsync` catches and discards every connection-service cleanup exception, making its supervised `ServiceCleanupTask` report success even when user resources failed to release.
- `ServerServiceCleanup.DisposeAsync` reaches every singleton registration and the owned provider but exposes only the first failure, discarding the rest of the server-wide cleanup diagnosis.
- `SharpLinkServerHostedService.StartAsync` returns after the run loop becomes asynchronous but never supervises its retained task. A later listener/run failure leaves the Generic Host running with a dead RPC server and no stop notification.

## Audit guardrails

The full performance-pattern and static source-to-test scans were already completed once during 0.8.4. This pass continues targeted ownership and concurrency review without rerunning identical heuristics.
