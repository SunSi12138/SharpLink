# #801 — 45 non-public method removal candidates: verified compile + tests

Source baseline: `dev` @ `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`. Only an **ephemeral GitHub Actions checkout** was modified; nothing under `src/` was pushed. Reproducible run: [Actions #38064370899](https://github.com/SunSi12138/SharpLink/actions/runs/38064370899).

## Actual evidence

| Check | Result |
|---|---|
| Candidate method declarations removed | **45** (requested 46; 1 skipped because it has no normal method body) |
| Complete Release `Sharplink.slnx` after removal | **PASS — 0 warnings, 0 errors** |
| UnitTests on removed source | **PASS — 2018/2018** |
| Generator Tests on removed source | **PASS — 269/269** |
| Pristine dev Release rebuild | **PASS** |
| Pristine dev UnitTests | **PASS — 2018/2018** |

Compiler proof is not the same as AOT/trim/ALC/runtime proof, and these candidates are not yet removed in any production branch. The method list below is the exact source edit set used by the passed run; it is not automatically all of tier A.

## SharpLink.Abstractions (7)

- [`SharpLink.Abstractions.RpcDeadline.GetRemaining(long, long)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Abstractions/RpcDeadline.cs#L135) (`src/SharpLink.Abstractions/RpcDeadline.cs:135`)
- [`SharpLink.Abstractions.RpcGeneratedCodecWire.WriteUInt32(System.Buffers.IBufferWriter&lt;byte&gt;, uint)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs#L447) (`src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:447`)
- [`SharpLink.Abstractions.SharpLinkResourceExhaustion.CreateRemote(SharpLink.Abstractions.SharpLinkErrorCode, string)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Abstractions/SharpLinkResourceExhaustion.cs#L39) (`src/SharpLink.Abstractions/SharpLinkResourceExhaustion.cs:39`)
- [`SharpLink.Abstractions.SharpLinkTime.AddElapsedDuration(long, System.TimeSpan, long)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Abstractions/SharpLinkTime.cs#L27) (`src/SharpLink.Abstractions/SharpLinkTime.cs:27`)
- [`SharpLink.Abstractions.SharpLinkTimer.DelayAsync(System.TimeSpan, System.Threading.CancellationToken)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Abstractions/SharpLinkTimer.cs#L8) (`src/SharpLink.Abstractions/SharpLinkTimer.cs:8`)
- [`SharpLink.Abstractions.SharpLinkTimer.WaitAsync(System.Threading.Tasks.Task, System.TimeSpan, System.Threading.CancellationToken)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Abstractions/SharpLinkTimer.cs#L215) (`src/SharpLink.Abstractions/SharpLinkTimer.cs:215`)
- [`SharpLink.Abstractions.SharpLinkTimer.WaitAsync(System.Threading.SemaphoreSlim, System.TimeSpan, System.Threading.CancellationToken)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Abstractions/SharpLinkTimer.cs#L355) (`src/SharpLink.Abstractions/SharpLinkTimer.cs:355`)

## SharpLink.Client (5)

- [`SharpLink.Client.ClientAssemblyRegistry.TryGetProxyRegistration(System.Type, out SharpLink.Client.SharpLinkClient.ClientProxyRegistration)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Client/ClientAssemblyRegistry.cs#L64) (`src/SharpLink.Client/ClientAssemblyRegistry.cs:64`)
- [`SharpLink.Client.GeneratedClusterRouteSnapshot.FromManifests(System.Collections.Generic.IReadOnlyList&lt;SharpLink.Abstractions.ISharpLinkGeneratedClusterRouteManifest&gt;)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Client/GeneratedClusterRouteSource.cs#L93) (`src/SharpLink.Client/GeneratedClusterRouteSource.cs:93`)
- [`SharpLink.Client.SharpLinkClient.CreateAuthenticationRejectedException(string)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Client/SharpLinkClient.cs#L273) (`src/SharpLink.Client/SharpLinkClient.cs:273`)
- [`SharpLink.Client.SharpLinkClient.SendRpcCall(SharpLink.Runtime.RpcSession, long, long, long, SharpLink.Abstractions.ProtocolV2FrameFlags, System.Action&lt;System.Buffers.IBufferWriter&lt;byte&gt;&gt;?, SharpLink.Abstractions.RpcDeadline, SharpLink.Sdk.SharpLinkMetadata?)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Client/SharpLinkClient.RpcChannel.cs#L5) (`src/SharpLink.Client/SharpLinkClient.RpcChannel.cs:5`)
- [`SharpLink.Client.SharpLinkMultiClusterClientBuilder.PrepareRuntimeCluster(SharpLink.Abstractions.SharpLinkClusterKey, SharpLink.Client.SharpClientBuilder, bool)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Client/SharpLinkMultiClusterClientBuilder.cs#L234) (`src/SharpLink.Client/SharpLinkMultiClusterClientBuilder.cs:234`)

## SharpLink.Generator (11)

- [`SharpLink.Generator.RpcGenerator.DtoAnalysisState.GetAdapterTargetLogicalIdentity(SharpLink.Generator.GeneratedCodecModel)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.AdapterClosedIdentity.cs#L7) (`src/SharpLink.Generator/RpcGenerator.AdapterClosedIdentity.cs:7`)
- [`SharpLink.Generator.RpcGenerator.DtoAnalysisState.Report(SharpLink.Generator.DtoDiagnosticKind, Microsoft.CodeAnalysis.ISymbol, string, Microsoft.CodeAnalysis.Location?)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.CodecPolicySupport.cs#L69) (`src/SharpLink.Generator/RpcGenerator.CodecPolicySupport.cs:69`)
- [`SharpLink.Generator.RpcGenerator.DtoAnalysisState.IsNativeCodecType(Microsoft.CodeAnalysis.ITypeSymbol)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.CodecRoutes.cs#L297) (`src/SharpLink.Generator/RpcGenerator.CodecRoutes.cs:297`)
- [`SharpLink.Generator.RpcGenerator.HasSameCodecDefinition(SharpLink.Generator.GeneratedCodecModel, SharpLink.Generator.GeneratedCodecModel)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs#L20) (`src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs:20`)
- [`SharpLink.Generator.RpcGenerator.DtoAnalysisState.EscapeIdentifier(string)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs#L125) (`src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs:125`)
- [`SharpLink.Generator.RpcGenerator.DtoAnalysisState.CollectReferencedContractRoots(System.Collections.Generic.Dictionary&lt;string, Microsoft.CodeAnalysis.ITypeSymbol&gt;)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.DtoGraphAnalysis.cs#L54) (`src/SharpLink.Generator/RpcGenerator.DtoGraphAnalysis.cs:54`)
- [`SharpLink.Generator.RpcGenerator.DtoAnalysisState.IsBuiltin(Microsoft.CodeAnalysis.ITypeSymbol)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.DtoGraphAnalysis.cs#L419) (`src/SharpLink.Generator/RpcGenerator.DtoGraphAnalysis.cs:419`)
- [`SharpLink.Generator.RpcGenerator.AppendDtoSuppressedSerializeBody(System.Text.StringBuilder, SharpLink.Generator.DtoCodecAnalysisModel, System.Collections.Generic.Dictionary&lt;string, int&gt;, string)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.DtoSerializeEmitter.cs#L96) (`src/SharpLink.Generator/RpcGenerator.DtoSerializeEmitter.cs:96`)
- [`SharpLink.Generator.RpcGenerator.AppendDtoExactSerializeBody(System.Text.StringBuilder, SharpLink.Generator.DtoCodecAnalysisModel, System.Collections.Generic.Dictionary&lt;string, int&gt;)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.DtoSizingEmitter.cs#L5) (`src/SharpLink.Generator/RpcGenerator.DtoSizingEmitter.cs:5`)
- [`SharpLink.Generator.RpcGenerator.GetReferencedInterfaceModels(Microsoft.CodeAnalysis.Compilation, System.Threading.CancellationToken)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.ReferenceAnalysis.cs#L5) (`src/SharpLink.Generator/RpcGenerator.ReferenceAnalysis.cs:5`)
- [`SharpLink.Generator.RpcGenerator.GetReferencedServiceModels(Microsoft.CodeAnalysis.Compilation, System.Threading.CancellationToken)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.ReferenceAnalysis.cs#L29) (`src/SharpLink.Generator/RpcGenerator.ReferenceAnalysis.cs:29`)

## SharpLink.Runtime (15)

- [`SharpLink.Runtime.RpcCodecProvider.TryGetExplicitCodec&lt;T&gt;(out SharpLink.Abstractions.IRpcCodec&lt;T&gt;)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/Codec/RpcCodecProvider.cs#L48) (`src/SharpLink.Runtime/Codec/RpcCodecProvider.cs:48`)
- [`SharpLink.Runtime.RpcGeneratedManifestRegistration.ValidateCodec(SharpLink.Abstractions.IRpcGeneratedCodecFactory, SharpLink.Abstractions.IRpcCodec)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/Codec/RpcCodecProvider.cs#L475) (`src/SharpLink.Runtime/Codec/RpcCodecProvider.cs:475`)
- [`SharpLink.Runtime.GeneratedManifestSnapshot.FromManifests(System.Collections.Generic.IReadOnlyList&lt;SharpLink.Abstractions.ISharpLinkGeneratedAssemblyManifest&gt;)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/GeneratedManifestSource.cs#L86) (`src/SharpLink.Runtime/GeneratedManifestSource.cs:86`)
- [`SharpLink.Runtime.RpcSession.SendClientStreamChunkAsync&lt;T&gt;(long, ushort, T, SharpLink.Abstractions.RpcDeadline, System.TimeProvider, System.Threading.CancellationToken)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSession.ClientStreamPublication.cs#L5) (`src/SharpLink.Runtime/RpcSession.ClientStreamPublication.cs:5`)
- [`SharpLink.Runtime.RpcSessionContractManifestExtensions.extension(SharpLink.Runtime.RpcSession).SendContractManifestAndFlushAsync(SharpLink.Runtime.ProtocolV2ContractManifest, System.Threading.CancellationToken)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSessionContractManifestExtensions.cs#L7) (`src/SharpLink.Runtime/RpcSessionContractManifestExtensions.cs:7`)
- [`SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendPingAsync()`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSessionExtensions.cs#L203) (`src/SharpLink.Runtime/RpcSessionExtensions.cs:203`)
- [`SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendPongAsync(long)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSessionExtensions.cs#L218) (`src/SharpLink.Runtime/RpcSessionExtensions.cs:218`)
- [`SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendHealthResponse(long, SharpLink.Abstractions.SharpLinkHealthStatus)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSessionExtensions.cs#L241) (`src/SharpLink.Runtime/RpcSessionExtensions.cs:241`)
- [`SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendStreamChunkAsync&lt;T&gt;(long, ushort, T, System.Threading.CancellationToken)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSessionExtensions.cs#L296) (`src/SharpLink.Runtime/RpcSessionExtensions.cs:296`)
- [`SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendStreamCompleteAsync(long, ushort)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSessionExtensions.cs#L360) (`src/SharpLink.Runtime/RpcSessionExtensions.cs:360`)
- [`SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendStreamErrorAsync(long, ushort, SharpLink.Abstractions.SharpLinkException)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSessionExtensions.cs#L394) (`src/SharpLink.Runtime/RpcSessionExtensions.cs:394`)
- [`SharpLink.Runtime.SharpLinkAssemblyManifestLoader.ValidateManifest(SharpLink.Abstractions.ISharpLinkGeneratedAssemblyManifest, System.Reflection.Assembly)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/SharpLinkDynamicModule.cs#L168) (`src/SharpLink.Runtime/SharpLinkDynamicModule.cs:168`)
- [`SharpLink.Runtime.SharpLinkDynamicModule.WaitForDrainAsync(System.Threading.Tasks.Task, System.TimeSpan)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/SharpLinkDynamicModule.cs#L500) (`src/SharpLink.Runtime/SharpLinkDynamicModule.cs:500`)
- [`SharpLink.Runtime.StreamManager.CompleteStream(long, bool, string?)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/StreamManager.cs#L257) (`src/SharpLink.Runtime/StreamManager.cs:257`)
- [`SharpLink.Runtime.StripedLongMap&lt;TValue&gt;.CopyEntries(System.Span&lt;System.Collections.Generic.KeyValuePair&lt;long, TValue&gt;&gt;)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/StripedLongMap.cs#L182) (`src/SharpLink.Runtime/StripedLongMap.cs:182`)

## SharpLink.Server (7)

- [`SharpLink.Server.AdmissionProgram.AcquireUse()`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/Admission/AdmissionProgram.cs#L101) (`src/SharpLink.Server/Admission/AdmissionProgram.cs:101`)
- [`SharpLink.Server.SharpLinkAdmissionController.TryReserveAdditionalQueuedBytes(int)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs#L584) (`src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs:584`)
- [`SharpLink.Server.SharpLinkAdmissionController.ReleaseAdditionalQueuedBytes(int)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs#L587) (`src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs:587`)
- [`SharpLink.Server.AdmissionRuleRuntime.CreateOwned(SharpLink.Server.SharpLinkAdmissionRuleOptions, string, System.TimeProvider)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs#L990) (`src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs:990`)
- [`SharpLink.Server.SharpLinkServer.TryReserveCall(SharpLink.Server.ServerConnectionState, SharpLink.Server.ServerRequestPermitTestHooks?, out SharpLink.Server.ServerRequestPermit?)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/SharpLinkServer.CallPermit.cs#L10) (`src/SharpLink.Server/SharpLinkServer.CallPermit.cs:10`)
- [`SharpLink.Server.SharpLinkServer.ForceStop()`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/SharpLinkServer.cs#L329) (`src/SharpLink.Server/SharpLinkServer.cs:329`)
- [`SharpLink.Server.SharpLinkServer.ObserveUserCall(System.Threading.Tasks.ValueTask, long)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/SharpLinkServer.RequestLoop.cs#L461) (`src/SharpLink.Server/SharpLinkServer.RequestLoop.cs:461`)

## One candidate intentionally not removed

- `src/SharpLink.Client/SharpLinkClient.Log.cs:37 lacks body` — analyzer classified it as a non-public zero-reference method, but the declaration has no standard body (source-generated logging/partial syntax), so the generic removal probe conservatively skipped it.

## False negatives discovered and protected during compilation/test probes

- `SharpLink.Shared/RpcBuiltinCollectionWireCatalog.TryGet`: compiled into linked projects and used by Generator; its removal causes compilation failure despite an earlier zero-reference index.
- `SharpLink.Server.TransportExtensions.ValidateTcpPort/ValidateBacklog`: C#14 extension-block methods actually invoked by transport builder; their removal causes compiler errors; source-location fallback now protects them.
- `SharpLinkServer.DisposeAllSessionsAsync` private alias: UnitTest uses reflection `GetMethod("DisposeAllSessionsAsync")`; removing it causes shutdown test failure and a misleading cleanup exception. The analyzer now records member-name string reflection roots. In 3.0, migrate the test to an explicit test seam before deleting this alias.
- `SharpLinkTelemetry` meter instruments and interface implementations cannot be deleted solely on zero direct references because initializers and virtual/interface dispatch are meaningful.

## Scope not yet verified

NativeAOT/trim/ALC dynamic module and multi-platform builds, benchmark comparisons, and all test/benchmark-only (Tier B) and public API (Tier C) removals. The exact Build/Test logs, full symbol graph candidate lists, and JSON proof are in this audit artifact directory.
