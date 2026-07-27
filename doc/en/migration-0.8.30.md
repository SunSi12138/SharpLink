# SharpLink 0.8.30 migration guide

Chinese: [`../migration-0.8.30.md`](../migration-0.8.30.md)

0.8.30 does not change Protocol v2, valid payloads, or the normal Generic Host start/stop order.

- A stopped `SharpLinkServerHostedService` instance cannot restart, and an already-started instance rejects another Start. Create a new Host/DI scope for manual restart scenarios.
- A Run fault caused during explicit Stop remains the Stop result but no longer requests application shutdown.
- `Task<T>` RPC response types may safely contain `ValueTask` in `T`'s name; rebuild to regenerate corrected Proxy/Stub code.
- `SharpLinkNamedPipeAddress.PipeName` and `SharpLinkSharedMemoryAddress.Name` reject NUL, `/`, and `\\`, matching the concrete transports. Replace path-like values with logical identifiers.
- Local server health status and descriptions are unchanged; their completed Tasks are reused.
