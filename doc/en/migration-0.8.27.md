# SharpLink 0.8.27 migration guide

Chinese: [`../migration-0.8.27.md`](../migration-0.8.27.md)

0.8.27 does not change Protocol v2 framing or valid payload layouts, but it tightens two invalid response cases. A call declaring `HasResponsePayload` no longer turns an empty response into `default(T)`; its registered Codec decides whether empty input is legal. A void/acknowledgement response that declares no payload reports `DataLoss` when extra bytes arrive. A custom Codec may still define an empty sequence as a valid value.

When `WithCancellation` or an explicit `GetAsyncEnumerator(token)` is used with a server/duplex response stream, the consumer token and original RPC call token now remain independently effective. No code change is required; relying on a consumer token to mask call cancellation was unsupported.

An `AnonymousPipeClientTransportFactory` offer is consumed when its first `ConnectAsync` attempt begins, even if that attempt fails. Request fresh handles from `IAnonymousPipeAllocator` before retrying. A hosted Server that exits successfully while neither the Host nor its Hosted Service is stopping now calls `StopApplication`; use the normal Host/Hosted Service shutdown flow when intentionally stopping that Server.

The concurrent writer-pool disposal fix is transparent and requires no public API or configuration change.
