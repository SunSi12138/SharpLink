# Generation control extension

`SharpLink.GenerationControl` is an optional, statically compiled generation-declaration contract for deployments where either endpoint can change generation first. It is deliberately separate from Protocol v2 and the ordinary RPC data path.

The central rule is:

> Peer can inform me, but peer cannot mutate me.

## Transport direction and peer semantics

SharpLink v2 currently permits RPC requests to be initiated by the Client. A Server-sent `Request` is a protocol violation, so peer synchronization must not assume a Server can call a Client service.

The extension therefore maps logical peer-to-peer synchronization onto the existing transport direction:

- `GetInventoryAsync` lets the Client query the Server's current declared inventory.
- `SynchronizeAsync(clientInventories, ...)` is a Client-initiated duplex stream. The Client sends its own complete inventories/revisions through the request stream; the Server returns its own complete inventories/revisions through the response stream.

Both directions carry declarations only. Neither side receives an RPC that stages, validates, activates, drains, loads, restarts, or otherwise mutates the other endpoint.

## Source of truth and reconnects

Each endpoint's local inventory is the source of truth for that endpoint.

A synchronization stream should begin by sending the endpoint's current complete inventory. Later stream items carry newer complete revisions. After disconnect, reconnect, notification loss, or process restart, the Client opens a new synchronization stream and sends its current inventory again; the Server likewise returns its current inventory again.

This avoids correctness depending on replaying an event log or on both sides switching at the same instant.

## Inventory and compatibility metadata

Each inventory has a monotonic endpoint-local revision and zero or more generation snapshots. A snapshot carries capability/generation identity, wire identity, generated ABI identity, artifact identity/reference, compatibility metadata, local lifecycle state, and a diagnostic code.

Remote metadata is input to local policy. Applications must not treat a peer-provided artifact reference or compatibility string as permission to execute or load anything.

## Local reconciliation

Either endpoint may change first.

A normal Server-first flow is:

```text
Server g11 -> g12
      |
      +-- server inventory on duplex response --> Client
                                                  |
                                                  +-> compare local/peer
                                                  +-> local policy decides
                                                  +-> local provider updates Client if needed
                                                  +-> Client publishes its new inventory
```

A Client-first flow uses the same stream in the opposite data direction:

```text
Client g11 -> g12
      |
      +-- client inventory on duplex request --> Server
                                                 |
                                                 +-> compare local/peer
                                                 +-> local policy decides
                                                 +-> local provider updates Server if needed
                                                 +-> Server publishes its new inventory
```

`ISharpLinkGenerationProvider` is a local SPI with stage, validate, activate, and drain operations. It separates reconciliation policy from materialization. Provider outcomes are structured through `SharpLinkGenerationOperationStatus`; human-readable messages are diagnostic only.

The package does not choose a global authority, implement version solving, or define an automatic rollout algorithm.

## Lifecycle

A local provider can report:

`Absent -> Staged -> Validated -> Ready -> Active -> Draining -> Removed`

`Failed` records a failed local preparation/activation observation. The package does not implement automatic rollback.

## JIT and NativeAOT

A JIT provider can map local stage/validate/activate/drain to collectible `AssemblyLoadContext`, `ISharpLinkAssemblyRegistry`, and application-specific artifact verification.

For NativeAOT, unknown managed assemblies are not loaded in-process. A local provider may return `ProcessReplacementRequired`; the application/deployment layer stages a new binary, starts/restarts the process, and uses normal SharpLink drain/stop semantics for the old process.

The peer only learns the resulting declared generation state. It does not initiate process replacement.

## Non-goals

The package does not provide artifact transfer, credentials, signing roots, signature verification infrastructure, version solving, rollout percentages, health policy, automatic rollback, remote code-execution semantics, or Server-initiated RPC.
