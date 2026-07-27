# SharpLink 0.8.41 migration guide

Version 0.8.41 does not change valid Protocol v2 framing, method IDs, wire types, or payload layouts. It carries the response nullability already generated in 0.8.40 through real network decoding and runtime compatibility identity.

## Decoded nullability

A non-nullable `T` in a unary/client-streaming response, ServerStreaming/DuplexStreaming response item, or ClientStreaming/DuplexStreaming request item no longer accepts null returned by a Codec. Violations fail with `SharpLinkException(DataLoss)`. Contracts that need to transport null must declare `T?` or `IAsyncEnumerable<T?>` explicitly; those declarations continue to round-trip null.

`PooledAsyncStreamDispatcher<T>` retains its original two-argument `Rent` methods, preserving binary compatibility for existing compiled callers. New three-argument overloads let generated/runtime callers provide payload nullability.

## Contract identity

Nullable responses now participate in runtime method/service/contract fingerprints. Separately generated contracts that differ only in response nullability are therefore correctly incompatible. Method IDs and established required-response fingerprints are unchanged; endpoint routing and payload Codecs need no changes.

## Error code

`SharpLinkErrorCode.Unknown` is a reserved unset state, not a valid Protocol v2 Error code. Custom protocol callers must not write or send it; the reader now classifies it as `ProtocolViolation`, like an undefined enum value. Numeric values and round trips for every concrete defined code are unchanged.
