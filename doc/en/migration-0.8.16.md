# SharpLink 0.8.16 Migration Guide

Chinese: [`../migration-0.8.16.md`](../migration-0.8.16.md)

0.8.16 does not change Protocol v2 or generated Manifests. `MaxPendingRequestsPerConnection` must still be a positive power of two and now has a 1,048,576 maximum; larger concurrency budgets must be distributed across more physical connections. `SharpLinkBufferWriterPool` now implements `IDisposable`; a pool owned by a disposed Runtime Context is closed and subsequent `Rent` calls throw `ObjectDisposedException`.

Server `StopAsync`, `DisposeAsync`, and the shared `RunAsync` now surface immediate listener/framework/service cleanup failures: one failure retains its original exception, while multiple failures use `AggregateException`. Cleanup that exceeds the final five-second budget remains observed asynchronously and is represented by Unhealthy/Faulted state rather than making Stop unbounded. The Generic Host `StartAsync` token controls startup only; after successful publication, use the Host stop lifecycle to shut down the server.
