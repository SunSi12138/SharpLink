# Issue #801 — dev full-solution symbol reference index

Dev base: eb99fe887cf2129d9b88441245ca0a4a6406b6c2
Audit head: a8ef2daab1e9d5eeb64e6cf10574c325b994bea2
Projects 65; source docs 2215; generated docs 945
Production source files indexed: 476/476; declarations: 8853; matched references: 109848; elapsed seconds: 71
**Caution:** A/B/C are review candidates, NOT proof of dead code. This scans direct semantic references, not whole-program reachability, reflection, DI or external consumers.

## Tier counts
- A-NO-DIRECT-REFERENCE: 542
- B-ONLY-TEST-BENCH-EXAMPLE: 303
- C-PUBLIC-API-REVIEW: 397
- D-REFERENCED-PRODUCTION: 7611
## Unindexed production files
## Workspace diagnostics (first 30)
- MSBuildLocator unavailable; workspace might not load.

## A-NO-DIRECT-REFERENCE (first 120)
| Symbol | Source | Production | Generated | Tests | Benchmarks | Examples | Safety root |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| SharpLink.Abstractions.RpcDeadline.GetRemaining(long, long) | src/SharpLink.Abstractions/RpcDeadline.cs:135 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteUInt32(System.Buffers.IBufferWriter<byte>, uint) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:447 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkResourceExhaustion.CreateRemote(SharpLink.Abstractions.SharpLinkErrorCode, string) | src/SharpLink.Abstractions/SharpLinkResourceExhaustion.cs:39 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetry.ClientActiveEndpoints | src/SharpLink.Abstractions/SharpLinkTelemetry.cs:31 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetry.ClientReadyEndpoints | src/SharpLink.Abstractions/SharpLinkTelemetry.cs:36 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetry.ClientDrainingEndpoints | src/SharpLink.Abstractions/SharpLinkTelemetry.cs:41 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetry.ClientActiveConnections | src/SharpLink.Abstractions/SharpLinkTelemetry.cs:50 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetry.ClientRetiringConnections | src/SharpLink.Abstractions/SharpLinkTelemetry.cs:55 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTime.AddElapsedDuration(long, System.TimeSpan, long) | src/SharpLink.Abstractions/SharpLinkTime.cs:27 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTimer.DelayAsync(System.TimeSpan, System.Threading.CancellationToken) | src/SharpLink.Abstractions/SharpLinkTimer.cs:8 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTimer.WaitAsync(System.Threading.Tasks.Task, System.TimeSpan, System.Threading.CancellationToken) | src/SharpLink.Abstractions/SharpLinkTimer.cs:215 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTimer.WaitAsync(System.Threading.SemaphoreSlim, System.TimeSpan, System.Threading.CancellationToken) | src/SharpLink.Abstractions/SharpLinkTimer.cs:355 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.ClientAssemblyRegistry.TryGetProxyRegistration(System.Type, out SharpLink.Client.SharpLinkClient.ClientProxyRegistration) | src/SharpLink.Client/ClientAssemblyRegistry.cs:64 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.ClientCallLifetimeSourceExtensions | src/SharpLink.Client/ClientRequestTimeoutPolicy.cs:57 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.DynamicClusterTopologyState.SelectEndpoint(SharpLink.Client.SharpLinkClient.DynamicEndpointSelectionSnapshot, ulong) | src/SharpLink.Client/DynamicClusterTopologyState.cs:159 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.GeneratedClusterRouteSnapshot.Empty | src/SharpLink.Client/GeneratedClusterRouteSource.cs:83 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.GeneratedClusterRouteSnapshot.FromManifests(System.Collections.Generic.IReadOnlyList<SharpLink.Abstractions.ISharpLinkGeneratedClusterRouteManifest>) | src/SharpLink.Client/GeneratedClusterRouteSource.cs:93 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.PendingRequestTable.RentAsync<T>(SharpLink.Abstractions.IRpcCodec<T>, bool, SharpLink.Abstractions.RpcDeadline, System.Threading.CancellationToken) | src/SharpLink.Client/PendingRequestTable.cs:177 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.RpcRequestOperation<T>.Initialize(long, SharpLink.Abstractions.IRpcCodecProvider) | src/SharpLink.Client/RpcRequestOperation.cs:33 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.AttemptOutcomeState.ShouldHonorAdmissionRetryAfter | src/SharpLink.Client/SharpLinkClient.Attempts.cs:33 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.CreateAuthenticationRejectedException(string) | src/SharpLink.Client/SharpLinkClient.cs:273 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.LogClientDisconnected(Microsoft.Extensions.Logging.ILogger) | src/SharpLink.Client/SharpLinkClient.Log.cs:37 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.SendRpcCall(SharpLink.Runtime.RpcSession, long, long, long, SharpLink.Abstractions.ProtocolV2FrameFlags, System.Action<System.Buffers.IBufferWriter<byte>>?, SharpLink.Abstractions.RpcDeadline, SharpLink.Sdk.SharpLinkMetadata?) | src/SharpLink.Client/SharpLinkClient.RpcChannel.cs:5 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkMultiClusterClientBuilder.PrepareRuntimeCluster(SharpLink.Abstractions.SharpLinkClusterKey, SharpLink.Client.SharpClientBuilder, bool) | src/SharpLink.Client/SharpLinkMultiClusterClientBuilder.cs:234 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.EquatableArray<T>.implicit operator SharpLink.Generator.EquatableArray<T>(System.Collections.Immutable.ImmutableArray<T>) | src/SharpLink.Generator/EquatableArray.cs:26 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.EquatableArray<T>.implicit operator System.Collections.Immutable.ImmutableArray<T>(SharpLink.Generator.EquatableArray<T>) | src/SharpLink.Generator/EquatableArray.cs:27 | 0 | 0 | 0 | 0 | 0 | - |
| System.Runtime.CompilerServices.IsExternalInit | src/SharpLink.Generator/Polyfills.cs:10 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.DtoAnalysisState.GetAdapterTargetLogicalIdentity(SharpLink.Generator.GeneratedCodecModel) | src/SharpLink.Generator/RpcGenerator.AdapterClosedIdentity.cs:7 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.DtoAnalysisState.Report(SharpLink.Generator.DtoDiagnosticKind, Microsoft.CodeAnalysis.ISymbol, string, Microsoft.CodeAnalysis.Location?) | src/SharpLink.Generator/RpcGenerator.CodecPolicySupport.cs:69 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.DtoAnalysisState.IsNativeCodecType(Microsoft.CodeAnalysis.ITypeSymbol) | src/SharpLink.Generator/RpcGenerator.CodecRoutes.cs:297 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.ContractManifestDocument.GeneratorVersion | src/SharpLink.Generator/RpcGenerator.ContractManifest.Infrastructure.cs:251 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.HasSameCodecDefinition(SharpLink.Generator.GeneratedCodecModel, SharpLink.Generator.GeneratedCodecModel) | src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs:20 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.DtoAnalysisState.EscapeIdentifier(string) | src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs:125 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.DtoAnalysisState.CollectReferencedContractRoots(System.Collections.Generic.Dictionary<string, Microsoft.CodeAnalysis.ITypeSymbol>) | src/SharpLink.Generator/RpcGenerator.DtoGraphAnalysis.cs:54 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.DtoAnalysisState.IsBuiltin(Microsoft.CodeAnalysis.ITypeSymbol) | src/SharpLink.Generator/RpcGenerator.DtoGraphAnalysis.cs:419 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcHashValueExtensions | src/SharpLink.Generator/RpcGenerator.DtoModels.cs:226 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.AppendDtoSuppressedSerializeBody(System.Text.StringBuilder, SharpLink.Generator.DtoCodecAnalysisModel, System.Collections.Generic.Dictionary<string, int>, string) | src/SharpLink.Generator/RpcGenerator.DtoSerializeEmitter.cs:96 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.AppendDtoExactSerializeBody(System.Text.StringBuilder, SharpLink.Generator.DtoCodecAnalysisModel, System.Collections.Generic.Dictionary<string, int>) | src/SharpLink.Generator/RpcGenerator.DtoSizingEmitter.cs:5 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.GetReferencedInterfaceModels(Microsoft.CodeAnalysis.Compilation, System.Threading.CancellationToken) | src/SharpLink.Generator/RpcGenerator.ReferenceAnalysis.cs:5 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Generator.RpcGenerator.GetReferencedServiceModels(Microsoft.CodeAnalysis.Compilation, System.Threading.CancellationToken) | src/SharpLink.Generator/RpcGenerator.ReferenceAnalysis.cs:29 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcBufferWriterExtensions | src/SharpLink.Runtime/ArrayBufferWriterExtensions.cs:3 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcBufferWriterExtensions.extension(SharpLink.Abstractions.IRpcByteBufferWriter) | src/SharpLink.Runtime/ArrayBufferWriterExtensions.cs:6 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcCodecProvider.TryGetExplicitCodec<T>(out SharpLink.Abstractions.IRpcCodec<T>) | src/SharpLink.Runtime/Codec/RpcCodecProvider.cs:48 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcGeneratedManifestRegistration.HasContractCodecs | src/SharpLink.Runtime/Codec/RpcCodecProvider.cs:319 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcGeneratedManifestRegistration.ContractCodecProvider | src/SharpLink.Runtime/Codec/RpcCodecProvider.cs:321 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcGeneratedManifestRegistration.ValidateCodec(SharpLink.Abstractions.IRpcGeneratedCodecFactory, SharpLink.Abstractions.IRpcCodec) | src/SharpLink.Runtime/Codec/RpcCodecProvider.cs:475 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.GeneratedManifestSnapshot.Empty | src/SharpLink.Runtime/GeneratedManifestSource.cs:75 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.GeneratedManifestSnapshot.FromManifests(System.Collections.Generic.IReadOnlyList<SharpLink.Abstractions.ISharpLinkGeneratedAssemblyManifest>) | src/SharpLink.Runtime/GeneratedManifestSource.cs:86 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.GetResult(short) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:1201 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.GetStatus(short) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:1205 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.OnCompleted(System.Action<object?>, object?, short, System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:1209 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.ProtocolViolationLogTokens | src/SharpLink.Runtime/ProtocolViolationReason.cs:55 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSession.SendClientStreamChunkAsync<T>(long, ushort, T, SharpLink.Abstractions.RpcDeadline, System.TimeProvider, System.Threading.CancellationToken) | src/SharpLink.Runtime/RpcSession.ClientStreamPublication.cs:5 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSession.CompressionProfile | src/SharpLink.Runtime/RpcSession.Compression.cs:5 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSession.RemoteResponseCompressionAppliedGeneration | src/SharpLink.Runtime/RpcSession.ResponseCompressionPreference.cs:17 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSession.AppliedResponseCompressionPreference | src/SharpLink.Runtime/RpcSession.ResponseCompressionPreference.cs:26 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionContractManifestExtensions | src/SharpLink.Runtime/RpcSessionContractManifestExtensions.cs:3 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionContractManifestExtensions.extension(SharpLink.Runtime.RpcSession) | src/SharpLink.Runtime/RpcSessionContractManifestExtensions.cs:5 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionContractManifestExtensions.extension(SharpLink.Runtime.RpcSession).SendContractManifestAndFlushAsync(SharpLink.Runtime.ProtocolV2ContractManifest, System.Threading.CancellationToken) | src/SharpLink.Runtime/RpcSessionContractManifestExtensions.cs:7 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionExtensions | src/SharpLink.Runtime/RpcSessionExtensions.cs:4 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession) | src/SharpLink.Runtime/RpcSessionExtensions.cs:6 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendPingAsync() | src/SharpLink.Runtime/RpcSessionExtensions.cs:203 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendPongAsync(long) | src/SharpLink.Runtime/RpcSessionExtensions.cs:218 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendHealthResponse(long, SharpLink.Abstractions.SharpLinkHealthStatus) | src/SharpLink.Runtime/RpcSessionExtensions.cs:241 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendStreamChunkAsync<T>(long, ushort, T, System.Threading.CancellationToken) | src/SharpLink.Runtime/RpcSessionExtensions.cs:296 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendStreamCompleteAsync(long, ushort) | src/SharpLink.Runtime/RpcSessionExtensions.cs:360 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendStreamErrorAsync(long, ushort, SharpLink.Abstractions.SharpLinkException) | src/SharpLink.Runtime/RpcSessionExtensions.cs:394 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionResponseCompressionPreferenceExtensions | src/SharpLink.Runtime/RpcSessionExtensions.ResponseCompressionPreference.cs:3 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionResponseCompressionPreferenceExtensions.extension(SharpLink.Runtime.RpcSession) | src/SharpLink.Runtime/RpcSessionExtensions.ResponseCompressionPreference.cs:5 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionSessionRefreshExtensions | src/SharpLink.Runtime/RpcSessionExtensions.SessionRefresh.cs:3 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionSessionRefreshExtensions.extension(SharpLink.Runtime.RpcSession) | src/SharpLink.Runtime/RpcSessionExtensions.SessionRefresh.cs:5 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.SharpLinkAssemblyManifestLoader.ValidateManifest(SharpLink.Abstractions.ISharpLinkGeneratedAssemblyManifest, System.Reflection.Assembly) | src/SharpLink.Runtime/SharpLinkDynamicModule.cs:168 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.SharpLinkDynamicModule.WaitForDrainAsync(System.Threading.Tasks.Task, System.TimeSpan) | src/SharpLink.Runtime/SharpLinkDynamicModule.cs:500 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.SharpLinkRetirementHandle<T>.Completion | src/SharpLink.Runtime/SharpLinkGenerationRetirement.cs:38 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.StreamManager.CompleteStream(long, bool, string?) | src/SharpLink.Runtime/StreamManager.cs:257 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.StreamManager.RequestDispatchers.TryGetPreAdmission(ushort, out SharpLink.Runtime.PreAdmissionStreamDispatcher) | src/SharpLink.Runtime/StreamManager.cs:945 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.StreamManager.RequestDispatchers.TryGetDiscarding(ushort, out SharpLink.Runtime.DiscardingStreamDispatcher) | src/SharpLink.Runtime/StreamManager.cs:966 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.StripedLongMap<TValue>.CopyEntries(System.Span<System.Collections.Generic.KeyValuePair<long, TValue>>) | src/SharpLink.Runtime/StripedLongMap.cs:182 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.Flags | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:634 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.UserId | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:636 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.GroupId | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:637 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.Size | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:638 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.AccessTime | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:639 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.AccessTimeNanoseconds | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:640 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.ModificationTime | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:641 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.ModificationTimeNanoseconds | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:642 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.ChangeTime | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:643 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.ChangeTimeNanoseconds | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:644 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.BirthTime | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:645 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.BirthTimeNanoseconds | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:646 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.RawDevice | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:648 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.UserFlags | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:650 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Runtime.UnixSocketPathIdentity.UnixFileStatus.HardLinkCount | src/SharpLink.Runtime/Transport/SocketTransportV2.cs:651 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.TransitionBarrierExpiryForDiagnostics | src/SharpLink.Server/Admission/AdmissionLimiterState.cs:567 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.AdmissionProgram.AcquireUse() | src/SharpLink.Server/Admission/AdmissionProgram.cs:101 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.Window | src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs:47 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.AdmissionStateKernel.TimeProvider | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:39 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.AdmissionUpdatePlan.ResizeCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:1132 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.AdmissionUpdatePlan.RateTransitionCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:1134 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.AdmissionUpdatePlan.PartitionUpdateCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:1136 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.SharpLinkAdmissionController.MaxQueuedBytesForTests | src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs:365 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.SharpLinkAdmissionController.TryReserveAdditionalQueuedBytes(int) | src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs:584 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.SharpLinkAdmissionController.ReleaseAdditionalQueuedBytes(int) | src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs:587 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.AdmissionRuleRuntime.RateDefinition | src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs:980 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.AdmissionRuleRuntime.CreateOwned(SharpLink.Server.SharpLinkAdmissionRuleOptions, string, System.TimeProvider) | src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs:990 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.AdmissionPartitionPool.IdleTimeoutForTests | src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs:1423 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.ServerCallCapacityGovernor.Capacity | src/SharpLink.Server/ServerCallCapacityGovernor.cs:26 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.ServerCallCapacityGovernor.ServerCallReservation.IsReserved | src/SharpLink.Server/ServerCallCapacityGovernor.cs:161 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.ServerCallCapacityGovernor.ServerCallReservation.IsActive | src/SharpLink.Server/ServerCallCapacityGovernor.cs:163 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.ServerPreAdmissionStreamBytesPermit.RetainedBytes | src/SharpLink.Server/ServerResourceGovernor.cs:314 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.ServerRetainedCompressedPermit.RetainedCompressedBytes | src/SharpLink.Server/ServerResourceGovernor.cs:346 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.ServerDecodedBytesPermit.DecodedBytes | src/SharpLink.Server/ServerResourceGovernor.cs:390 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.ServerDecodePermit.DecodedBytesOwned | src/SharpLink.Server/ServerResourceGovernor.cs:432 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.ServerServiceModuleRegistry.DynamicModuleTable.Keys | src/SharpLink.Server/ServerServiceModuleRegistry.cs:75 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.ServerServiceModuleRegistry.UnregisterOperationTable.Count | src/SharpLink.Server/ServerServiceModuleRegistry.cs:112 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.ServerServiceModuleRegistry.DetachedModuleServiceTable.Count | src/SharpLink.Server/ServerServiceModuleRegistry.cs:144 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.SharpLinkServer.TryReserveCall(SharpLink.Server.ServerConnectionState, SharpLink.Server.ServerRequestPermitTestHooks?, out SharpLink.Server.ServerRequestPermit?) | src/SharpLink.Server/SharpLinkServer.CallPermit.cs:10 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.SharpLinkServer.FrameworkTaskSnapshotForDiagnostics | src/SharpLink.Server/SharpLinkServer.cs:298 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.SharpLinkServer.ForceStop() | src/SharpLink.Server/SharpLinkServer.cs:329 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Server.SharpLinkServer.DecodeQueueReservationsForDiagnostics | src/SharpLink.Server/SharpLinkServer.DecodeExecutor.cs:76 | 0 | 0 | 0 | 0 | 0 | - |

## B-ONLY-TEST-BENCH-EXAMPLE (first 120)
| Symbol | Source | Production | Generated | Tests | Benchmarks | Examples | Safety root |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| SharpLink.Abstractions.RpcDeadline.FromTimestamp(long) | src/SharpLink.Abstractions/RpcDeadline.cs:81 | 0 | 0 | 6 | 3 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetry.RecordSharedMemorySpillCopyBytes(long) | src/SharpLink.Abstractions/SharpLinkTelemetry.cs:351 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetry.RecordSharedMemoryStagingCopyBytes(long) | src/SharpLink.Abstractions/SharpLinkTelemetry.cs:355 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Client.ClientAssemblyRegistry.DynamicModules | src/SharpLink.Client/ClientAssemblyRegistry.cs:59 | 0 | 0 | 6 | 0 | 0 | - |
| SharpLink.Client.ClientConnection.SessionRefreshRedirect | src/SharpLink.Client/ClientConnection.cs:89 | 0 | 0 | 9 | 0 | 0 | - |
| SharpLink.Client.ClientConnection.AssertStateInvariant() | src/SharpLink.Client/ClientConnection.cs:261 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Client.ClientConnection.CancellationToken | src/SharpLink.Client/ClientConnection.cs:281 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Client.LateResponseLogLimiter.IntervalTimestampTicks | src/SharpLink.Client/ClientConnection.cs:681 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Client.FixedGeneratedClusterRouteSource.Empty | src/SharpLink.Client/GeneratedClusterRouteSource.cs:44 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Client.PendingRequestTable.SlotsMaterialized | src/SharpLink.Client/PendingRequestTable.cs:117 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Client.PendingRequestTable.Rent<T>(out long) | src/SharpLink.Client/PendingRequestTable.cs:135 | 0 | 0 | 53 | 23 | 0 | - |
| SharpLink.Client.PendingRequestTable.RentAsync<T>(bool, SharpLink.Abstractions.RpcDeadline, System.Threading.CancellationToken) | src/SharpLink.Client/PendingRequestTable.cs:166 | 0 | 0 | 9 | 3 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseBeforeReadyPublicationTestHook(System.Func<System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>) | src/SharpLink.Client/SharpClientBuilder.Compression.cs:31 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.FixedTransportFactory | src/SharpLink.Client/SharpClientBuilder.cs:54 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseGeneratedManifestSource(SharpLink.Runtime.IGeneratedManifestSource) | src/SharpLink.Client/SharpClientBuilder.cs:102 | 0 | 0 | 78 | 2 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseSerializer(System.Func<System.Type, SharpLink.Abstractions.IRpcCodec?>?) | src/SharpLink.Client/SharpClientBuilder.cs:120 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseReconnectJitterForTesting(SharpLink.Client.ISharpLinkReconnectJitter) | src/SharpLink.Client/SharpClientBuilder.cs:442 | 0 | 0 | 9 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.ResolveCallControl(SharpLink.Sdk.SharpLinkMetadata?, bool, bool, System.TimeSpan?) | src/SharpLink.Client/SharpLinkClient.CallOptions.cs:5 | 0 | 0 | 11 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.GetShutdownDependencyOrder(string[], string[][]) | src/SharpLink.Client/SharpLinkClient.cs:246 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.FrameworkTaskSnapshotForDiagnostics | src/SharpLink.Client/SharpLinkClient.cs:270 | 0 | 0 | 10 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.PendingCallCount | src/SharpLink.Client/SharpLinkClient.cs:353 | 0 | 0 | 12 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.ReadinessPublicationForTesting | src/SharpLink.Client/SharpLinkClient.Readiness.cs:46 | 0 | 0 | 25 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.CloseStopAdmissionForTesting() | src/SharpLink.Client/SharpLinkClient.Readiness.cs:49 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.ReadySignalForTesting | src/SharpLink.Client/SharpLinkClient.Readiness.cs:52 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.TransitionToForTesting(SharpLink.Abstractions.SharpLinkConnectionState) | src/SharpLink.Client/SharpLinkClient.Readiness.cs:55 | 0 | 0 | 10 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.RecordConnectionFailure(SharpLink.Client.SharpLinkConnectionFailureStage, System.Exception, string?) | src/SharpLink.Client/SharpLinkClient.SupportSnapshot.cs:63 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Client.SharpLinkMultiClusterClient.FrameworkTaskSnapshotForDiagnostics | src/SharpLink.Client/SharpLinkMultiClusterClient.cs:714 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Client.SharpLinkMultiClusterClientBuilder.UseGeneratedDiscoverySources(SharpLink.Runtime.IGeneratedManifestSource, SharpLink.Client.IGeneratedClusterRouteSource) | src/SharpLink.Client/SharpLinkMultiClusterClientBuilder.cs:53 | 0 | 0 | 10 | 0 | 0 | - |
| SharpLink.Client.SharpLinkMultiClusterClientExtensions.AddClusterAsync(SharpLink.Abstractions.ISharpLinkMultiClusterClient, SharpLink.Abstractions.SharpLinkClusterKey, System.Action<SharpLink.Client.SharpClientBuilder>, System.Action<SharpLink.Client.SharpLinkMultiClusterSlotOptions>?, System.Threading.CancellationToken, SharpLink.Runtime.IGeneratedManifestSource, SharpLink.Client.IGeneratedClusterRouteSource) | src/SharpLink.Client/SharpLinkMultiClusterClientExtensions.cs:32 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClient.StaticClusterTopologyState.SelectEndpoint(SharpLink.Client.SharpLinkClient.StaticEndpointSelectionSnapshot, ulong) | src/SharpLink.Client/StaticClusterTopologyState.cs:109 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Runtime.RpcUnsafeBlitPlatform.IsSupported(System.Type, int) | src/SharpLink.Runtime/Codec/RpcUnsafeBlitPlatform.cs:55 | 0 | 0 | 7 | 0 | 0 | - |
| SharpLink.Runtime.RpcWirePlatform | src/SharpLink.Runtime/Codec/RpcWirePlatform.cs:3 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Runtime.RpcWirePlatform.IsSupported(bool) | src/SharpLink.Runtime/Codec/RpcWirePlatform.cs:11 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Runtime.SharpLinkCompressionOptions.FindProviderBinding(string) | src/SharpLink.Runtime/Compression/SharpLinkCompression.cs:118 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.Rent(System.Threading.CancellationToken, SharpLink.Abstractions.IRpcCodecProvider) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:127 | 0 | 0 | 8 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.Rent(System.Threading.CancellationToken, SharpLink.Abstractions.IRpcCodec<T>) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:150 | 0 | 0 | 65 | 9 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.SetBeforeConcurrentDisposeCompletionInstallForTests(System.Action?) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:415 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.SetBeforeRemoteTerminalPublicationPublishForTests(System.Action?) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:420 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.SetBeforeRemoteTerminalPublicationCompletionInstallForTests(System.Action?) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:423 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.SetAfterRemoteTerminalPublicationCompletionInstallForTests(System.Action?) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:426 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.SetBeforeProducerOperationAcquireForTests(System.Action?) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:429 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.SetBeforeConsumerWaitOwnerAcquireForTests(System.Action?) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:432 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.SetAfterConsumerWaitResultForTests(System.Action?) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:435 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.SetBeforeReturnTransitionForTests(System.Action?) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:438 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.SetAfterReturnTransitionForTests(System.Action?) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:441 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.SetConsumerAbandonedCallback(System.Action<long>?, long) | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:671 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.RetainedCountForTests | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:1543 | 0 | 0 | 50 | 2 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.BufferCapacityForTests | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:1545 | 0 | 0 | 2 | 3 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.HasRetainedReferencesForTests | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:1547 | 0 | 0 | 12 | 0 | 0 | - |
| SharpLink.Runtime.PooledAsyncStreamDispatcher<T>.ClearPoolForTests() | src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:1603 | 0 | 0 | 96 | 5 | 0 | - |
| SharpLink.Runtime.PreAdmissionStreamDispatcher.BufferedItemObserverForTests | src/SharpLink.Runtime/PreAdmissionStreamDispatcher.cs:53 | 0 | 0 | 6 | 0 | 0 | - |
| SharpLink.Runtime.PreAdmissionStreamDispatcher.RetainedBytesForTests | src/SharpLink.Runtime/PreAdmissionStreamDispatcher.cs:59 | 0 | 0 | 10 | 0 | 0 | - |
| SharpLink.Runtime.ProtocolV2FrameWriter | src/SharpLink.Runtime/ProtocolV2/ProtocolV2FrameCodec.cs:373 | 0 | 0 | 59 | 18 | 0 | - |
| SharpLink.Runtime.ProtocolV2FrameWriter.WriteEmptyFrame(SharpLink.Abstractions.IRpcByteBufferWriter, SharpLink.Abstractions.ProtocolV2FrameType, SharpLink.Abstractions.ProtocolV2FrameFlags, ulong) | src/SharpLink.Runtime/ProtocolV2/ProtocolV2FrameCodec.cs:406 | 0 | 0 | 4 | 0 | 0 | - |
| SharpLink.Runtime.ProtocolV2Negotiator.CreateImplementedPolicy(int, int, int, System.Collections.Generic.IReadOnlyList<SharpLink.Runtime.SharpLinkCompressionProviderBinding>) | src/SharpLink.Runtime/ProtocolV2/ProtocolV2Negotiator.cs:134 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Runtime.RpcSession.LastActive | src/SharpLink.Runtime/RpcSession.cs:25 | 0 | 0 | 21 | 2 | 0 | - |
| SharpLink.Runtime.RpcSession.HasPendingSendPumpIdleWait | src/SharpLink.Runtime/RpcSession.cs:436 | 0 | 0 | 0 | 1 | 0 | - |
| SharpLink.Runtime.RpcSession.IsDraining | src/SharpLink.Runtime/RpcSession.cs:438 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.RpcSession.AssertStateInvariant() | src/SharpLink.Runtime/RpcSession.cs:447 | 0 | 0 | 8 | 0 | 0 | - |
| SharpLink.Runtime.RpcSession.SendStreamChunkAsync<T>(long, ushort, T, System.Threading.CancellationToken) | src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs:12 | 0 | 0 | 22 | 7 | 0 | - |
| SharpLink.Runtime.RpcSession.PreCreditSerializedBytes | src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs:62 | 0 | 0 | 19 | 1 | 0 | - |
| SharpLink.Runtime.RpcSession.PreCreditSerializedByteLimit | src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs:65 | 0 | 0 | 8 | 0 | 0 | - |
| SharpLink.Runtime.RpcSession.PreCreditSerializedWaiterCount | src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs:68 | 0 | 0 | 19 | 1 | 0 | - |
| SharpLink.Runtime.RpcSession.PreCreditSerializationPermitLimit | src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs:73 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Runtime.RpcSession.PreCreditActiveSerializerCount | src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs:75 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.RpcSession.SendStreamCompleteAsync(long, ushort) | src/SharpLink.Runtime/RpcSession.PreCreditTerminal.cs:9 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.RpcSessionExtensions.extension(SharpLink.Runtime.RpcSession).SendRpcErrorAsync(long, SharpLink.Abstractions.SharpLinkException) | src/SharpLink.Runtime/RpcSessionExtensions.cs:116 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Runtime.SharpLinkDynamicModule.AssertAccountingInvariant() | src/SharpLink.Runtime/SharpLinkDynamicModule.cs:457 | 0 | 0 | 7 | 0 | 0 | - |
| SharpLink.Runtime.SharpLinkRetirementHandle.Completion | src/SharpLink.Runtime/SharpLinkGenerationRetirement.cs:14 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.SharpLinkRetirementHandle.WaitAsync(System.Threading.CancellationToken) | src/SharpLink.Runtime/SharpLinkGenerationRetirement.cs:16 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.SharpLinkRuntimeContextBuilder.Build(bool) | src/SharpLink.Runtime/SharpLinkRuntimeContext.cs:396 | 0 | 0 | 185 | 23 | 0 | - |
| SharpLink.Runtime.SharpLinkRuntimeContextBuilder.Build(System.Collections.Generic.IReadOnlyList<SharpLink.Abstractions.ISharpLinkGeneratedAssemblyManifest>) | src/SharpLink.Runtime/SharpLinkRuntimeContext.cs:401 | 0 | 0 | 28 | 2 | 0 | - |
| SharpLink.Runtime.StreamFlowController.SendConnectionCredit | src/SharpLink.Runtime/StreamFlowController.cs:537 | 0 | 0 | 24 | 0 | 0 | - |
| SharpLink.Runtime.StreamFlowController.ActiveSendStreamCount | src/SharpLink.Runtime/StreamFlowController.cs:546 | 0 | 0 | 4 | 0 | 0 | - |
| SharpLink.Runtime.StreamFlowController.RetainedSendStreamCount | src/SharpLink.Runtime/StreamFlowController.cs:555 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Runtime.StreamManager.Register(long, SharpLink.Runtime.IStreamDispatcher) | src/SharpLink.Runtime/StreamManager.cs:64 | 0 | 0 | 31 | 3 | 0 | - |
| SharpLink.Runtime.StreamManager.Unregister(long) | src/SharpLink.Runtime/StreamManager.cs:143 | 0 | 0 | 16 | 0 | 0 | - |
| SharpLink.Runtime.StreamManager.DispatchChunkAsync(long, System.Buffers.ReadOnlySequence<byte>) | src/SharpLink.Runtime/StreamManager.cs:181 | 0 | 0 | 14 | 0 | 0 | - |
| SharpLink.Runtime.StreamManager.CompleteStream(long, ushort, bool, string?) | src/SharpLink.Runtime/StreamManager.cs:263 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.StreamManager.CompleteAll(bool, string?) | src/SharpLink.Runtime/StreamManager.cs:269 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.StreamManager.CompleteStream(long, System.Exception?) | src/SharpLink.Runtime/StreamManager.cs:275 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Runtime.StreamManager.DroppedStreamFrames | src/SharpLink.Runtime/StreamManager.cs:711 | 0 | 0 | 8 | 0 | 0 | - |
| SharpLink.Runtime.StreamManager.HasMaterializedRoutingState | src/SharpLink.Runtime/StreamManager.cs:714 | 0 | 0 | 14 | 1 | 0 | - |
| SharpLink.Runtime.StreamManager.AssertAccountingInvariant() | src/SharpLink.Runtime/StreamManager.cs:721 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Runtime.SynchronousBuildTransaction.OwnRange<T>(System.Collections.Generic.IEnumerable<T>, System.Action<T>?, SharpLink.Runtime.SynchronousBuildResourceMetadata) | src/SharpLink.Runtime/SynchronousBuildTransaction.cs:69 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.SynchronousBuildTransaction.Transfer() | src/SharpLink.Runtime/SynchronousBuildTransaction.cs:90 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Runtime.NamedPipeClientTransportFactory.EffectivePipeOptions | src/SharpLink.Runtime/Transport/NamedPipeTransportV2.cs:31 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Runtime.NamedPipeServerTransportListener.EffectivePipeOptions | src/SharpLink.Runtime/Transport/NamedPipeTransportV2.cs:107 | 0 | 0 | 4 | 0 | 0 | - |
| SharpLink.Runtime.SharedMemoryMapping.ActiveMappingCount | src/SharpLink.Runtime/Transport/SharedMemoryMapping.cs:41 | 0 | 0 | 9 | 0 | 0 | - |
| SharpLink.Runtime.SharedMemoryPipeReader.HasPendingDataWait | src/SharpLink.Runtime/Transport/SharedMemoryPipelines.cs:24 | 0 | 0 | 0 | 1 | 0 | - |
| SharpLink.Runtime.ReadOwnershipPipeReader.CompletionRequested | src/SharpLink.Runtime/Transport/TransportConnection.cs:112 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Serializer.SharpPack.SharpPackRpcCodec<T>.Context | src/SharpLink.Serializer.SharpPack/SharpPackRpcCodec.cs:146 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Server.ResizableConcurrencyState.ActiveCount | src/SharpLink.Server/Admission/AdmissionLimiterState.cs:42 | 0 | 0 | 31 | 0 | 0 | - |
| SharpLink.Server.ResizableConcurrencyState.WaitingCount | src/SharpLink.Server/Admission/AdmissionLimiterState.cs:51 | 0 | 0 | 17 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.WaitingCount | src/SharpLink.Server/Admission/AdmissionLimiterState.cs:563 | 0 | 0 | 13 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.TransitionDebtForDiagnostics | src/SharpLink.Server/Admission/AdmissionLimiterState.cs:565 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Server.AdmissionProgram.BeforeProgramAttachForTests | src/SharpLink.Server/Admission/AdmissionProgram.cs:51 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Server.AdmissionProgram.IsReclaimed | src/SharpLink.Server/Admission/AdmissionProgram.cs:73 | 0 | 0 | 54 | 0 | 0 | - |
| SharpLink.Server.AdmissionProgram.ReclaimCount | src/SharpLink.Server/Admission/AdmissionProgram.cs:75 | 0 | 0 | 20 | 0 | 0 | - |
| SharpLink.Server.AdmissionProgram.DuplicateReleaseAttempts | src/SharpLink.Server/Admission/AdmissionProgram.cs:77 | 0 | 0 | 15 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.FixedWindowForTests | src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs:45 | 0 | 0 | 38 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.PermitLimit | src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs:46 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.ActivationModeForTests | src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs:48 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.ConsumedForTests | src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs:49 | 0 | 0 | 17 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.ActiveLimitForTests | src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs:50 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.QueuedLimitForTests | src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs:51 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.ActiveWindowForTests | src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs:52 | 0 | 0 | 4 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.HasPendingWindowForTests | src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs:53 | 0 | 0 | 4 | 0 | 0 | - |
| SharpLink.Server.AdmissionRateState.CounterIdentityForTests | src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs:54 | 0 | 0 | 9 | 0 | 0 | - |
| SharpLink.Server.AdmissionStateKernel.RetiredProgramCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:87 | 0 | 0 | 30 | 0 | 0 | - |
| SharpLink.Server.AdmissionStateKernel.LiveProgramCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:96 | 0 | 0 | 33 | 0 | 0 | - |
| SharpLink.Server.AdmissionStateKernel.RuleStateCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:105 | 0 | 0 | 29 | 0 | 0 | - |
| SharpLink.Server.AdmissionStateKernel.ConcurrencyStateCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:128 | 0 | 0 | 11 | 0 | 0 | - |
| SharpLink.Server.AdmissionStateKernel.RateStateCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:137 | 0 | 0 | 18 | 0 | 0 | - |
| SharpLink.Server.AdmissionStateKernel.PartitionStateCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:146 | 0 | 0 | 25 | 0 | 0 | - |
| SharpLink.Server.AdmissionStateKernel.PartitionEntryCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:155 | 0 | 0 | 4 | 0 | 0 | - |
| SharpLink.Server.AdmissionStateKernel.PartitionRuntimeGenerationCount | src/SharpLink.Server/Admission/AdmissionStateKernel.cs:164 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Server.ServerConnectionAdmission.MaxConnections | src/SharpLink.Server/Admission/ServerConnectionAdmission.cs:37 | 0 | 0 | 7 | 0 | 0 | - |
| SharpLink.Server.ServerConnectionAdmission.MaxHandshakes | src/SharpLink.Server/Admission/ServerConnectionAdmission.cs:39 | 0 | 0 | 8 | 0 | 0 | - |
| SharpLink.Server.ServerConnectionAdmission.ActiveConnections | src/SharpLink.Server/Admission/ServerConnectionAdmission.cs:41 | 0 | 0 | 47 | 0 | 0 | - |

## C-PUBLIC-API-REVIEW (first 120)
| Symbol | Source | Production | Generated | Tests | Benchmarks | Examples | Safety root |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| SharpLink.Abstractions.RpcMethodDescriptor.Deconstruct(out long, out long, out SharpLink.Abstractions.RpcMethodKind, out bool, out bool, out bool, out System.TimeSpan?, out bool, out int, out bool) | src/SharpLink.Abstractions/IRpcChannel.cs:178 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Abstractions.RpcNoClientStreams | src/SharpLink.Abstractions/IRpcChannel.cs:257 | 0 | 0 | 33 | 1 | 1 | - |
| SharpLink.Abstractions.RpcGeneratedCodecSizing | src/SharpLink.Abstractions/IRpcSizedCodec.cs:46 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecSizing.IsSuppressed | src/SharpLink.Abstractions/IRpcSizedCodec.cs:53 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecSizing.Enter() | src/SharpLink.Abstractions/IRpcSizedCodec.cs:56 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecSizing.Exit() | src/SharpLink.Abstractions/IRpcSizedCodec.cs:59 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.LogEvents.Stream | src/SharpLink.Abstractions/LogEvents.cs:61 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.LogEvents.Stream.ChunkReceived | src/SharpLink.Abstractions/LogEvents.cs:64 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.LogEvents.Stream.StreamClosed | src/SharpLink.Abstractions/LogEvents.cs:66 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.LogEvents.Server.HeartbeatLoopUnhandledException | src/SharpLink.Abstractions/LogEvents.cs:82 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteFieldKey(System.Buffers.IBufferWriter<byte>, uint, SharpLink.Abstractions.RpcGeneratedWireType) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:39 | 0 | 0 | 418 | 392 | 16 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteObjectEnd(System.Buffers.IBufferWriter<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:50 | 0 | 0 | 33 | 20 | 8 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.TryReadField(ref System.Buffers.SequenceReader<byte>, out uint, out SharpLink.Abstractions.RpcGeneratedWireType) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:53 | 0 | 0 | 20 | 10 | 4 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteBoolean(System.Buffers.IBufferWriter<byte>, bool) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:111 | 0 | 0 | 2 | 2 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.ReadBoolean(ref System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:121 | 0 | 0 | 1 | 1 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteRune(System.Buffers.IBufferWriter<byte>, System.Text.Rune) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:130 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.ReadRune(ref System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:134 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteDecimal(System.Buffers.IBufferWriter<byte>, decimal) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:144 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.ReadDecimal(ref System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:148 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteDateOnly(System.Buffers.IBufferWriter<byte>, System.DateOnly) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:164 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.ReadDateOnly(ref System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:168 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteDateTime(System.Buffers.IBufferWriter<byte>, System.DateTime) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:178 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.ReadDateTime(ref System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:182 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteTimeOnly(System.Buffers.IBufferWriter<byte>, System.TimeOnly) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:192 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.ReadTimeOnly(ref System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:196 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteDateTimeOffset(System.Buffers.IBufferWriter<byte>, System.DateTimeOffset) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:206 | 0 | 0 | 4 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.ReadDateTimeOffset(ref System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:219 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.GetFixedWireType(int) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:261 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.EnsureWireType(SharpLink.Abstractions.RpcGeneratedWireType, SharpLink.Abstractions.RpcGeneratedWireType) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:272 | 0 | 0 | 114 | 103 | 4 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WritePresence(System.Buffers.IBufferWriter<byte>, bool) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:279 | 0 | 0 | 64 | 40 | 16 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.ReadPresence(ref System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:287 | 0 | 0 | 17 | 10 | 4 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteString(System.Buffers.IBufferWriter<byte>, string) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:295 | 0 | 0 | 2 | 2 | 0 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.ReadString(ref System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:311 | 0 | 0 | 98 | 92 | 4 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.BeginLength(SharpLink.Abstractions.IRpcByteBufferWriter) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:330 | 0 | 0 | 19 | 7 | 1 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.EndLength(SharpLink.Abstractions.IRpcByteBufferWriter, SharpLink.Abstractions.RpcGeneratedLengthToken) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:341 | 0 | 0 | 19 | 7 | 1 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.SkipField(ref System.Buffers.SequenceReader<byte>, SharpLink.Abstractions.RpcGeneratedWireType) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:364 | 0 | 0 | 19 | 10 | 4 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.WriteCollectionCount(System.Buffers.IBufferWriter<byte>, int, bool) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:388 | 0 | 0 | 11 | 2 | 2 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.ReadCollectionCount(ref System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:403 | 0 | 0 | 5 | 1 | 1 | - |
| SharpLink.Abstractions.RpcGeneratedCodecWire.EnsureFullyConsumed(in System.Buffers.SequenceReader<byte>) | src/SharpLink.Abstractions/RpcGeneratedCodecWire.cs:415 | 0 | 0 | 44 | 22 | 10 | - |
| SharpLink.Abstractions.RpcHash128.operator ==(SharpLink.Abstractions.RpcHash128, SharpLink.Abstractions.RpcHash128) | src/SharpLink.Abstractions/RpcHash128.cs:40 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.RpcHash128.operator !=(SharpLink.Abstractions.RpcHash128, SharpLink.Abstractions.RpcHash128) | src/SharpLink.Abstractions/RpcHash128.cs:43 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.RpcInvocationExtensions | src/SharpLink.Abstractions/RpcInvocationExtensions.cs:4 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.RpcInvocationExtensions.AsVoid<T>(System.Threading.Tasks.ValueTask<T>) | src/SharpLink.Abstractions/RpcInvocationExtensions.cs:7 | 0 | 0 | 29 | 4 | 1 | - |
| SharpLink.Sdk.RpcCodecAttribute | src/SharpLink.Abstractions/Sdk/RpcCodecAttribute.cs:4 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Sdk.RpcCodecSemanticIdentityAttribute | src/SharpLink.Abstractions/Sdk/RpcCodecSemanticIdentityAttribute.cs:10 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Sdk.RpcServiceAttribute.Lifetime | src/SharpLink.Abstractions/Sdk/RpcServiceAttribute.cs:12 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Sdk.SharpLinkRpcContractsAttribute.ContractTypes | src/SharpLink.Abstractions/Sdk/SharpLinkRpcContractsAttribute.cs:9 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkAuthenticationContext.GetClaim(string) | src/SharpLink.Abstractions/SharpLinkAuthenticationContext.cs:73 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkAuthenticationResult.Authenticate(SharpLink.Abstractions.SharpLinkAuthenticationContext) | src/SharpLink.Abstractions/SharpLinkAuthenticationResult.cs:20 | 0 | 0 | 7 | 0 | 1 | - |
| SharpLink.Abstractions.SharpLinkAuthenticator | src/SharpLink.Abstractions/SharpLinkAuthenticator.cs:36 | 0 | 0 | 23 | 0 | 2 | - |
| SharpLink.Abstractions.SharpLinkAuthenticator.CreateClient(System.Func<System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.ReadOnlyMemory<byte>>>) | src/SharpLink.Abstractions/SharpLinkAuthenticator.cs:39 | 0 | 0 | 6 | 0 | 1 | - |
| SharpLink.Abstractions.SharpLinkAuthenticator.CreateServer(System.Func<SharpLink.Abstractions.SharpLinkAuthenticationRequest, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<SharpLink.Abstractions.SharpLinkAuthenticationResult>>) | src/SharpLink.Abstractions/SharpLinkAuthenticator.cs:47 | 0 | 0 | 17 | 0 | 1 | - |
| SharpLink.Abstractions.SharpLinkAuthorization | src/SharpLink.Abstractions/SharpLinkAuthorization.cs:4 | 0 | 0 | 6 | 0 | 3 | - |
| SharpLink.Abstractions.SharpLinkAuthorization.RequireActiveToken(System.DateTimeOffset?, string?) | src/SharpLink.Abstractions/SharpLinkAuthorization.cs:26 | 0 | 0 | 2 | 0 | 1 | - |
| SharpLink.Abstractions.SharpLinkAuthorization.RequireScope(string, string?) | src/SharpLink.Abstractions/SharpLinkAuthorization.cs:42 | 0 | 0 | 2 | 0 | 1 | - |
| SharpLink.Abstractions.SharpLinkAuthorization.RequireTenant(string, string?) | src/SharpLink.Abstractions/SharpLinkAuthorization.cs:59 | 0 | 0 | 2 | 0 | 1 | - |
| SharpLink.Abstractions.SharpLinkClusterKey.implicit operator SharpLink.Abstractions.SharpLinkClusterKey(string) | src/SharpLink.Abstractions/SharpLinkClusterKey.cs:43 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkEndpointSelectionContext.Count | src/SharpLink.Abstractions/SharpLinkEndpoints.cs:174 | 0 | 0 | 11 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkEndpointSelectionContext.this[int] | src/SharpLink.Abstractions/SharpLinkEndpoints.cs:182 | 0 | 0 | 4 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkEndpointCandidate.ReadyConnectionCount | src/SharpLink.Abstractions/SharpLinkEndpoints.cs:235 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkEndpointCandidate.ActiveCallCount | src/SharpLink.Abstractions/SharpLinkEndpoints.cs:238 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkGeneratedAssemblyCatalog.Register(SharpLink.Abstractions.ISharpLinkGeneratedAssemblyManifest) | src/SharpLink.Abstractions/SharpLinkGeneratedAssemblyManifest.cs:183 | 0 | 0 | 24 | 1 | 22 | - |
| SharpLink.Abstractions.SharpLinkGeneratedClusterRouteCatalog.Register(SharpLink.Abstractions.ISharpLinkGeneratedClusterRouteManifest) | src/SharpLink.Abstractions/SharpLinkGeneratedClusterRouteManifest.cs:33 | 0 | 0 | 7 | 0 | 3 | - |
| SharpLink.Abstractions.SharpLinkGeneratedCodecIdentityAttribute | src/SharpLink.Abstractions/SharpLinkGeneratedCodecIdentityAttribute.cs:7 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkGeneratedUnsafeBlitCatalog.Register(System.Type, int, bool) | src/SharpLink.Abstractions/SharpLinkGeneratedUnsafeBlitCatalog.cs:18 | 0 | 0 | 4 | 0 | 2 | - |
| SharpLink.Abstractions.SharpLinkServerInvocationContext.ConnectionId | src/SharpLink.Abstractions/SharpLinkInterceptors.cs:128 | 0 | 0 | 1 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetryDetailPolicySnapshot.Detailed | src/SharpLink.Abstractions/SharpLinkTelemetryDetailPolicy.cs:19 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetryDetailExtensions.GetTelemetryDetailPolicySnapshot(SharpLink.Abstractions.ISharpLinkClient) | src/SharpLink.Abstractions/SharpLinkTelemetryDetailPolicy.cs:32 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetryDetailExtensions.UpdateTelemetryDetailPolicy(SharpLink.Abstractions.ISharpLinkClient, SharpLink.Abstractions.SharpLinkTelemetryDetailMode) | src/SharpLink.Abstractions/SharpLinkTelemetryDetailPolicy.cs:41 | 0 | 0 | 6 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetryDetailExtensions.GetTelemetryDetailPolicySnapshot(SharpLink.Abstractions.ISharpLinkServer) | src/SharpLink.Abstractions/SharpLinkTelemetryDetailPolicy.cs:51 | 0 | 0 | 4 | 0 | 0 | - |
| SharpLink.Abstractions.SharpLinkTelemetryDetailExtensions.UpdateTelemetryDetailPolicy(SharpLink.Abstractions.ISharpLinkServer, SharpLink.Abstractions.SharpLinkTelemetryDetailMode) | src/SharpLink.Abstractions/SharpLinkTelemetryDetailPolicy.cs:60 | 0 | 0 | 3 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseRuntime(System.Action<SharpLink.Runtime.SharpLinkRuntimeOptions>) | src/SharpLink.Client/SharpClientBuilder.Compression.cs:9 | 0 | 0 | 68 | 3 | 1 | - |
| SharpLink.Client.SharpClientBuilder.UseRequestCompressionPolicy(SharpLink.Abstractions.SharpLinkCompressionSendPolicy) | src/SharpLink.Client/SharpClientBuilder.Compression.cs:20 | 0 | 0 | 15 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseAuthenticator(SharpLink.Abstractions.ISharpLinkClientAuthenticator) | src/SharpLink.Client/SharpClientBuilder.cs:66 | 0 | 0 | 27 | 0 | 1 | - |
| SharpLink.Client.SharpClientBuilder.AddInterceptor(SharpLink.Abstractions.ISharpLinkClientInterceptor) | src/SharpLink.Client/SharpClientBuilder.cs:77 | 0 | 0 | 16 | 9 | 1 | - |
| SharpLink.Client.SharpClientBuilder.UseTimeProvider(System.TimeProvider) | src/SharpLink.Client/SharpClientBuilder.cs:88 | 0 | 0 | 111 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseProtocol(System.Action<SharpLink.Runtime.SharpLinkProtocolOptions>) | src/SharpLink.Client/SharpClientBuilder.cs:109 | 0 | 0 | 35 | 7 | 2 | - |
| SharpLink.Client.SharpClientBuilder.UseLoggerFactory(Microsoft.Extensions.Logging.ILoggerFactory) | src/SharpLink.Client/SharpClientBuilder.cs:127 | 0 | 0 | 12 | 0 | 3 | - |
| SharpLink.Client.SharpClientBuilder.UseBufferWriterPool(System.Action<SharpLink.Runtime.BufferWriterPoolOptions>) | src/SharpLink.Client/SharpClientBuilder.cs:138 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseStateStoreConcurrency(System.Action<SharpLink.Runtime.RuntimeConcurrencyOptions>) | src/SharpLink.Client/SharpClientBuilder.cs:149 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseHeartbeat(System.TimeSpan, System.TimeSpan) | src/SharpLink.Client/SharpClientBuilder.cs:170 | 0 | 0 | 158 | 11 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseHeartbeatInterval(System.TimeSpan) | src/SharpLink.Client/SharpClientBuilder.cs:185 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseHeartbeatTimeout(System.TimeSpan) | src/SharpLink.Client/SharpClientBuilder.cs:198 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseRequestTimeout() | src/SharpLink.Client/SharpClientBuilder.cs:211 | 0 | 0 | 9 | 0 | 9 | - |
| SharpLink.Client.SharpClientBuilder.UseRequestTimeout(System.TimeSpan) | src/SharpLink.Client/SharpClientBuilder.cs:218 | 0 | 0 | 58 | 0 | 5 | - |
| SharpLink.Client.SharpClientBuilder.DisableRequestTimeout() | src/SharpLink.Client/SharpClientBuilder.cs:225 | 0 | 0 | 310 | 9 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseRpcSessionFlush(int, System.TimeSpan) | src/SharpLink.Client/SharpClientBuilder.cs:244 | 0 | 0 | 4 | 2 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseConnectionPool(System.Action<SharpLink.Client.SharpLinkConnectionPoolOptions>) | src/SharpLink.Client/SharpClientBuilder.cs:251 | 0 | 0 | 39 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseEndpoint(SharpLink.Abstractions.SharpLinkEndpoint, SharpLink.Abstractions.SharpLinkEndpointTransportFactory) | src/SharpLink.Client/SharpClientBuilder.cs:263 | 0 | 0 | 20 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseEndpoints(System.Collections.Generic.IEnumerable<SharpLink.Abstractions.SharpLinkEndpoint>, SharpLink.Abstractions.SharpLinkEndpointTransportFactory) | src/SharpLink.Client/SharpClientBuilder.cs:277 | 0 | 0 | 139 | 4 | 2 | - |
| SharpLink.Client.SharpClientBuilder.UseEndpointResolver(SharpLink.Abstractions.ISharpLinkEndpointResolver, SharpLink.Abstractions.SharpLinkEndpointTransportFactory) | src/SharpLink.Client/SharpClientBuilder.cs:291 | 0 | 0 | 91 | 2 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseDnsEndpoints(string, int, SharpLink.Abstractions.SharpLinkEndpointTransportFactory, System.Action<SharpLink.Client.SharpLinkDnsResolverOptions>?) | src/SharpLink.Client/SharpClientBuilder.cs:305 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseCluster(System.Action<SharpLink.Client.SharpLinkClusterOptions>) | src/SharpLink.Client/SharpClientBuilder.cs:329 | 0 | 0 | 120 | 6 | 2 | - |
| SharpLink.Client.SharpClientBuilder.UseLoadBalancing(SharpLink.Client.SharpLinkLoadBalancingStrategy) | src/SharpLink.Client/SharpClientBuilder.cs:341 | 0 | 0 | 14 | 2 | 2 | - |
| SharpLink.Client.SharpClientBuilder.UseEndpointSelector(SharpLink.Abstractions.ISharpLinkEndpointSelector) | src/SharpLink.Client/SharpClientBuilder.cs:356 | 0 | 0 | 37 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseRetry() | src/SharpLink.Client/SharpClientBuilder.cs:369 | 0 | 0 | 0 | 2 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseRetry(System.Action<SharpLink.Client.SharpLinkRetryOptions>) | src/SharpLink.Client/SharpClientBuilder.cs:380 | 0 | 0 | 5 | 0 | 2 | - |
| SharpLink.Client.SharpClientBuilder.UseRetry(SharpLink.Abstractions.ISharpLinkRetryPolicy) | src/SharpLink.Client/SharpClientBuilder.cs:393 | 0 | 0 | 7 | 0 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseEndpointAdmission(SharpLink.Abstractions.ISharpLinkEndpointAdmissionPolicy) | src/SharpLink.Client/SharpClientBuilder.cs:405 | 0 | 0 | 20 | 2 | 0 | - |
| SharpLink.Client.SharpClientBuilder.UseCircuitBreaker(System.Action<SharpLink.Client.SharpLinkCircuitBreakerOptions>) | src/SharpLink.Client/SharpClientBuilder.cs:418 | 0 | 0 | 1 | 3 | 2 | - |
| SharpLink.Client.SharpClientBuilder.UseReconnectPolicy(SharpLink.Abstractions.SharpLinkReconnectPolicy) | src/SharpLink.Client/SharpClientBuilder.cs:432 | 0 | 0 | 20 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientTopologyRuntimeConfigurationExtensions | src/SharpLink.Client/SharpLinkClient.RuntimeConfigurationTopologyResults.cs:135 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientTopologyRuntimeConfigurationExtensions.TryUpdateFixedConnectionPoolSizing(SharpLink.Abstractions.ISharpLinkClient, int, int) | src/SharpLink.Client/SharpLinkClient.RuntimeConfigurationTopologyResults.cs:138 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientTopologyRuntimeConfigurationExtensions.TryUpdateClusterConnectionPoolSizing(SharpLink.Abstractions.ISharpLinkClient, int, int) | src/SharpLink.Client/SharpLinkClient.RuntimeConfigurationTopologyResults.cs:147 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientTopologyRuntimeConfigurationExtensions.TryUpdateLoadBalancing(SharpLink.Abstractions.ISharpLinkClient, SharpLink.Client.SharpLinkLoadBalancingStrategy) | src/SharpLink.Client/SharpLinkClient.RuntimeConfigurationTopologyResults.cs:156 | 0 | 0 | 2 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientTopologyRuntimeConfigurationExtensions.TryUpdateEndpointSelector(SharpLink.Abstractions.ISharpLinkClient, SharpLink.Abstractions.ISharpLinkEndpointSelector) | src/SharpLink.Client/SharpLinkClient.RuntimeConfigurationTopologyResults.cs:164 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientTopologyRuntimeConfigurationExtensions.TryUpdateTelemetryDetailPolicy(SharpLink.Abstractions.ISharpLinkClient, SharpLink.Abstractions.SharpLinkTelemetryDetailMode) | src/SharpLink.Client/SharpLinkClient.RuntimeConfigurationTopologyResults.cs:172 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRpcSessionFlushExtensions | src/SharpLink.Client/SharpLinkClient.RuntimeRpcSessionFlush.cs:13 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRpcSessionFlushExtensions.GetRpcSessionFlushPolicySnapshot(SharpLink.Abstractions.ISharpLinkClient) | src/SharpLink.Client/SharpLinkClient.RuntimeRpcSessionFlush.cs:16 | 0 | 0 | 9 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRpcSessionFlushExtensions.UpdateRpcSessionFlushPolicy(SharpLink.Abstractions.ISharpLinkClient, int, System.TimeSpan) | src/SharpLink.Client/SharpLinkClient.RuntimeRpcSessionFlush.cs:30 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRpcSessionFlushExtensions.TryUpdateRpcSessionFlushPolicy(SharpLink.Abstractions.ISharpLinkClient, int, System.TimeSpan) | src/SharpLink.Client/SharpLinkClient.RuntimeRpcSessionFlush.cs:49 | 0 | 0 | 5 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRuntimeConfigurationExtensions | src/SharpLink.Client/SharpLinkClientRuntimeConfigurationExtensions.cs:9 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRuntimeConfigurationExtensions.TryReplaceInterceptors(SharpLink.Abstractions.ISharpLinkClient, System.Collections.Generic.IEnumerable<SharpLink.Abstractions.ISharpLinkClientInterceptor>) | src/SharpLink.Client/SharpLinkClientRuntimeConfigurationExtensions.cs:12 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRuntimeConfigurationExtensions.TryUpdateRequestTimeout(SharpLink.Abstractions.ISharpLinkClient, System.TimeSpan) | src/SharpLink.Client/SharpLinkClientRuntimeConfigurationExtensions.cs:20 | 0 | 0 | 6 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRuntimeConfigurationExtensions.TryDisableRequestTimeout(SharpLink.Abstractions.ISharpLinkClient) | src/SharpLink.Client/SharpLinkClientRuntimeConfigurationExtensions.cs:28 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRuntimeConfigurationExtensions.TryUpdateRetryPolicy(SharpLink.Abstractions.ISharpLinkClient, SharpLink.Abstractions.ISharpLinkRetryOptions) | src/SharpLink.Client/SharpLinkClientRuntimeConfigurationExtensions.cs:34 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRuntimeConfigurationExtensions.TryUpdateRetryPolicy(SharpLink.Abstractions.ISharpLinkClient, SharpLink.Abstractions.ISharpLinkRetryPolicy) | src/SharpLink.Client/SharpLinkClientRuntimeConfigurationExtensions.cs:42 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRuntimeConfigurationExtensions.TryUpdateRetryPolicy(SharpLink.Abstractions.ISharpLinkClient, SharpLink.Abstractions.ISharpLinkRetryPolicy, SharpLink.Abstractions.ISharpLinkRetryOptions) | src/SharpLink.Client/SharpLinkClientRuntimeConfigurationExtensions.cs:50 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRuntimeConfigurationExtensions.TryDisableRetry(SharpLink.Abstractions.ISharpLinkClient) | src/SharpLink.Client/SharpLinkClientRuntimeConfigurationExtensions.cs:59 | 0 | 0 | 0 | 0 | 0 | - |
| SharpLink.Client.SharpLinkClientRuntimeConfigurationExtensions.TryUpdateHeartbeat(SharpLink.Abstractions.ISharpLinkClient, System.TimeSpan, System.TimeSpan) | src/SharpLink.Client/SharpLinkClientRuntimeConfigurationExtensions.cs:65 | 0 | 0 | 0 | 0 | 0 | - |
