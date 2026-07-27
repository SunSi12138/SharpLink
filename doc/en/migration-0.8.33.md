# SharpLink 0.8.33 migration guide

Chinese: [`../migration-0.8.33.md`](../migration-0.8.33.md)

0.8.33 changes no public API, Protocol v2 framing, route hash, or valid payload. It tightens a contract shape that no single generated class can implement and fixes failure rollback plus Generic Host ownership.

Inherited RPC methods with identical parameter signatures and incompatible return types now report `SHARPLINK057`; no Proxy/Stub is emitted for that conflicting contract. Rename a route, unify the return type, or split the contract. Internal generated Stub size-field names change, while wire types, Manifest identities, route hashes, and payloads do not.

Synchronous Client/Server `Build()` failure still completes asynchronous resource cleanup and aggregates cleanup failures before returning, but no longer depends on pumping the caller's `SynchronizationContext`. Client and Multi-Cluster Hosted Services are single-start owners: a second `StartAsync` throws `InvalidOperationException` without disposing the existing instance or failing the accessor.
