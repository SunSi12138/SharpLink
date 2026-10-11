# RPC channel / client stream sink split in 3.0

In 3.0, `IRpcChannel` no longer inherits `IRpcClientStreamSink`.
`SharpLinkClient.SendClientStreamAsync<T>` always failed, and metadata/dynamic
channel forwarding methods delegated only to that failure; all have been removed.

**Generated client streaming is preserved.** Generated proxy and stream writers
use the real connection-bound `IRpcClientStreamSink`, not the channel facade.
Application callers should use generated `[RpcContract]` proxy streaming methods;
they must not cast an `IRpcChannel` to an `IRpcClientStreamSink`.

This is a CLR/Generated ABI migration for 3.0; regenerate contracts with the same
3.0 SDK and deploy matching Runtime, Abstractions, Client and Server packages.
It does **not** change Protocol v2 wire frames or the client stream semantics.
