# Runtime Architecture Phase 04: Server-owned exception mapping

Business exception policy now ends at the Server invocation boundary. `RpcSession` owns protocol
state and can encode only an already structured `SharpLinkException`; it does not store an
`IRpcExceptionMapper`, service registration, contract ID, method policy, or late-bound callback.

## Invocation boundary

Each accepted Server connection creates one `ServerGeneratedBridge`. Generated API 4 stubs receive
that stable narrow capability. Inbound-stream registration delegates directly to Runtime. An
outbound service stream is pumped by Runtime, while its raw enumeration or codec failure propagates
back to the Server bridge. The bridge applies cancellation reason and `IRpcExceptionMapper` policy,
then submits one structured `StreamComplete(Error)` terminal to Runtime.

Unary and client-stream service failures already return to the Server dispatch layer and use the
same mapper before a structured `Response(Error)` is encoded. Server-stream and duplex failures now
join that boundary without a Session callback or compatibility adapter. Admission,
authentication/authorization, protocol, compression, cancellation, and module-drain paths construct
their explicit `SharpLinkException` before calling the same protocol send operations.

## Ownership

| State or resource | Creator and owner | Terminal behavior |
|---|---|---|
| `IRpcExceptionMapper` | Server builder / Server | Immutable for the Server lifetime; never transferred to Runtime |
| generated invocation bridge | accepted-connection path / `ServerConnectionState` | Reused by calls on that connection; owns no transport or disposable resource |
| generated protocol bridge | Runtime adapter over Session | Borrows Session; pumps typed items and propagates raw failures without mapping |
| structured protocol error | Server invocation boundary | Runtime bounds and encodes code/message, then completes the corresponding request or stream |
| mapper failure | Server invocation boundary | Logged and replaced with safe `Internal`; the connection remains usable |

No new object, delegate, lock, or lookup is added per Unary call or per stream item. The Server
bridge is allocated once per physical connection. Generated API 4 and Protocol v2 frame layout,
IDs, schema rules, cancellation codes, and negotiation remain unchanged.

## Verification

The mapping matrix covers Unary, client streaming, server streaming, duplex streaming,
interceptor-visible terminal state, mapper throws, structured interceptor errors, cancellation and
server-stop reasons, and default sensitive-detail hiding. Runtime bridge tests prove that raw
business/codec failures propagate to the Server owner and that only a caller-supplied structured
error is encoded as a stream terminal.

The bounded bare-metal `RuntimePhase00Benchmarks.UnarySendAndComplete` comparison used the same
.NET 10.0.10 process, 8 warmups, 20 measurement iterations, and 2 launches for both revisions. The
pre-change commit measured 15.76 us mean / 15.23 us P50 / 17.42 us P99 / 952 B, while this change
measured 14.88 us / 14.85 us / 15.53 us / 952 B. The result establishes unchanged per-call managed
allocation and no measured Unary throughput regression; it is not a claim that the refactor itself
caused the observed timing improvement.
