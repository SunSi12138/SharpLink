# SharpLink 0.8.7 Migration Guide

Chinese: [`../migration-0.8.7.md`](../migration-0.8.7.md)

0.8.7 changes no public API, Protocol v2, or generated Manifest. Concurrent Dispose/Stop callers now await the same owned cleanup; multiple Adapter or connection-close failures may surface as `AggregateException`. Cancellation callback failure is logged and no longer blocks RPC termination. No configuration migration is required.
