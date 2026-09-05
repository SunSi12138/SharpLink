# RPC wire identity and contract binding

SharpLink uses deterministic generated identities to separate fast lookup from wire-compatibility validation.

## Identity model

`ContractId` and `MethodId` are compact lookup identifiers. Equality of either identifier is not proof that two independently built contract assemblies have the same wire shape.

Each generated contract assembly therefore carries a deterministic 128-bit `RpcAssemblyHash`. The generator derives this hash from the canonical RPC wire surface of the assembly. Two peers may bind an RPC contract only when the local and remote `RpcAssemblyHash` values are exactly equal.

The compatibility invariant is strict:

- equal `RpcAssemblyHash` values permit contract binding;
- different hashes reject contract binding;
- a missing remote contract entry rejects binding;
- an empty local generated hash is invalid.

There is no fuzzy or per-method fallback after an assembly hash mismatch.

## Remote discovery

Protocol v2 negotiates the `ContractManifest` capability. A server publishes a deterministic snapshot containing:

- a monotonically increasing registry generation;
- each remotely callable `ContractId`;
- the `RpcAssemblyHash` of the generated assembly that owns that contract.

The initial snapshot is a connection-bootstrap step. The normal protocol handshake is completed first, then the server sends the initial `ContractManifest`; the client does not publish that connection into its callable ready pool until the manifest has been received and any proxy acquired before `ConnectAsync` has been validated.

This preserves the synchronous `Get<TContract>()` API. `Get<TContract>()` never performs a hidden network round trip.

## Bind-time validation

For a connected client, `Get<TContract>()` and `GetWithMetadata<TContract>()` compare the local generated assembly hash with the latest discovered remote manifest snapshot before returning or creating a proxy.

A mismatch fails with `FailedPrecondition`. The diagnostic identifies the contract, local assembly/hash, remote hash, session, and remote manifest generation. Rejection happens before an RPC `Request` payload is emitted.

The request/response wire layout does not carry `RpcAssemblyHash`, and the normal invocation hot path does not repeat the comparison on every RPC.

## Dynamic assembly lifecycle

When the server registers, replaces, or drains a dynamic RPC assembly while running, it coalesces registry changes and publishes a newer manifest snapshot to ready clients.

A client uses the newer snapshot for subsequent `Get<TContract>()` calls. A proxy reference already returned to application code is not silently rebound or replaced by a manifest refresh; existing assembly drain/replacement lifecycle rules remain responsible for retiring old bindings.

During server shutdown, manifest publication is not scheduled. Registry cleanup must not create new framework work after draining begins.

## Operational interpretation

A hash mismatch means the peers were generated from different RPC wire contracts even if their lookup IDs happen to collide. Deploy matching generated contract assemblies, or complete the intended rolling replacement/drain sequence, rather than bypassing the validation.
