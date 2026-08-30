# SharpLink maintainability report

Source ref: `00e2f18c6384c785d232bd59902102d3af7ad3da`
Tool ref: `bdb28961681e4c4e410fb6fe79582c917b22acfb`

## Summary

| Domain | Files | LOC | Methods | Large methods | Complex methods |
| --- | ---: | ---: | ---: | ---: | ---: |
| source | 293 | 70543 | 3816 | 81 | 66 |
| test | 366 | 127214 | 10053 | 75 | 33 |

## Top source files by LOC

| Path | LOC | Methods | Max method LOC | Max complexity | Using dependencies |
| --- | ---: | ---: | ---: | ---: | ---: |
| `src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs` | 1930 | 84 | 135 | 31 | 1 |
| `src/SharpLink.Generator/RpcGenerator.Analysis.cs` | 1655 | 138 | 132 | 17 | 0 |
| `src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs` | 1638 | 79 | 132 | 29 | 0 |
| `src/SharpLink.Client/SharpLinkClient.DynamicCluster.cs` | 1450 | 73 | 152 | 26 | 0 |
| `src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs` | 1379 | 91 | 151 | 27 | 0 |
| `src/SharpLink.Client/PendingRequestTable.cs` | 1305 | 62 | 71 | 12 | 0 |
| `src/SharpLink.Generator/RpcGenerator.DtoEmitter.cs` | 1285 | 42 | 179 | 22 | 0 |
| `src/SharpLink.Runtime/StreamManager.cs` | 1241 | 69 | 71 | 12 | 0 |
| `src/SharpLink.Runtime/PreAdmissionStreamDispatcher.cs` | 1193 | 34 | 190 | 24 | 0 |
| `src/SharpLink.Server/Admission/AdmissionStateKernel.cs` | 1160 | 50 | 77 | 26 | 0 |
| `src/SharpLink.Runtime/Transport/SharedMemoryPipelines.cs` | 1142 | 65 | 68 | 12 | 0 |
| `src/SharpLink.Client/SharpLinkClient.Invokers.cs` | 1099 | 31 | 154 | 23 | 0 |
| `src/SharpLink.Generator/RpcGenerator.ContractManifest.cs` | 1098 | 109 | 281 | 42 | 4 |
| `src/SharpLink.Client/SharpClientBuilder.cs` | 1044 | 105 | 82 | 13 | 0 |
| `src/SharpLink.Runtime/StreamFlowController.cs` | 958 | 42 | 62 | 15 | 0 |
| `src/SharpLink.Server/SharpLinkServer.cs` | 942 | 42 | 123 | 12 | 1 |
| `src/SharpLink.Client/SharpLinkClient.Interceptors.cs` | 924 | 60 | 71 | 13 | 0 |
| `src/SharpLink.Server/SharpLinkServer.Interceptors.cs` | 913 | 33 | 143 | 8 | 0 |
| `src/SharpLink.Client/SharpLinkClient.StaticCluster.cs` | 908 | 47 | 81 | 14 | 1 |
| `src/SharpLink.Server/Admission/AdmissionDynamicRateState.cs` | 904 | 58 | 58 | 12 | 1 |
| `src/SharpLink.Server/SharpLinkServerBuilder.cs` | 829 | 90 | 105 | 20 | 0 |
| `src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs` | 814 | 16 | 414 | 40 | 0 |
| `src/SharpLink.Client/SharpLinkMultiClusterClient.cs` | 800 | 53 | 98 | 19 | 2 |
| `src/SharpLink.Runtime/RpcSession.cs` | 786 | 44 | 65 | 11 | 0 |
| `src/SharpLink.Runtime/RpcSession.SendPump.cs` | 765 | 32 | 170 | 28 | 0 |

## Top test files by LOC

| Path | LOC | Methods | Max method LOC | Max complexity | Using dependencies |
| --- | ---: | ---: | ---: | ---: | ---: |
| `test/SharpLink.Generator.Tests/RpcAnalyzerTests.cs` | 3576 | 169 | 127 | 10 | 9 |
| `test/SharpLink.IntegrationTests/IntegrationBehaviorTests.cs` | 2692 | 286 | 86 | 15 | 0 |
| `test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.cs` | 2499 | 151 | 119 | 11 | 4 |
| `test/SharpLink.UnitTests/Runtime/PooledAsyncStreamDispatcherTests.cs` | 2155 | 138 | 119 | 7 | 6 |
| `test/SharpLink.UnitTests/Client/SharpLinkMultiClusterClientTests.cs` | 2154 | 276 | 60 | 6 | 12 |
| `test/SharpLink.IntegrationTests/TransportConnectionIntegrationTests.cs` | 2075 | 143 | 87 | 7 | 0 |
| `test/SharpLink.UnitTests/Client/SharpLinkClientLifecycleStateTests.cs` | 1857 | 155 | 66 | 8 | 10 |
| `test/SharpLink.UnitTests/Runtime/SharpLinkRuntimeContextTests.cs` | 1552 | 125 | 54 | 6 | 5 |
| `test/SharpLink.ChaosTests/Program.cs` | 1540 | 100 | 452 | 27 | 20 |
| `test/SharpLink.LoadTest/Program.cs` | 1506 | 82 | 337 | 70 | 18 |
| `test/SharpLink.IntegrationTests/DynamicEndpointIntegrationTests.cs` | 1401 | 110 | 144 | 6 | 2 |
| `test/SharpLink.UnitTests/Server/SharpLinkServerInvocationTests.cs` | 1399 | 80 | 138 | 18 | 13 |
| `test/SharpLink.IntegrationTests/StaticEndpointIntegrationTests.cs` | 1360 | 107 | 66 | 6 | 0 |
| `test/SharpLink.IntegrationTests/SharedMemoryTransportConnectionIntegrationTests.cs` | 1330 | 70 | 81 | 8 | 9 |
| `test/SharpLink.Benchmarks/SendPumpIsolationEvidenceRunner.cs` | 1318 | 69 | 143 | 15 | 13 |
| `test/SharpLink.Benchmarks/DecodeExecutionPhase0EvidenceRunner.cs` | 1312 | 65 | 132 | 17 | 12 |
| `test/SharpLink.IntegrationTests/InterceptorIntegrationTests.cs` | 1289 | 106 | 54 | 6 | 0 |
| `test/SharpLink.UnitTests/Runtime/RpcSessionLifecycleTests.cs` | 1281 | 97 | 103 | 9 | 5 |
| `test/SharpLink.UnitTests/Runtime/StreamManagerTests.cs` | 1252 | 119 | 61 | 6 | 3 |
| `test/SharpLink.UnitTests/Runtime/RequestManagerTests.cs` | 1095 | 88 | 69 | 5 | 5 |
| `test/SharpLink.IntegrationTests/RpcChannelCallShapeIntegrationTests.cs` | 1056 | 216 | 114 | 5 | 1 |
| `test/SharpLink.UnitTests/Runtime/StreamFlowControllerTests.cs` | 1047 | 49 | 63 | 6 | 2 |
| `test/SharpLink.Generator.Tests/ContractManifestGeneratorTests.cs` | 1040 | 93 | 57 | 7 | 11 |
| `test/SharpLink.Benchmarks/ConnectionAdmissionEvidenceRunner.cs` | 1006 | 57 | 117 | 9 | 20 |
| `test/SharpLink.UnitTests/Client/SharpLinkClientRetryTests.cs` | 974 | 66 | 47 | 6 | 5 |

## Top 25 large methods (>= 80 LOC)

| Domain | Method | Location | LOC | Complexity |
| --- | --- | --- | ---: | ---: |
| test | `Main` | `test/SharpLink.ChaosTests/Program.cs:38` | 452 | 27 |
| source | `DispatchOneWayRpc` | `src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs:5` | 414 | 40 |
| source | `Initialize` | `src/SharpLink.Generator/RpcGenerator.cs:24` | 388 | 1 |
| source | `ContinueRpcDispatch` | `src/SharpLink.Server/SharpLinkServer.InvocationContinuation.cs:10` | 368 | 28 |
| test | `ExecuteStageAsync` | `test/SharpLink.LoadTest/Program.cs:432` | 337 | 35 |
| source | `DispatchRpcAsync` | `src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs:5` | 335 | 27 |
| source | `CompareContractManifests` | `src/SharpLink.Generator/RpcGenerator.ContractManifest.cs:479` | 281 | 42 |
| source | `ProcessRequestLoop` | `src/SharpLink.Server/SharpLinkServer.RequestLoop.cs:57` | 275 | 44 |
| test | `ExecuteStageAsync` | `test/SharpLink.StreamLoadTest/Program.cs:267` | 265 | 40 |
| test | `Parse` | `test/SharpLink.LoadTest/Program.cs:929` | 260 | 70 |
| test | `Main` | `test/SharpLink.Benchmarks/Program.cs:9` | 208 | 69 |
| source | `CreateContractManifest` | `src/SharpLink.Generator/RpcGenerator.ContractManifest.cs:202` | 203 | 25 |
| source | `DispatchCompressedAsync` | `src/SharpLink.Runtime/PreAdmissionStreamDispatcher.cs:224` | 190 | 24 |
| source | `ReplaceAssemblyAsync` | `src/SharpLink.Server/SharpLinkServer.AssemblyRegistration.cs:150` | 184 | 21 |
| test | `MeasureAsync` | `test/SharpLink.Benchmarks/DecodeExecutorBlockedWriterCancellationEvidenceRunner.cs:47` | 179 | 33 |
| source | `AppendDtoCodec` | `src/SharpLink.Generator/RpcGenerator.DtoEmitter.cs:136` | 179 | 22 |
| test | `RunAsync` | `test/SharpLink.LoadTest/HoldCapacity.cs:120` | 171 | 17 |
| source | `RunAsync` | `src/SharpLink.Runtime/RpcSession.SendPump.cs:211` | 170 | 28 |
| source | `ReplaceAssemblyAsync` | `src/SharpLink.Client/SharpLinkClient.AssemblyRegistration.cs:125` | 170 | 21 |
| source | `ValidateShape` | `src/SharpLink.Runtime/SharpLinkGeneratedManifestCompatibility.cs:139` | 169 | 45 |
| source | `DispatchRpcWithPersistentDecodeAsync` | `src/SharpLink.Server/SharpLinkServer.PersistentDecodeDispatch.cs:5` | 169 | 6 |
| source | `AppendStubDispatchCases` | `src/SharpLink.Generator/RpcGenerator.StubEmitter.cs:262` | 154 | 26 |
| source | `InvokeOneWayCoreAsync` | `src/SharpLink.Client/SharpLinkClient.Invokers.cs:559` | 154 | 23 |
| source | `ApplySnapshotAsync` | `src/SharpLink.Client/SharpLinkClient.DynamicCluster.cs:430` | 152 | 26 |
| source | `Visit` | `src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs:361` | 151 | 26 |

## Top 25 complex methods (>= 15)

| Domain | Method | Location | LOC | Complexity |
| --- | --- | --- | ---: | ---: |
| test | `Parse` | `test/SharpLink.LoadTest/Program.cs:929` | 260 | 70 |
| test | `Main` | `test/SharpLink.Benchmarks/Program.cs:9` | 208 | 69 |
| source | `ValidateShape` | `src/SharpLink.Runtime/SharpLinkGeneratedManifestCompatibility.cs:139` | 169 | 45 |
| source | `ProcessRequestLoop` | `src/SharpLink.Server/SharpLinkServer.RequestLoop.cs:57` | 275 | 44 |
| source | `CompareContractManifests` | `src/SharpLink.Generator/RpcGenerator.ContractManifest.cs:479` | 281 | 42 |
| source | `DispatchOneWayRpc` | `src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs:5` | 414 | 40 |
| test | `ExecuteStageAsync` | `test/SharpLink.StreamLoadTest/Program.cs:267` | 265 | 40 |
| test | `ExecuteStageAsync` | `test/SharpLink.LoadTest/Program.cs:432` | 337 | 35 |
| source | `ValidatePayloadShape` | `src/SharpLink.Runtime/ProtocolV2/ProtocolV2FrameCodec.cs:147` | 91 | 35 |
| source | `TryLoad` | `src/SharpLink.Runtime/SharpLinkDynamicModule.cs:16` | 140 | 34 |
| test | `Parse` | `test/SharpLink.StreamLoadTest/Program.cs:654` | 136 | 34 |
| test | `MeasureAsync` | `test/SharpLink.Benchmarks/DecodeExecutorBlockedWriterCancellationEvidenceRunner.cs:47` | 179 | 33 |
| test | `VerifyClientAsync` | `test/SharpLink.AotSmoke/Program.cs:195` | 90 | 32 |
| source | `TryAcquireCore` | `src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs:758` | 132 | 31 |
| source | `ProcessRequestLoop` | `src/SharpLink.Client/SharpLinkClient.Lifecycle.cs:454` | 148 | 30 |
| source | `TryGetConstantSize` | `src/SharpLink.Generator/RpcGenerator.StubEmitter.cs:470` | 43 | 30 |
| source | `TryReturnToPool` | `src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs:1410` | 132 | 29 |
| test | `ReadRuns` | `test/SharpLink.Benchmarks/LatencyRecorderBaselineAnalyzer.cs:170` | 101 | 29 |
| source | `ContinueRpcDispatch` | `src/SharpLink.Server/SharpLinkServer.InvocationContinuation.cs:10` | 368 | 28 |
| source | `RunAsync` | `src/SharpLink.Runtime/RpcSession.SendPump.cs:211` | 170 | 28 |
| test | `Main` | `test/SharpLink.ChaosTests/Program.cs:38` | 452 | 27 |
| source | `DispatchRpcAsync` | `src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs:5` | 335 | 27 |
| source | `ResolveCodec` | `src/SharpLink.Runtime/Codec/RpcCodecProvider.cs:47` | 113 | 27 |
| source | `CollectAdapterRegistrations` | `src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs:155` | 92 | 27 |
| test | `OnDeserialized` | `test/SharpLink.CodecCompatibility/Models.cs:231` | 54 | 27 |
## Metric definitions

- LOC: Physical line count from Roslyn SourceText; generated build output under bin/ and obj/ is excluded.
- Method LOC: Inclusive physical line span for C# method-like executable bodies, including local functions, lambdas, and anonymous methods.
- Cyclomatic complexity: 1 plus if/loop/catch/case/switch-expression-arm/conditional-expression/&&/|| decision points inside each executable body; nested local functions, lambdas, and anonymous methods are excluded from the parent and measured independently.
- Using dependency count: Distinct namespace targets from non-global using directives in the file; this is a lightweight coupling proxy.
