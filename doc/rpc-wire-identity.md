# RPC wire identity, local proxies and registration receipts

SharpLink 3.0 separates local proxy construction, selection of an eligible connection, and admission against the server's current registration. Success at an earlier boundary does not guarantee a later one. See [ADR 0003](adr/0003-local-proxies-and-server-contract-admission.md) for tradeoffs and [Protocol v2](protocol-v2.md) for the wire layout.

## Local proxy construction

`CreateProxy<TContract>(RpcProxyOptions? options = null)` is synchronous and local. `Get<TContract>()` has the same semantics with default options. Both work before `ConnectAsync`, without Ready connections, and with absent or stale remote manifests. They validate the local generated descriptor, nonempty `RpcAssemblyHash`, codec ownership, module lifecycle, client terminal state and options. They never wait for an endpoint or perform a hidden network round trip.

A proxy freezes its local `ContractId`, `RpcAssemblyHash`, codec owner, module generation, target client/cluster scope and immutable options. It captures no remote session or service lease. Replacing or unregistering its local module retires the old proxy for new calls; an already started call holds its original module lease until completion. The application must release old proxy and Type references before its collectible AssemblyLoadContext can be collected.

## Control plane and Picker

Topology, Ready/disconnected sessions, handshake identity, manifests and local registration changes rebuild immutable route entries. Ordinary entries are indexed by local contract ID/hash and contain eligible sessions within the client's target scope. Receipt entries also use the server instance and registry epoch established by the session's successful handshake.

Each invocation reads its current route entry, applies the existing endpoint selection policy to the eligible set, and reserves the actual selected session. A stale selection can fail; it cannot redirect a call to an unselected replacement connection. Requests and established streams retain that session. No invocation scans the complete remote manifest or constructs a readiness report.

The default and currently supported proxy policy is **FailFast**: an empty eligible set returns `Unavailable` with detail `NoRoutableTarget`, before request serialization or stream producer startup. This observes routing availability, not permanent incompatibility. This change adds no implicit RPC waiting queue or `WaitForReady` proxy option. Existing explicit readiness/manifest waits remain available as observation barriers. Their cancellation budget belongs to the explicit wait; it is not silently added to an RPC deadline.

Interceptors, eligible unary retries, send backpressure, stream establishment and responses share the original logical call's cancellation and deadline. Non-idempotent calls, OneWay and streaming do not gain automatic replay.

## Server admission is authoritative

`ContractId` and `MethodId` are lookup identifiers. Equality does not establish compatible payloads. The generator computes the assembly's deterministic 128-bit `RpcAssemblyHash` from its canonical RPC wire surface; compatibility requires exact equality.

Every Request carries contract ID, method ID, assembly hash, invocation shape, client stream count and optional receipt in an uncompressed bounded prefix. Before business decoding, service activation or interceptors, the server looks up the current registration, validates identity and shape, checks the version constraint, and acquires that same registration's call lease within its registry/lifecycle boundary. Replacement after that boundary cannot change the admitted call's codec or service. A failed lease acquisition rejects the request; it never falls back to a replacement registration.

| Situation | Code / detail |
| --- | --- |
| No eligible client route | `Unavailable / NoRoutableTarget` |
| Registration no longer admits calls | `Unavailable / RegistrationRetired` |
| Contract is not registered on the server | `Unimplemented / ContractNotRegistered` |
| Method is not implemented | `Unimplemented / MethodNotImplemented` |
| Contract hash disagrees | `FailedPrecondition / ContractMismatch` |
| Server, epoch or registration constraint disagrees | `FailedPrecondition / ReceiptMismatch` |

Malformed framing, malformed identity prefixes and invocation shapes inconsistent with a matching method descriptor remain connection-level protocol violations. A well-formed but stale request gets a structured call rejection. Remote manifests improve selection; they do not replace the final check.

## Receipts across independent connections

Successful server service registration or replacement returns `Receipts`, one scalar `ContractRegistrationReceipt` per published service contract. A contract-only local registration has no remotely callable registration and returns no receipt. Each receipt contains:

- `ServerInstanceId` and `RegistryEpoch`, identifying one server registry timeline;
- `Generation`, the registration's publication version within that timeline;
- `ContractId` and `RpcAssemblyHash`, identifying the wire contract;
- `RegistrationId`, identifying the exact registration, including same-hash replacements.

Applications can return these scalar fields from an application-owned control RPC DTO and reconstruct a receipt on the data client. The receipt type does not add a general business serialization codec. After loading the matching local contract generation, an application can use:

```csharp
var proxy = client.CreateProxy<IMyContract>(new RpcProxyOptions
{
    Receipt = receipt,
    Requirement = RpcRegistrationRequirement.ExactRegistration
});
await proxy.ExecuteAsync(cancellationToken);
```

The explicit receipt path selects only sessions whose handshake identifies its server and registry epoch, within the existing cluster/endpoint scope. It can send even when the latest manifest has not advertised the receipt's hash. The server checks the actual current registration. A receipt never makes the operation eligible on another replica or an unknown identity.

| Requirement | Server check after matching receipt server and epoch |
| --- | --- |
| `Compatible` | Current contract ID and assembly hash match. |
| `MinimumVersion` | Compatible, and current registration generation is at least the receipt's generation. |
| `ExactRegistration` | MinimumVersion, with the same registration ID. |

`MinimumVersion` and `ExactRegistration` require a receipt. With no receipt, `Compatible` uses the ordinary manifest-derived route. An explicit receipt with `Compatible` still limits routing to its server and epoch. Generation zero is valid for initial static registrations. A later registration can invalidate an exact receipt even with an unchanged hash. A server restart or different registry epoch invalidates receipts for the old timeline.

Receipt identity is bound to the selected session's handshake. It is not authentication, authorization, artifact authority, a cluster-wide ordering number, or delivery acknowledgement. Existing transport authentication and target scope still apply. A receipt is not cryptographic proof of the server's claims when the application has not authenticated its transport.

## OneWay and streams

OneWay completion means local send/queue acceptance. It does not prove server acceptance, decoding or execution. There is no per-request OneWay ACK. The server rejects mismatches before the wrong codec or service runs. The bounded shape prefix lets it discard declared OneWay client streams safely; malformed shapes fail closed.

Two-way ClientStreaming and DuplexStreaming use an empty `RequestAccepted` frame after server contract/version/resource admission and receive-stream reservation. The client waits on its existing pending-call lifecycle before starting any producer. Rejection, cancellation, deadline or stop completes that wait without starting a producer. The ACK permits production; it does not claim business success. Unary and ServerStreaming need no producer gate, and OneWay never waits for it.

An established stream holds its selected session and admitted registration/codec until terminal cleanup. Later manifest, topology or registry changes cannot migrate it. Flow control, bounded queues, cancellation and drain retain their existing ownership rules.

## Discovery and migration

The initial manifest remains part of connection bootstrap into the ordinary callable pool; it no longer validates every proxy created before connection. Registry changes publish newer snapshots and update routes. Manifest generations are ordered per source timeline, never across independent servers. `WaitForRemoteContractAsync` observes advertised wire identity, not a server lease or exact registration.

Protocol minor 5 is the breaking grammar boundary for the request prefix, server registry identity in the handshake, and stream admission frame. Older peers fail negotiation before business traffic. Deploy matching 3.0 peers; there is no hash downgrade or per-method fallback. Generated API/ABI identity is unchanged by this work. Reviewed public API baselines separately cover the additive proxy and receipt APIs.

## Verification

`SharpLinkClientContractRoutingTests` and `SharpLinkClientStreamAdmissionTests` cover local creation, mixed routes, receipt scope, strict session reservation and producer gating. `ServerContractAdmissionTests` checks identity/version/shape and lifecycle ownership. `RuntimeAssemblyIntegrationTests.ContractReceipts` uses independent real TCP control/data connections with blocked manifest publication, all five RPC shapes and a replacement hash. See [issue #775](https://github.com/SunSi12138/SharpLink/issues/775), the canonical [PR Fast gate](pr-fast.md), and [performance evidence](issue-775-performance.md).
