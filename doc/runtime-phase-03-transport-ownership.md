# Runtime Architecture Phase 03: single transport ownership

`RpcSession` now accepts only an `ITransportConnection` and an immutable creation snapshot. The
Session stores one non-null transport field; its input, output, endpoints, and physical cleanup all
come from that owner. Client and Server own the Session, and the Session owns the transport.

## Removed lifecycle branch

The PipeReader/PipeWriter/disconnect/isConnected constructor and its fields were deleted rather
than deprecated or forwarded. `IsConnected` now reports only whether the Session has published a
terminal state. EOF and native failures arrive through the transport pipelines and converge on the
same terminal winner; no second physical-connection boolean can disagree with protocol state.

Session cleanup no longer completes transport pipelines itself. Each `ITransportConnection`
implementation owns its pipelines and native handles and must release all of them from
`DisposeAsync`. This gives custom and built-in transports the same stable SPI and prevents Session
and transport from racing to reclaim the same reader, writer, stream, socket, mapping, or pipe.

## Terminal and cleanup ownership

| Resource/state | Creator and owner | Terminal behavior |
|---|---|---|
| transport candidate | Client factory or Server listener | Ownership transfers only after Session construction succeeds |
| `RpcSession` | Client/Server connection path | Owns the accepted transport and protocol terminal state |
| Input/Output pipelines | transport | Transport disposal completes/faults them and releases native resources |
| transport dispose task | Session | Created once under the dispose gate; synchronous throws become one faulted task |
| Fault observer | Session | Observes an early asynchronous dispose failure; later explicit disposal still awaits the same task and preserves the exception |

`RpcSessionLifecycleTests` races Fault, BeginShutdown, and two explicit DisposeAsync callers against
a blocked failing transport. Disposal starts once, both callers receive the same exception object,
and the Session remains terminal. Existing 100-round terminal races, transport cleanup tests,
SendPump/StreamManager tests, transport integration, chaos, and NativeAOT retain broader coverage.

The change removes one connection callback and one branch from `IsConnected`; it adds no per-RPC,
per-frame, or per-item allocation or lock. Protocol v2, Generated API 4, identifiers, schema rules,
and wire negotiation remain unchanged.
