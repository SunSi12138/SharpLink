# SharpLink 0.8.40 migration guide

Version 0.8.40 does not change valid Protocol v2 framing, route hashes, request schemas, manifest wire types, or payload layouts. Generated method metadata now carries response nullability for local boundary validation.

If an interceptor invokes `next`, the framework joins an incomplete continuation before the logical call completes even when interceptor code discards its `ValueTask`. Direct forwarding and normal awaiting retain their result and exception behavior. Client interceptors may still short-circuit without calling `next`; response-bearing Server interceptors must still call it, while OneWay interceptors may return directly.

Generated Proxy and Stub signatures now retain nullable reference annotations. Null from a non-nullable `Task<T>`, `ValueTask<T>`, or `IAsyncEnumerable<T>` service result becomes `Internal`; an invalid Client short circuit throws `InvalidCastException`. Explicit nullable response types continue to accept null. Nullable source spelling does not change method IDs or wire-type lookup.

`RpcException` has been removed; use `SharpLinkException` with a defined concrete `SharpLinkErrorCode`. `Unknown` and undefined values are rejected at construction, including from custom exception mappers. Unknown RPC methods now consistently return `Unimplemented`.

`RpcMethodDescriptor.ResponseNullable` is an additive read-only property backed by a final optional constructor parameter. The prior nine-value deconstruction remains available alongside a new ten-value shape.
