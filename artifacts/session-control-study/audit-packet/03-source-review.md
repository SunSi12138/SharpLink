# 03 — Source Review (S prototype)

Status: source inspection of the research branch, not an approval. Final S commit: `525a5aed918516ac1214a91c99fbb3fbb3a746e9`.

## 3.1 Changed files and commits

| Commit | Files | Nature |
|---|---|---|
| `58c280f4` | `src/SharpLink.Server/{ServerConnectionState,SharpLinkServer.ConnectionLifecycle,SharpLinkServer.ContractManifest,SharpLinkServer.InvocationDispatch,SharpLinkServer.AdmissionDispatch,SharpLinkServer.PreAdmissionStreams,SharpLinkServer.RequestLoop,SharpLinkServer.AssemblyRegistration}.cs`; new `SharpLinkServer.SessionControlStudy.cs`; model; tests | first S prototype |
| `5b8d5fd2` | tests only | F03/F09/F10/F18, harness `connectClient` support |
| `525a5aed` | `SharpLinkServer.AssemblyRegistration.cs`, `SharpLinkServer.AssemblyDrain.cs` | cut-before-publish ordering; unregister retirement |

The exact diff can be reproduced with:

```bash
git diff b499d2cf..525a5aed -- src/SharpLink.Server
```

The prototype does **not** add a new wire frame type, ABI identity, minor version, public API, or generated ABI. It reuses `ProtocolV2FrameType.SessionRefreshRequested` (16), which already existed for desired-session configuration refresh. That reuse is a semantic risk: the existing frame was not designed to carry registry-retirement intent.

## 3.2 Pinned snapshot capture

`ServerConnectionState` now owns:

```csharp
private FrozenDictionary<long, ServiceRegistration>? _pinnedContractServices;
internal FrozenDictionary<long, ServiceRegistration> PinnedContractServices
    => Volatile.Read(ref _pinnedContractServices) ?? throw ...;
```

`MarkReady` takes the snapshot and publishes it before the `Handshaking -> Ready` CAS:

```csharp
Volatile.Write(ref _pinnedContractServices, pinnedContractServices);
...
Interlocked.CompareExchange(ref _lifecycleState, Ready, Handshaking);
```

`SharpLinkServer.ConnectionLifecycle` captures `Volatile.Read(ref _services)` under `_registryGate`, passes it to `MarkReady`, and builds the bootstrap `ContractManifest` from that **same** dictionary reference and the same generation. This gives the handshake one coherent service table plus manifest.

The snapshot is cleared in `CloseCoreAsync` before connection-scoped service cleanup:

```csharp
Volatile.Write(ref _pinnedContractServices, null);
```

So an old session does not keep the old ALC alive after its physical close. The F18 idle test confirms this for the idle path.

## 3.3 Request routing and the cut

Both immediate dispatch paths changed from the live registry:

```csharp
// before
Volatile.Read(ref _services).TryGetValue(request.InterfaceHash, out var serviceInfo)
// after
connection.PinnedContractServices.TryGetValue(request.InterfaceHash, out var serviceInfo)
```

`SharpLinkServer.PreAdmissionStreams.ResolveRawRequestClientStreamCount` now takes the connection and uses the same pinned table. This is important: otherwise an old OneWay rejection path could resolve a different method shape against R2.

The replacement order in `SharpLinkServer.AssemblyRegistration.cs` is now:

```text
prepare/validate all fallible resources (outside lock)
-> under _registryGate:
     revalidate generation/old module
     publish generated codecs (before cut)
     add new module / detached services / unregister operation
     adopt generated manifest
     oldModule.TryBeginDraining()        <-- admission cut
     Volatile.Write(ref _services, new snapshot)
     _registryGeneration++
     ScheduleContractManifestPublish()
-> outside lock:
     OnPinnedGroupCutCommitted(oldModule)
         -> optional test hook
         -> TrackFrameworkTask(RetirePinnedSessionsAsync)
```

### Where is the cut?

`SharpLinkDynamicModule.TryBeginDraining()`:

```csharp
var changed = Interlocked.CompareExchange(
    ref _state,
    (int)SharpLinkDynamicModuleState.Draining,
    (int)SharpLinkDynamicModuleState.Running) == Running;
```

`SharpLinkDynamicModule.TryAcquire` checks `Running`, increments striped counters, then checks `Running` again; if the state changed, it releases and returns false. This is the effective per-call admission gate for dynamic services. `ServiceRegistration.AcceptsCalls` also reports `Module.State == Running` and causes an early `Unavailable` response.

### Is the cut linearizable?

The `TryBeginDraining` CAS on `_state` is the linearization point for “old R stops accepting new starts”. A request either observes `Running` twice in `TryAcquire` and receives a lease, or it observes `Draining` and is rejected. There is no path that acquires an old module lease after the CAS.

This statement is limited to the module gate. The prototype does **not** provide N0's exact per-request registration token, RID receipt, or queue-only start-claim transaction. A request that has already won `TryAcquire` is allowed to finish by design (F06).

### Why a late request cannot be accepted by R2

On a session created before the replacement, the routing table is the immutable bootstrap snapshot. R2 is not in it. A stale request therefore resolves either the old `ServiceRegistration` (which is Draining and rejects) or no entry (`Unimplemented`). It cannot fall back to the live `_services` table. This is why the F05 stale request returns `Unavailable` with both R1 and R2 business counters zero.

This safety property depends on the snapshot being **per physical session**. The prototype does not implement a per-request epoch/token; changing the dispatch lookup back to `_services` would immediately reintroduce C-NEG.

## 3.4 Session retirement notification

New `SharpLinkServer.SessionControlStudy.cs`:

```csharp
private void OnPinnedGroupCutCommitted(SharpLinkDynamicModule oldModule)
{
    PinnedSessionCutCommittedForTesting?.Invoke();
    TrackFrameworkTask(RetirePinnedSessionsAsync(oldModule), "PinnedSessionRetirement");
}
```

`RetirePinnedSessionsAsync` snapshots active connections, selects those whose `PinnedContractServices` contain the old module, and sends:

```csharp
new ProtocolV2SessionRefreshRequested(
    _desiredSessionServerInstanceId,
    checked((ulong)Interlocked.Increment(ref _pinnedSessionRetirementGeneration) + 1_000_000_000UL));
```

The synthetic generation offset avoids collision with the existing desired-session generation space in ordinary operation. It is not an N0 `RegistryEpoch` and is not a receipt.

The task sends one frame per affected connection. It catches only expected shutdown exceptions; other send exceptions can fault the tracked task. There is **no retry, no acknowledgement, and no reconciliation** if the frame is lost, duplicated, or the connection closes before the client acts. This is a P0 availability gap for S, not a safe fallback.

If `SessionRefresh` was not negotiated, the method silently skips the connection. On TCP the capability is normally negotiated; AnonymousPipe does not support it. S therefore has no retirement path for AnonymousPipe and the old session remains pinned to the retired R.

## 3.5 Cold-path cost and per-RPC allocations

- The hot path changes one `Volatile.Read(ref _services)` + frozen-dictionary lookup to one `Volatile.Read(ref _pinnedContractServices)` + frozen-dictionary lookup. There is no new per-request object, closure, task, or async wrapper in `DispatchRpcAsync`/`DispatchOneWayRpc`.
- `PinnedContractServices` is a field read; the property does not allocate.
- `PinsModule` iterates the pinned values only during a replacement/unregister cold path.
- `RetirePinnedSessionsAsync` allocates a connection array and per-session async send only on replacement.
- There is no new lock in the request path. The existing `_registryGate` is taken in handshake, register, replace and unregister; the dispatch path remains lock-free.
- The snapshot is one strong reference per physical connection. It is bounded by the number of active connections and by the number of services. It is not an unbounded per-request table.

## 3.6 Hidden state and lifecycle risks

1. **Pinned strong references.** The snapshot strongly references every `ServiceRegistration` in the session's bootstrap table. While an old session remains alive (for example because a long stream has not completed), the old ALC remains rooted. F18 idle and F18b held-duplex both pass: the held-duplex ALC is retained while the stream is active and collected after release/session retirement. Other stream tails are still unmeasured.
2. **No Register parity.** A service registered after a session is Ready is not in that session's pinned table. The session must be replaced, but `RegisterAssembly` does not send a retirement notification. Existing code that registers after connect then calls a dynamic proxy fails with `Unimplemented` (observed).
3. **No Unregister parity before the fix.** The first prototype only retired on Replace. `525a5aed` adds `OnPinnedGroupCutCommitted` after `UnregisterAssemblyAsync`'s cut, but this path has not been exercised by a dedicated F-case.
4. **Single-shot control frame.** No resend/ack; loss can leave the client on a draining old session with permanent `Unavailable`.
5. **Whole-session scope.** Any session that pins the old module is retired even if it also serves unrelated B (F09). This is the declared S cost, not an accident, but it violates the “unrelated service continuity” expectation in #775.
6. **Semantic mismatch of frame 16.** The existing client refresh path waits for active calls and then cuts the source. It was designed for desired-session configuration, not for a registry cut. The prototype relies on its source-eligibility cut but has not proven that every old active call is protected against early ALC release.

## 3.7 What the source does not prove

- It does not prove F01/F02, F11's late/out-of-order notification subcase, F20 duplicate/reorder, F12–F17/F19/F21–F23, nor the Unregister/server-stream tails of F18. F04 is now covered as a fail-closed case, F18b covers a held duplex Replace, and F24 covers five cycles plus Stop public counters, but not every stream-owner/task census case.
- It does not prove control-frame loss/reorder/duplicate safety; the implementation has no recovery for loss.
- It does not provide the N0 exact token, receipt, group queue-only cut, or physical-entry handoff.
- It does not produce performance evidence. No claim of hot-path improvement is made.
