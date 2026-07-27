# SharpLink 0.8.3 Migration Guide

Chinese: [`../migration-0.8.3.md`](../migration-0.8.3.md)

0.8.3 changes neither wire layout nor the public `SharpLinkMetadata` constructor. Endpoint snapshots now own frozen endpoint attributes. `StopAsync` returns its awaitable operation without synchronously waiting for cancellation callbacks. When startup/connect and cleanup both fail, an `AggregateException` preserves the primary failure first. Metadata array ownership is Runtime-internal. Client and Server can be rolled independently, though coordinated deployment gives consistent diagnostics.
