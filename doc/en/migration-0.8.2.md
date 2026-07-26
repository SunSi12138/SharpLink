# SharpLink 0.8.2 Migration Guide

Chinese: [`../migration-0.8.2.md`](../migration-0.8.2.md)

0.8.2 does not change any valid RPC request, response, or generated-contract layout. Peers using SharpLink's writers require no wire migration.

Cancellation now affects only the corresponding waiter on a shared fixed-endpoint connect. Endpoint-cluster handshake failures retain a structured timeout cause. Custom DNS query failures other than transient `SocketException` are no longer hidden by last-good fallback. Hand-written peers must emit shortest-form VarUInt32 lengths and valid UTF-8 error messages; the built-in writers already do so. Upgrade Client and Server together when consistent malformed-wire rejection is required.
