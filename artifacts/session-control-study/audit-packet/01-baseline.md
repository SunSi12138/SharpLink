# G0 Baseline Audit — SharpLink Issue #998

Status: `PASS` for baseline/source inventory (read-only). No product code was modified before this audit.

## 1. Frozen baseline

| Item | Observed value | Evidence |
|---|---|---|
| Repository | `https://github.com/SunSi12138/SharpLink` | `git remote -v` |
| Baseline branch | `dev` | `git rev-parse HEAD` |
| Baseline SHA | `2948de82fff77d66fce51ecec9a5783b6ef3cb62` | `logs/g0/git-rev-parse-head.txt` |
| Baseline worktree | clean (`git status --porcelain` empty) | `logs/g0/git-status-porcelain.txt` |
| Baseline commit | `fix: preserve codec publication across metadata reentrancy (#985)` (2026-11-15 15:17:12 +0800) | `logs/g0/git-log.txt` |
| Research branch | `study/998-session-control` | `git branch --show-current` |
| Toolchain-only commit | `b499d2cfff22a424ea3448c5be73fbeba49bcb99` (`chore(study): use installed .NET SDK 10.0.404 for issue 998`) | `git show --stat b499d2cf` |
| Host | Windows 11 Pro for Workstations 10.0.26200, x64 | `logs/g0/host.json` |
| CPU | AMD Ryzen 9 7950X, 16 physical / 32 logical, 4501 MHz | `logs/g0/host.json` |
| Memory | 67,772,514,304 bytes visible | `logs/g0/host.json` |
| SDK used | .NET SDK `10.0.404` at `C:\Program Files\dotnet\sdk\10.0.404` | `dotnet --version` |
| Runtime used by tests | Microsoft.NETCore.App `10.0.15` | TUnit banner and `dotnet --list-runtimes` |

### Toolchain deviation (declared, not silent)

`global.json` in the repository requests SDK `10.0.102` with `rollForward: latestPatch`. The host has SDKs `10.0.404` and `11.0.100`, but not `10.0.102`. An attempt to install `10.0.102` was stopped when the user directed use of the installed local .NET 10 SDK. The research branch therefore changes only `global.json` from `10.0.102` to `10.0.404` in commit `b499d2cf`. The target frameworks, product source, test source, wire constants, and analysis baselines are unchanged. This is an environment deviation relative to #998's frozen toolchain and must be carried into any performance conclusion.

## 2. Repository/protocol identity observed at G0

| Identity | Actual value | Source |
|---|---|---|
| Protocol major / header | Protocol v2, magic `0x89`, 15-byte header, 16-byte request prefix | `src/SharpLink.Abstractions/ProtocolV2.cs:7-13` |
| Protocol minor / floor | `4` / `4` | `src/SharpLink.Abstractions/ProtocolV2.cs:16,19` |
| Request route prefix | `ContractId:u64 + MethodId:u64` (16 bytes); no BindingId | `ProtocolV2Constants.RequestPrefixBytes`, `ProtocolV2FrameType.Request` |
| Frame types | `0..16`; `ContractManifest = 13`, `SessionRefreshRequested = 16` | `src/SharpLink.Abstractions/ProtocolV2.cs:23-59` |
| Generated API version | `5` | `src/SharpLink.Generator/RpcGenerator.ManifestEmitter.cs:5,58,77`; `SharpLinkGeneratedManifestVersions` |
| Generated ABI identity | `sharplink-3.0-api5-owner-bound-unsafe-blit-v1` | `src/SharpLink.Abstractions/SharpLinkGeneratedAssemblyManifest.cs:163` |
| Public proxy factory | `ISharpLinkClient.Get<T>()` / `GetWithMetadata<T>()` | `src/SharpLink.Abstractions/ISharpLinkClient.cs:443,448`; `src/SharpLink.Client/SharpLinkClient.Lifecycle.cs:328,348` |
| Server publication API | `RegisterAssembly`, `ReplaceAssemblyAsync`, `UnregisterAssemblyAsync` | `src/SharpLink.Server/SharpLinkServer.AssemblyRegistration.cs:9,158`; `SharpLinkServer.AssemblyDrain.cs:7` |

`dev` is **not** an N0 implementation. It has no `BindingId`, `BindContract`, `BindAck`, `BindingInvalidated`, `BindingRelease`, `RequestAccepted`, or `OneWayInputRequest`; frame types `17..22` are absent. It has no `ServerInstanceId` / `RegistryEpoch` handshake fields and no per-request exact-token admission. This matches #998's warning that `SharpLinkClient.ContractBinding.cs` is a **remote Manifest cache**, not a published N0 binding.

## 3. Key source paths and current behavior

### 3.1 Manifest production and broadcast

- `src/SharpLink.Server/SharpLinkServer.ContractManifest.cs:11-31`: `CreateContractManifestSnapshot()` takes `_registryGate`, reads `_services` and `_registryGeneration`, and materializes a sorted contract/hash list.
- `src/SharpLink.Server/SharpLinkServer.ContractManifest.cs:33-88`: `ScheduleContractManifestPublish()` starts one asynchronous broadcast; `ContractManifestPublishBarrierForTesting` can pause the broadcast **after** the registry commit, which is exactly the G2 injection point named by #998. The barrier does not pause the commit or the server's business-routing table.
- `src/SharpLink.Server/SharpLinkServer.ContractManifest.cs:55-70`: broadcast iterates `_connectionRegistry.SnapshotActive()` and sends the current snapshot to every `Ready` connection with the `ContractManifest` capability.
- `src/SharpLink.Server/SharpLinkServer.ConnectionLifecycle.cs:149-167`: with `ContractManifest`, the server holds `_registryGate`, marks the connection Ready, and sends the bootstrap manifest from the same locked snapshot. The request loop starts only after this flush.
- `src/SharpLink.Client/SharpLinkClient.ContractBinding.cs:11-43`: client caches one manifest per `RpcSession`; it rejects older generations and publishes an immutable array of `(Session, Manifest)`.
- `src/SharpLink.Client/SharpLinkClient.ContractBinding.cs:44-48`: only the *initial* manifest validates already acquired proxies. Later refreshes deliberately do **not** rebind or invalidate held proxies.
- `src/SharpLink.Client/SharpLinkClient.Lifecycle.cs:328-346`: `Get<T>()` validates the remote manifest and creates/reuses the generated proxy; this is a remote-availability gate, not an N0 local `CreateProxy` that succeeds before connect.

### 3.2 Registration, replacement, unregister

- `src/SharpLink.Server/SharpLinkServer.AssemblyRegistration.cs:9-156` (`RegisterAssembly`): builds a candidate off-lock, then under `_registryGate` publishes the complete candidate dictionary at line 111, adds the module at 112, increments `_registryGeneration` at 113, and schedules a manifest broadcast at 114.
- `src/SharpLink.Server/SharpLinkServer.AssemblyRegistration.cs:158-360` (`ReplaceAssemblyAsync`): this is the current replacement transaction. Its commit block is lines 300-312:
  - line 303 add new module to `_dynamicModules`,
  - line 304 retain detached old services,
  - line 305 add the unregister operation,
  - line 306 adopt generated manifest,
  - **line 307 publishes the new `_services` dictionary**,
  - line 308 increments `_registryGeneration`,
  - line 309 schedules manifest broadcast,
  - **line 310 calls `oldModule.TryBeginDraining()`**.
  Therefore the current product publishes the new registry **before** it closes the old module's admission gate. That is the opposite of N0's required order (cut first, then publish) and is the concrete source-level reason a stale request can be retargeted to R2 during the manifest-notification window.
- `src/SharpLink.Server/SharpLinkServer.AssemblyDrain.cs:7-44` (`UnregisterAssemblyAsync`): calls `TryBeginDraining()` at line 27, increments generation and invalidates codec publication at 30-31. It does not publish a per-registration invalidation control frame.
- `src/SharpLink.Runtime/SharpLinkDynamicModule.cs:236-305`: `ForcedCancellation`, `RemainingCalls`, `TryBeginDraining()`, `CancelRemainingCalls()`. `TryBeginDraining()` changes the module state; `AcceptsCalls` is derived from `State == Running` (`ServiceRegistration.cs:119`).

### 3.3 RPC dispatch and admission

- `src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs:5-80` (`DispatchRpcAsync`):
  - line 23 reads the request envelope (ContractId + MethodId),
  - **line 44 resolves the service from the live `_services` dictionary at request time**,
  - line 63 checks `serviceInfo.AcceptsCalls` and rejects with `Unavailable` when the module is draining,
  - line 83+ starts admission/queueing.
- `src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs:5-76` is the OneWay equivalent; line 26 also resolves from live `_services`, line 65 checks `AcceptsCalls`.
- `src/SharpLink.Server/SharpLinkServer.DispatchSelection.cs:1-28` (`QueuedServiceSelection`): a queued admission captures either a strong static service or a weak dynamic identity. It does not hold a per-session registration snapshot. The comment explicitly says the waiter must never resolve a different generation when it resumes.
- `src/SharpLink.Server/SharpLinkServer.AdmittedRpcDispatch.cs:7-95`: on resumption after admission, it reuses `serviceSelection`, then checks `AcceptsCalls` at line 66. Combined with `QueuedSelectionRetirementShouldPreserveEveryRpcShape`, the current P already prevents a **queued** old request from being retargeted to R2. It does not prevent a **newly arrived** request between commit and client notification from resolving R2, because that request performs the line-44/26 lookup against the already-published new dictionary.
- `src/SharpLink.Server/Admission/ServerCallAdmission.cs` and `SharpLinkServer.CallPermit.cs` implement call capacity; `SharpLinkServer.QueuedAdmission.cs` handles the post-admission continuation. The admission permit is separate from the registry generation and does not by itself pin a registration.

### 3.4 Session creation, handshake, refresh

- `src/SharpLink.Runtime/RpcSession.Negotiation.cs:5-45`: `TryCompleteHandshake` transitions `Handshaking -> Ready` and publishes negotiated options.
- `src/SharpLink.Server/SharpLinkServer.ConnectionLifecycle.cs:54-64`: creates `ServerConnectionState` and registers it.
- `src/SharpLink.Server/SharpLinkServer.ConnectionLifecycle.cs:149-181`: Ready transition, bootstrap manifest, request loop.
- `src/SharpLink.Runtime/RpcSession.SessionRefresh.cs:5-36`: receives frame `16`, validates server-to-client direction/capability, and raises `SessionRefreshRequested`.
- `src/SharpLink.Client/SharpLinkClient.SessionRefresh.cs:128-168`: fixed-pool handler stores refresh debt; `ReplaceFixedSessionAsync` creates a replacement physical connection, then commits the source retirement.
- `src/SharpLink.Client/SharpLinkClient.SessionRefresh.cs:455-479`: the source is removed only after `CallAdmissionReservationCount == 0 && ActiveCallCount == 0`; this is the existing physical-drain boundary that S can reuse.
- `src/SharpLink.Server/SharpLinkServer.DesiredSession.cs:216-316`: the server currently sends `SessionRefreshRequested` only for desired-session configuration changes, **not** for registry Replace/Unregister.

### 3.5 Queue, stream, codec, ALC physical lifecycle

- `src/SharpLink.Runtime/SharpLinkDynamicModule.cs`: striped call counters and drain state; `TryBeginDraining` at 276, `CancelRemainingCalls` at 305, and `WaitForDrainAsync` around 340. `ReferencesReleased` is derived from physical counters, not from logical completion.
- `src/SharpLink.Server/SharpLinkServer.AssemblyDrain.cs:52-86`: unregister waits for physical drain, optionally cancels remaining calls, and only then releases the module.
- `src/SharpLink.Server/ServerConnectionState.cs:403-438` (`CleanupServicesWhenCallsDrainAsync`): connection-scoped services are disposed only after `_callsDrained`, then the dictionary is cleared; this is the server-side physical owner path.
- `src/SharpLink.Server/SharpLinkServer.OutputPhysicalTail.cs` does not exist; the output physical-tail source is in `RpcSession`/generated bridge and the regression is `test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.OutputPhysicalTail.cs`. No output-tail source was modified in the S prototype.
- Existing tests cited by #998 are the primary oracles for this study:
  - `test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.RemoteContractReadiness.cs`
  - `test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.QueuedSelection.cs`
  - `test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.QueuedReplacementCancellation.cs`
  - `test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.Support.cs`

## 4. Source-level differences from #775 N0

| N0 obligation | Actual dev state | Consequence for #998 |
|---|---|---|
| `BindingId:u64 + MethodId:u64`, exact token | absent; 16B is `ContractId + MethodId` | N is not implemented; N performance is `NOT_IMPLEMENTED`, not a comparison arm |
| `BindContract` / `BindAck` / invalidate / release frames 17-20 | absent | no per-session binding table; no exact-token admission |
| `RequestAccepted` frame 21 for client-streaming/duplex | absent; streaming uses existing pending-call machinery | Q/S cannot assume per-call start claim from N0 |
| `OneWayInputRequest` frame 22 | absent | OneWay-input wire-shape plan not implemented |
| `ServerInstanceId` / `RegistryEpoch` in handshake/manifest | absent | S snapshot identifiers must be synthesized test-only, not claimed as N0 receipt |
| Atomic whole-group cut before new registry publication | replacement publishes at line 307 then cuts at 310 | P is expected to fail F05; a candidate must change this ordering |
| Queue-only cut notification for Waiting requests | absent | queued old requests are protected by `AcceptsCalls` + weak identity, but the notification semantics differ |
| Exact RID/receipt/ABA | absent | N/A for P; any S/Q claim must state it does not provide N0 receipt |

## 5. Read-only conclusion

`G0 = PASS` for inventory and baseline freeze. The current `dev` baseline is **P**, not N0. It has a concrete publish-before-cut window and routes new requests through the live registry at `InvocationDispatch.cs:44` / `AdmissionDispatch.cs:26`. That is enough to justify the G1 negative control and to predict F05 failure for P/C-NEG; it is not evidence that any candidate is correct.

No product code was changed in G0. At the G0 freeze point the only commit after the frozen `dev` baseline was the declared `global.json` toolchain change on the research branch. Later S commits are listed in `02-execution-ledger.md`.
