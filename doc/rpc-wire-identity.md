# RPC wire identity and contract binding

SharpLink uses deterministic generated identities to separate fast lookup from wire-compatibility validation.

## Identity model

`ContractId` and `MethodId` are compact lookup identifiers. Equality of either identifier is not proof that two independently built contract assemblies have the same wire shape.

Each generated contract assembly therefore carries a deterministic 128-bit `RpcAssemblyHash`. The generator derives this hash from the canonical RPC wire surface of the assembly. Two peers may bind an RPC contract only when the local and remote `RpcAssemblyHash` values are exactly equal.

The compatibility check is strict:

- equal `RpcAssemblyHash` values satisfy the wire-compatibility check;
- different hashes reject contract binding;
- a missing remote contract entry rejects binding;
- an empty local generated hash is invalid.

There is no fuzzy or per-method fallback after an assembly hash mismatch.

## Remote discovery

Protocol v2 negotiates the `ContractManifest` capability. A server publishes a deterministic snapshot containing:

- a monotonically increasing registry generation;
- each remotely callable `ContractId`;
- the `RpcAssemblyHash` of the generated assembly that owns that contract.

The server sends the initial `ContractManifest` after the handshake response. The client receives it before making the connection Ready. Ready means the connection is available to prepare contracts; individual proxies can call only after their own preparation.

## Bind-time validation

`CreateProxy<TContract>()` creates a local handle. Ordinary `PrepareAsync` checks the selected connection's manifest before sending `Bind`. Coordinated replacement can use its observed target instead of the broadcast manifest. In both cases the server checks its current registration. A successful `BindAck` grants a `BindingId` for that exact registration on that physical session. Metadata belongs to the calls, not to the bind identity.

A manifest mismatch fails with `FailedPrecondition`. Its diagnostic identifies the contract, local assembly/hash, remote hash, session, and remote manifest generation. If the registration changes after the manifest check, the server can reject the bind, which fails preparation with `Unavailable`. Neither failure sends a business `Request`.

Normal requests carry `BindingId + MethodId`. They do not carry or recompare the assembly hash. A binding cannot be reused on another connection; prepare again after reconnecting.

## Dynamic assembly lifecycle

When the server registers, replaces, or drains a dynamic RPC assembly while running, it coalesces registry changes and publishes a newer manifest snapshot to ready clients.

A newer snapshot informs future preparations. It does not retarget an existing binding. Work already admitted to the old registration keeps it until physical completion. Use [coordinated replacement](coordinated-version-change.md) to resume a prepared route on the same connection when both sides agree.

During server shutdown, manifest publication is not scheduled. Registry cleanup must not create new framework work after draining begins.

## Operational interpretation

A hash mismatch means the peers were generated from different RPC wire contracts even if their lookup IDs happen to collide. Deploy matching generated contract assemblies, or complete the intended rolling replacement/drain sequence, rather than bypassing the validation.
