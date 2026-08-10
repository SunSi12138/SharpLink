# Runtime Architecture Phase 02: complete Session construction

`RpcSession` now has one internal creation model. Every constructor requires an immutable
`RpcSessionCreationOptions` snapshot containing the Client/Server role, the real instance-owned
`SharpLinkRuntimeContext`, optional flush policy, and optional Server exception mapper. The
constructor publishes transport input, Context, role-specific telemetry, negotiated local frame
limit, and one `StreamManager` before returning.

## Removed temporal coupling

The following production paths were deleted, not deprecated or forwarded:

- `SharpLinkRuntimeContext.Default` and the field initializers that referenced it;
- `RpcSession.BindRuntimeContext` and `RpcSession.SetTelemetrySide`;
- the mutable `RpcSession.ServiceExceptionMapper` patch point;
- nullable Runtime Context construction in `SharpLinkClient` and `SharpLinkServer`;
- the nullable/default codec-provider path in `PooledAsyncStreamDispatcher`.

Client fixed, static-cluster, and dynamic-cluster connection paths now create Client-role Sessions
with their owning Client Context. The Server allocates the per-connection cancellation map first,
then gives the same map to both the immutable mapper delegate and `ServerConnectionState`; this
breaks the previous Session/connection-state construction cycle without a holder, late setter, or
global lookup.

## Ownership and state boundaries

| State/resource | Creator and owner | Terminal behavior |
|---|---|---|
| Runtime Context | Client/Server builder; owned by the resulting Client/Server | Disposed once by Client/Server stop; Session only borrows it |
| transport | Client connector or Server listener; transferred to Session after successful construction | Session terminal arbitration disposes it once |
| StreamManager | Session constructor | Reference never changes; Session terminal completion drains it |
| Server call-cancellation map | Server accepted-connection path; owned by `ServerConnectionState` | Deadline scheduler and connection close converge on the same map |
| exception mapper delegate | Server accepted-connection path; borrowed by Session | Immutable; invoked only for that connection and never disposed |

Transport/protocol state remains in `RpcSession`; new-call admission and pending ownership remain
in `ClientConnection`; authentication, call admission, and draining remain in
`ServerConnectionState`. A handshaking Server connection cannot accept business calls or publish a
business request ID.

## Tests and compatibility

`RpcSessionLifecycleTests` verifies missing/invalid creation dependencies, role and Context
publication, Context isolation, constructor-supplied mapper behavior, stable StreamManager
references through concurrent terminal cleanup, and deterministic disposal of both isolated
Contexts. `ServerConnectionStateTests` verifies that business admission remains closed before
handshake. Existing seeded 100-round Session terminal races continue to prove exactly-once
transport disposal.

This is an intentional CLR/source construction break. No legacy constructor, obsolete shim,
reflection adapter, or process-wide Context alias remains. Protocol v2 framing, Generated API 4,
contract/method/member IDs, schema rules, and wire negotiation are unchanged.

The production changes are control-plane construction work. They do not add a per-RPC, per-frame,
or per-stream-item abstraction, allocation, or lock. The dispatcher provider cleanup affects only
an overload not used by production call sites; production continues to rent with an already
resolved codec. Phase 08 retains scheduler/monotonic-time work, and Phases 03-06 retain transport
constructor removal, exception-mapper placement, and handshake negotiation snapshots.
