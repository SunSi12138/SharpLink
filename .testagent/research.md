# 0.8.1 regression-test research

## Scope

- Authentication and topology immutability boundaries.
- Generated manifest and built-in request Codec integrity.
- Built-in endpoint-resolver cancellation lifecycle.
- Native `List<T>` decode allocation/copy cost.

## Confirmed candidates

- `SharpLinkAuthenticationContext.Scopes` exposes mutable `HashSet` instances, including one process-wide shared empty set.
- `SharpLinkEndpointSnapshot.Endpoints` and generated manifest collections expose arrays behind read-only interfaces.
- Both built-in endpoint resolvers cancel but never dispose their owned `CancellationTokenSource`; synchronous cancellation also violates their async disposal surface.
- Generated inline requests bypass validating built-in Codecs for Boolean and semantic value types.
- `BlitListCodec<T>` materializes an intermediate array and then copies it into a second List-owned array. The frozen 0.8.0 RPC baseline is 560 B/op for 16 integers and 2480 B/op for 256 integers.

## Acceptance checklist

- [x] Authorization scopes cannot be mutated or shared-contaminated by callers.
- [x] Endpoint and generated manifest collections cannot be cast back to writable arrays/lists.
- [x] Resolver disposal is idempotent, asynchronous, and disposes the owned cancellation source.
- [x] Semantic fixed request parameters use validating built-in Codecs; raw numeric hot-path parameters remain inline.
- [x] List decoding writes directly into the List-owned storage and reduces allocations without throughput regression.
- [ ] Release, Generator, Unit, Integration, focused benchmarks, and pseudo-mutation review pass.
