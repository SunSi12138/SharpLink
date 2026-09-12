# Runtime Architecture Phase 16: Engine public API boundary

Phase 16 makes the business API, Generated ABI, and Runtime engine three separate surfaces. This is
an intentional CLR/source breaking change; Protocol v2, contract IDs, method IDs, DTO member IDs,
and the released Generated API version lineage remain separate from Runtime engine visibility.

## Public-surface diff

The following engine APIs are no longer exported:

| Removed public surface | Replacement / rationale |
|---|---|
| `IRpcSession` | No public Session control object. Application code uses client/server builders, call contexts, and diagnostics snapshots. |
| `IStreamManager`, `IStreamDispatcher`, `IStreamConsumptionAwareDispatcher`, `PooledAsyncStreamDispatcher<T>` | Runtime owns raw frame routing, dispatcher registration, completion races, pooling, and receive-credit accounting. Generated streaming exposes `IAsyncEnumerable<T>`. |
| `RpcSession`, `StreamManager`, `RpcSessionExtensions` | Internal Runtime engine implementation; callers cannot create sessions, mutate peer activity, access protocol readers, or emit arbitrary control frames. |

`IRpcGeneratedServerBridge` remains the sole Generated ABI capability for typed inbound streams and
complete outbound-stream pumping; it does not expose raw payload dispatch, a session, or a stream
registry. The 2.0 call-lifetime work adds the required `EnsureUserCodeEntry(long requestId)` member:
current generated stubs call it after request decode/binding and immediately before business-code
entry, and generated stream adapters use the same ownership rule at deferred user-code re-entry.
This is an exact Generated ABI semantic change even though the release-line integer remains
Generated API 4, so the current `AbiIdentity` must also match and custom generated infrastructure
must be rebuilt against this shape.

## Retained extension points and ownership

| Public SPI | Valid application use | Ownership boundary |
|---|---|---|
| `ITransportConnection` | Provide one connected or accepted duplex transport. | A connection returned to SharpLink is handed to the Runtime. The Session owns and disposes it after hand-off, including terminal startup and shutdown paths. |
| Direct `IClientTransportFactory`, `IServerTransportListener`, and dynamic `ISharpLinkEndpointResolver` instances | Supply the transport or dynamic topology resource configured directly on a builder. | Build materialization transfers these resources into the framework ownership transaction. Rollback disposes them on failure; after commit, the resulting Client/Server disposes them at its terminal lifecycle. |
| `SharpLinkEndpointTransportFactory` | Create a concrete client transport factory for each static or dynamic endpoint generation. | The delegate itself remains caller-owned and is never disposed by SharpLink. Each concrete `IClientTransportFactory` it returns is framework-owned and is disposed during materialization rollback, endpoint retirement, or Client shutdown. |
| `IRpcCodec<T>`, codec adapters | Encode application contract values. | Explicit codec instances are caller-owned and are only retained/invoked. Adapter instances are also only retained/invoked; a disposable adapter scope created for a Runtime Context is framework-owned by that Context. Codecs do not own Session state or frame buffers after a call returns. |
| Client/server interceptors and authenticators | Apply application policy around calls or handshakes. | These instances are caller-owned and are only retained/invoked; SharpLink does not dispose them. The framework owns the invocation and connection lifecycle represented by their documented context values. |
| Endpoint selector, retry/admission policy | Configure endpoint choice and policy. | These instances are caller-owned and are only retained/invoked; SharpLink does not dispose them or transfer transport/Session ownership to them. |
| Logger factory and `TimeProvider` | Supply diagnostics and time semantics. | Caller-supplied instances are retained/invoked but remain caller-owned and are not disposed by SharpLink. |
| endpoints, Client/Server builders | Configure and create application clients/servers. | Builders copy or freeze configuration during materialization. Only the resources identified above enter the framework ownership transaction; other supplied components remain caller-owned unless their public contract explicitly states otherwise. |
| `IRpcGeneratedServerBridge` | Source-generated stub ABI only. | Runtime owns stream dispatch, flow-control credit, serialization buffers, send-pump and terminal arbitration. Current implementations must also provide the required user-code-entry claimant; hand-written business code should not implement this bridge. |

## Migration

Do not construct `RpcSession`, subscribe to lifecycle notifications, read a Session `PipeReader`,
set `LastActive`, register a dispatcher, or call protocol-frame helpers. For a custom transport,
implement `ITransportConnection` and expose it through `IClientTransportFactory` or
`IServerTransportListener`, then configure the appropriate builder. For application diagnostics,
use the existing call/connection diagnostic snapshots rather than retaining a mutable Session
reference.

Custom generated infrastructure must target the current API 4 exact ABI identity and the required
`IRpcGeneratedServerBridge.EnsureUserCodeEntry` contract; do not replace the bridge with a Runtime
concrete type or reconstruct the removed interfaces. A source or binary reference to an older API 4
shape, or to any removed engine type, must be rebuilt against the current 2.0 public/generated
surface.

## Verification

`LegacyApiSurfaceTests.EngineControlSurfaceShouldNotBeExportedAndApprovedSpisRemainImplementable`
and `RawStreamDispatcherTypesShouldNotBeExported` check the metadata-level boundary, including the
exact `SharpLink.Runtime.PooledAsyncStreamDispatcher\`1` sentinel and exported-type inventory. The external
`SharpLink.PackageSmoke.AssertEnginePublicApiBoundary` check compiles real transport, codec, and
interceptor implementations solely against published packages. Generator tests continue to reject
generated references to `SharpLink.Runtime`, `IRpcSession`, `RuntimeContext`, pooled dispatchers,
and Runtime protocol helpers.
