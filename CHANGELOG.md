# 更新日志

本文档记录项目的重要变更，格式参考 Keep a Changelog，并遵循语义化版本。

## [Unreleased]

## [0.8.6] - 2026-07-27

### Fixed

- Stream transport and RPC session teardown now continue through every owned resource after unexpected completion failures and preserve the complete ordered error set.
- Concurrent RPC session disposers now observe the same terminal cleanup outcome.
- Connection-scoped and server-wide service cleanup now reports every disposal failure after completing all remaining services and the owned provider.
- Hosted servers now supervise asynchronous run-loop failure, log it, and request Generic Host shutdown instead of leaving a live process with a dead RPC endpoint.

### Compatibility and validation

- Public APIs, Protocol v2, and generated Manifest versions are unchanged. Cleanup callers may now receive `AggregateException` when multiple resources fail.
- Release build, Generator 83/83, Unit 369/369, Integration 228/228, package smoke, and same-machine disposal A/B passed.
- Normal session disposal measured 950.9 → 955.8 ns with overlapping 99.9% confidence intervals and unchanged 17.5 KB allocation.

## [0.8.5] - 2026-07-27

### Fixed

- Hosted single-client publication and terminal stop/failure are now serialized; a racing startup can no longer resurrect or return a client after the host stopped.
- Call- and connection-scoped activation rollback now preserves both the service-factory failure and scope cleanup failure.
- Call and connection service disposal now completes every cleanup layer and aggregates service and scope failures instead of silently discarding one cause.
- Fixed-client initial pool rollback now disposes every established connection, preserves the later connection failure together with all cleanup failures, and always leaves a failed attempt in `Faulted` rather than `Connecting`.
- Leased RPC invocation now preserves handler, request-stream completion, and lease cleanup failures together for exception mappers and diagnostics.

### Compatibility and validation

- Public APIs, generated Manifest versions, and Protocol v2 wire layouts are unchanged. Custom exception mappers may now receive an `AggregateException` when user execution and cleanup fail together; inspect its inner causes rather than assuming a single exception.
- Release build completed with 0 warnings/errors. Generator 83/83, Unit 364/364, Integration 228/228, five deterministic pre-fix failure probes, connection/call branch-completeness regressions, and the package restore/run smoke passed.
- Published-client accessor lookup remained allocation-free and statistically flat at 1.457 → 1.483 ns on the same Apple M4/.NET 10 benchmark gate; the 99.9% confidence intervals overlap.

## [0.8.4] - 2026-07-27

### Fixed

- Codec resolution now revalidates the generated-registration snapshot after user/native factory work, preventing a lookup that overlaps dynamic publication from returning or caching a superseded wire Codec.
- In-flight fallback and generated Codec resolution now observes Runtime Context disposal before publishing its result, so a disposed provider cannot be repopulated or return a newly resolved Codec.
- Pre-admission client-stream replay no longer synchronously waits for an incomplete dispatcher operation. Retained and newly arriving frames remain in one bounded ordered queue until the generated dispatcher is ready.
- Generated dispatcher configuration and replay now execute outside the per-request stream-registry lock, allowing safe callback reentrancy while preserving entry and buffer leases through asynchronous replay.
- Multi-cluster replacement now reconciles coordinator routes when the child committed its new assembly but old-generation cleanup failed; the original cleanup exception still reaches the caller.

### Compatibility and validation

- Public APIs and Protocol v2 wire layouts are unchanged. Rare Codec publication races may retry a fallback resolver or native generated factory, which must already tolerate concurrent resolution.
- Release build, Generator 83/83, Unit 357/357, Integration 228/228, six deterministic pre-fix failure probes across five findings plus branch-completeness regressions, and same-machine BenchmarkDotNet A/B validation passed.
- Cached explicit/fallback Codec lookup remained statistically flat at 6.529 → 6.533 ns and 6.515 → 6.504 ns. Cached generated lookup improved from 8.670 to 6.499 ns; attached pre-admission dispatch improved from 17.656 to 17.098 ns. All remained 0 B/op; an earlier 19.755 ns dispatch candidate was rejected and redesigned.

## [0.8.3] - 2026-07-26

### Fixed

- `SharpLinkEndpointSnapshot` now clones each endpoint and freezes nested attributes, so mutations through the source dictionary or a cast cannot alter a published topology.
- Fixed, static/dynamic-cluster, and multi-cluster client shutdown now await `CancelAsync`; blocking callbacks no longer prevent `StopAsync` from returning its asynchronous operation, and callback failures no longer skip remaining cleanup.
- Failed fixed/static/dynamic connection attempts now preserve both the primary connect/handshake exception and any transport/session cleanup failure instead of replacing the root cause.
- Client, multi-cluster, and Server HostedService startup cleanup now preserves the original startup/run failure together with cleanup errors and continues token cleanup.
- Protocol metadata decoding now adopts its already validated entry array instead of allocating and copying a second array.

### Compatibility and validation

- Valid wire payloads are unchanged. `SharpLinkMetadata` keeps its existing public constructor signature; only the Runtime receives internal validated-array ownership.
- Release build, Generator 83/83, Unit 348/348, Integration 227/227, four pre-fix mutation/lifecycle probes, and three-launch metadata benchmarks passed.
- Two-entry metadata decode improved from 68.33 ns / 280 B to 61.89 ns / 224 B. Public construction remained 80 B and showed no regression.

## [0.8.2] - 2026-07-26

### Fixed

- Fixed-endpoint `ConnectAsync` now owns its shared initialization independently of individual waiters, so cancellation by the first caller no longer cancels concurrent callers or faults the client-wide attempt.
- Fixed, static-cluster, and dynamic-cluster connections now share one handshake-timeout classifier. Endpoint clusters retain a structured `Unavailable` timeout cause instead of burying a linked-token `OperationCanceledException`.
- DNS last-good fallback now catches transient `SocketException` failures only. Unexpected resolver implementation failures propagate to callers or the supervised watch loop instead of being silently hidden forever.
- Protocol v2 length fields reject overlong VarUInt32 representations, restoring a single canonical wire encoding for metadata and error lengths.
- Binary error payloads are validated with strict UTF-8 across contiguous and segmented frames; malformed peer text now terminates the frame as `ProtocolViolation` instead of being lossily replaced.

### Compatibility and validation

- Valid Protocol v2 payloads and generated RPC layouts are unchanged. Peers that emitted overlong VarUInt32 values or invalid UTF-8 error text are now rejected and must emit the canonical writer format.
- Release build, Generator 83/83, Unit 344/344, Integration 227/227, five pre-fix failure probes, and three-launch frame-parser benchmarks passed.
- The metadata parser changed from 42.67 ns to 39.60 ns while the same-run control changed from 39.32 ns to 40.23 ns; both remained 0 B/op. Because host variance was high, the result is treated as a no-regression signal rather than an improvement claim.

## [0.8.1] - 2026-07-26

### Fixed

- Authentication scopes are now frozen snapshots. Callers can no longer cast `Scopes` to a mutable set, inject privileges, or contaminate the process-wide empty scope set.
- Endpoint snapshots and all generated assembly/cluster manifest collections now expose read-only wrappers, including nested method and service-dependency arrays.
- Built-in delegate and DNS endpoint resolvers now share idempotent asynchronous disposal, await cancellation callbacks, dispose their owned cancellation sources, and synchronize operation admission against disposal.
- Generated request decoders canonicalize and validate Boolean bytes and route semantic fixed values (`decimal`, date/time types, `Rune`, `Index`, and `Range`) through their validating built-in Codecs. Raw numeric hot paths remain inline.
- Native `List<T>` decoding now writes directly into List-owned storage, eliminating the intermediate array and full copy.

### Compatibility and validation

- Requests containing the semantic fixed values listed above use length-delimited Codec framing in 0.8.1. Rebuild and deploy affected Client/Server contracts together; Boolean and raw numeric request layouts remain unchanged.
- Release build, Generator 83/83, Unit 339/339, Integration 227/227, immutability/lifecycle mutation probes, and three-round alternating `Rpc_SumList` benchmarks passed.
- `Rpc_SumList` retained 99.56% throughput at 16 items and improved to 102.53% at 256 items. Allocations fell from 560 to 472 B/op and from 2480 to 1432 B/op.

## [0.8.0] - 2026-07-26

### Fixed

- Native built-in Codecs now reject truncated or trailing payload bytes consistently for contiguous and segmented input, and reject non-canonical Boolean and nullable-presence markers as `DataLoss`.
- Connection-level stream-credit batching now returns credit for every contributing stream instead of stranding credit on an idle open stream; the session emits each resulting `WindowUpdate` exactly once.
- RPC contracts now include inherited base-interface methods, including diagnostics, DTO-root discovery, proxy generation, stub dispatch, and deterministic handling of directly redeclared signatures.
- Unmanaged user-defined and nullable request parameters now use the selected registered Codec and length-delimited request framing instead of bypassing it through native layout blitting.

### Compatibility and validation

- Valid built-in Codec payloads remain compatible; previously accepted non-canonical or trailing bytes are now rejected.
- Generated request wire layout changes for nullable and user-defined unmanaged parameters, and inherited methods change the derived contract fingerprint. Rebuild and deploy both peers together; see `doc/migration-0.8.0.md`.
- Release build, Generator 81/81, Unit 336/336, Integration 227/227, targeted malformed-input coverage, emitted-frame verification, and same-machine BenchmarkDotNet comparison passed. The seven runtime hot paths retained allocations and ranged from 93.09% to 101.64% of baseline latency (lower is better), with no regression signal.

## [0.7.11] - 2026-07-26

### Added

- Added generic compile-time Codec Adapter registration and explicit binding APIs: `RpcCodecAdapterRegistrationAttribute`, `RpcCodecAdapterAttribute`, `IRpcCodecAdapter`, and `IRpcCodecAdapterScope`.
- Added `SharpLink.Serializer.SharpPack` with the exact SharpPack dependency range `[1.1.0]`. `[SharpPackable]` selects the Adapter automatically, while frameworks without a selector Attribute can use explicit type or assembly bindings.
- Contract manifests now require `wireFormatId` for every request, response, stream item, and DTO member, plus a top-level reachable Codec wire inventory. Compatibility compares wire identity rather than Adapter implementation identity, including Adapter types nested inside native collections.

### Changed

- Generated Manifest API is version 3. Adapter factories emit closed `CreateCodec<T>()` calls and contain no runtime generic construction, serializer scanning, or reflection resolver.
- Adapter state is owned per Runtime Context, Manifest instance, and Adapter ID. Automatic SharpPack Scopes own isolated formatter graphs. Dynamic register/replace/unregister publishes transactionally, validates every factory Adapter instance, and releases generation-owned Codec caches, Scopes, and serializer Contexts after drain even when another cleanup fails.
- SharpPack serialization uses a zero-allocation concrete writer bridge so SharpPack 1.1.0 remains visible to the NativeAOT IL scanner when SharpLink receives an `IBufferWriter<byte>` interface.
- Explicit `UseCodec` remains the highest priority and caller-owned. Ordinary supported DTOs continue to use SharpLink native generated Codecs even when an Adapter package is installed.

### Removed

- Removed `SharpLink.Serializer.MemoryPack`, the MemoryPack package dependency, `MemoryPackCodec`, `MemoryPackCodec<T>`, `RpcExternalCodecAttribute`, and the process-wide generated Codec registry.

### Compatibility and validation

- This is a source/API-breaking pre-1.0 migration. Development-time contract manifests without `wireFormatId` or the reachable `codecs` inventory are invalid and must be regenerated; no legacy fallback or compatibility shell is retained.
- MemoryPack 1.21.4 golden payloads for null, nullable/string/non-ASCII, arrays/lists/dictionaries, nested objects, empty collections, unions, and circular graphs are byte-identical under SharpPack 1.1.0.
- Local Release, Generator/Unit/Integration, collectible ALC, NativeAOT, local NuGet PackageSmoke, five-round BenchmarkDotNet, and TCP QPS/P99 validation cover the migration. No remote state or package feed was changed.

## [0.7.10] - 2026-07-22

### Added

- `SharpLinkMultiClusterClientBuilder`, `ISharpLinkMultiClusterClient`, and `SharpLinkClusterKey` coordinate multiple isolated child clients while routing each generated contract to exactly one cluster slot.
- `[assembly: SharpLinkClusterContractAssembly(cluster, typeof(Marker))]` generates a deterministic, weak-catalogued static route manifest. The generator rejects invalid cluster keys, missing generated manifests, and contradictory contract-assembly routes.
- Dynamic registration, unregister, and replacement now have explicit multi-cluster overloads. Contract-owning assemblies remain exclusive to one slot; dependency-only assemblies can remain independently owned by more than one slot.
- `AddSharpLinkMultiClusterClient` adds hosted startup, shutdown, and `ISharpLinkMultiClusterClientAccessor` without exposing child `ISharpLinkClient` instances through DI.

### Changed

- `SharpClientBuilder.Build()` keeps its existing complete generated-manifest snapshot behavior. The multi-cluster builder uses an internal filtered build context, so ordinary clients and the fixed-endpoint RPC hot path do not perform coordinator lookups or acquire new locks.
- Cluster selection is performed only by `Get<TContract>()`; generated proxies call their selected child channel directly and Protocol v2 frames, handshake capabilities, headers, and metadata do not carry a cluster key.

### Compatibility

- Ordinary single-client applications require no migration. Endpoint-cluster retry, admission, circuit-breaker, resolver, transport, authentication, and connection-pool behavior stays owned by each child client.
- Static NativeAOT routing remains manifest based. Runtime assembly registration continues to return the existing structured platform-not-supported result when unavailable.

## [0.7.9] - 2026-07-21

### 收敛

- endpoint 路径补充低基数 `sharplink.client.attempts`、`retries`、`endpoint_admission.rejected`、`breaker.open` 与 `selection.failures` metrics；默认标签不含 endpoint ID、地址、authority 或 transport 名称。
- 完成 static/dynamic topology、selector、Retry、custom admission 与 generation-scoped circuit breaker 的本地组合验证；物理 Ready/Draining 状态不因 admission 拒绝或 breaker Open 被伪装成断线。
- 文档补全迁移路径、传输限制、Retry/Breaker 语义与 0.7.x API freeze 审核说明。

## [0.7.8] - 2026-07-21

### 新增

- `ISharpLinkEndpointAdmissionPolicy`、`SharpLinkEndpointAdmissionDecision` 和 `SharpLinkEndpointOutcome` 提供 endpoint 级 TryAcquire/Report SPI；`UseEndpointAdmission` 显式启用自定义策略。
- `UseCircuitBreaker` 和 `SharpLinkCircuitBreakerOptions` 提供按 endpoint generation 隔离的 Closed/Open/HalfOpen breaker，默认关闭。

### 变更

- endpoint selection 在连接选择之前执行 admission；拒绝候选会继续选择其他 Ready endpoint，实际获得许可的 attempt 沿用 PendingCall 单一终结路径恰好 Report 一次。
- breaker 使用 monotonic time、惰性状态推进、有限采样 ring 和 HalfOpen 原子 permit，不创建每 endpoint timer；连接、拓扑与 `CheckHealthAsync` 的物理语义保持不变。

## [0.7.7] - 2026-07-21

### 新增

- `UseRetry()`、`UseRetry(Action<SharpLinkRetryOptions>)` 和 `UseRetry(ISharpLinkRetryPolicy)` 为显式标记 `[Idempotent]` 的 Unary 提供可选重试；默认最多三次、50/100/200 ms 指数退避和 ±20% jitter。
- `ISharpLinkRetryPolicy`、`SharpLinkRetryContext` 与 `SharpLinkRetryDecision` 提供同步、无 I/O 的自定义决策 SPI；非法 delay 或 policy 异常只失败当前 logical call，不影响 Client 健康。
- Retry attempt 复用既有 PendingCall 的单一完成仲裁，记录 endpoint/generation、connection、完成原因、响应是否已观测和耗时；无第二套 pending-request 表。

### 变更

- Client interceptor 仍只对一次 logical call 执行；Retry 位于其 terminal 内，每次 attempt 重新选择 endpoint，并共享入口冻结的绝对 deadline。
- 默认策略仅对 `[Idempotent]` Unary 的 endpoint 不可用、连接关闭/切换、发送失败与远端 `Unavailable` 重试；业务错误和 `ResourceExhausted`、OneWay 及所有 Streaming 不自动重试。
- 多 endpoint retry 使用调用内 `ulong` exclusion mask，优先尝试不同 Ready endpoint；尝试完当前 snapshot 后才复用候选，动态 generation 更新自动形成新候选集。
- telemetry 保持 logical call 指标一调用一次，并在 listener 存在时额外产生 `sharplink.rpc.attempt` Activity。

### 兼容性与验证

- Retry 默认关闭，因此固定单 endpoint 的既有 Unary 继续直接走原有路径；Protocol v2 wire format 和握手 capability 未改变。
- 覆盖远端 `Unavailable`、`ResponseObserved`、非幂等与 `ResourceExhausted` 拒绝重试、绝对 deadline、delay 中取消、custom policy、interceptor 一次性和 endpoint exclusion/reset。

## [0.7.6] - 2026-07-21

### 新增

- `SharpLinkEndpointSnapshot`、`ISharpLinkEndpointResolver` 与 `UseEndpointResolver` 提供版本化的动态 endpoint 拓扑；Client 对 Resolver 拥有明确的 Stop/Dispose 生命周期。
- `DelegateSharpLinkEndpointResolver` 支持连续 Watch 或单 worker 轮询，可适配 Consul、Nacos、Etcd 等应用已有 SDK，而 SharpLink 核心不引入其依赖。
- `UseDnsEndpoints` 与 `SharpLinkDnsEndpointResolver` 提供 A/AAAA Discovery、地址族筛选、规范化稳定 ID、hostname Authority、last-good 保留和可配置 refresh/jitter。

### 变更

- 动态快照以单 writer 原子协调：新增 ID 建立 generation；同 ID 的 Address/Authority 变化替换 generation；Attributes-only 更新保留连接；删除 endpoint 立即停止新调用并排空已有 Unary/Streaming。
- Resolver Watch 结束或异常后以 100 ms–30 s 的指数退避和 ±20% jitter 重启；空拓扑可恢复且继续遵守 WaitForReady、deadline、cancel 与 Stop。
- retired connection 使用独立预算。预算超出时抑制 replacement 而不强杀用户 stream，归零后 factory 恰好释放一次。

### 兼容性与验证

- Protocol v2 wire format、握手 capability、固定单 endpoint 与静态 cluster 的调用路径未改变；无新 NuGet 或第三方服务发现 SDK。
- 覆盖 add/remove/replace、属性更新、DNS、watch/retry、流排空、PackageSmoke、NativeAOT 及动态稳态矩阵；固定 TCP 五轮 A/B 的 QPS 中位数为 0.7.5 的 100.44%，P99 中位数保持 72 µs。

## [0.7.5] - 2026-07-21

### 新增

- Client 新增不可变的 `SharpLinkEndpoint`、显式传输地址和 transport factory 注册模型；`UseEndpoint`、`UseEndpoints`、`UseCluster` 及四种内置负载均衡策略可在不影响旧单端点用法的前提下构建静态端点集群。
- 静态集群支持 TCP（hostname/IPv4/IPv6）、Unix Domain Socket、Named Pipe、Shared Memory 和既有 Anonymous Pipe；端点属性可传给自定义选择器。
- 集群按端点独立维护连接、重连和健康状态，初始连接受 `MaxConnections` 与并行度上限约束；支持最少就绪端点、LeastPending、P2C、Random、RoundRobin 与自定义选择器。

### 变更

- `GoAway`、Stop 和 Dispose 进入排空流程后，新调用立即选择仍健康的端点；长 Unary 和流式调用在预算内继续完成，超出独立 retiring budget 时才被定点终止。
- 单个静态端点继续折叠为既有固定连接快速路径；多端点的成员快照仅在就绪成员增减时重建，调用路径仅读取原子快照和实时计数。
- Protocol v2 wire format、默认传输语义和现有公共配置保持兼容，未引入新的 NuGet 依赖。

### 测试与性能

- 覆盖多传输、地址族、故障/重连、GoAway 排空、所有选择策略、TLS、包使用与 NativeAOT；固定 TCP Unary 的五轮本地 A/B 中，吞吐中位数为基线的 100.27%，P99 为 104.48%，BenchmarkDotNet 分配保持 352 B/op。

## [0.7.4] - 2026-07-20

### 新增

- Protocol v2 minor 3 增加有界、确定性的压缩 wire-profile 协商；Request/Response/StreamData 只压缩业务 payload，保持路由、deadline、metadata 和 stream ID 可在分配前验证。
- `SharpLinkRuntimeOptions.Compression` 内置 Brotli，并支持线程安全的自定义 `ISharpLinkCompressionProvider` 扩展其他格式；默认按 1024 B、64 B 与 5% 三重收益门槛选择候选帧。
- `SharpLinkServerBuilder.UseAdmissionControl` 增加 Global、Contract、Method 与有界 Partition 的累计并发/速率限制，支持 TokenBucket、FixedWindow、SlidingWindow、总队列 call/byte 边界和 deadline/cancellation/Draining 联动。
- Generator 的 `RpcMethodDescriptor.ClientStreamCount` 为排队中的客户端流预留稳定 stream ID；permit 到达前按序 spool，压缩 item 按 wire bytes 记账并延迟解压。
- Generated Manifest API 升为 2；0.7.3 及更早版本的预编译生成程序集会在注册阶段明确拒绝，升级时需要重新运行 Source Generator 并编译。

### 安全性与性能

- 解压在精确有界 owner 中核验 consumed/written、原始长度和内置 Provider 完整性尾部；截断、损坏与尾部垃圾只终止当前调用/流，健康连接继续使用。
- 压缩默认关闭；普通 RPC 仅增加 Session 级空 Provider 分支，SendPump 不感知压缩且不新增锁、后台任务或每调用状态。
- Admission 默认关闭；普通调用只增加 Server 级空 controller 分支。拒绝调用保持连接健康，OneWay 不伪造执行成功，分区池机会式回收且不会永久增长。
- Provider 的 `WireProfile` 明确定义为完整 wire profile：内置 `CompressionLevel` 可按方向独立配置，dictionary 等影响解码的设置必须使用不同 profile；五轮吞吐矩阵覆盖关闭、Brotli 的 Fastest/Optimal/Smallest、收益阈值和可压缩/随机数据。

## [0.7.3] - 2026-07-20

### 新增

- Client 与 Server 增加 `ReplaceAssemblyAsync`，在完整准备和冲突验证后一次发布新路由快照，并复用现有调用/流排空、定点取消、延迟清理与 collectible ALC 引用释放机制。
- Source Generator 输出确定性的 JSON 契约 Manifest，并通过 `SharpLinkContractBaseline` 对上一版本执行 Contract、Method、DTO member、调用形态、wire type、required、enum、union 与 route 兼容性诊断。

## [0.7.2] - 2026-07-20

### 性能

- 静态 Singleton Unary 默认路径直接返回池化 `ValueTask`，不再为只负责等待响应的包装方法生成额外 async 状态机；`WaitForReady` 继续使用独立慢路径，取消、deadline、metadata、遥测和 interceptor 语义不变。
- 客户端请求操作池和 PendingCall 池改用无节点分配的并发队列，移除稳态归还时的 `ConcurrentStack` 节点分配。
- 同机五轮交替 A/B 的静态 Unary `add` 关键点中，五种传输相对 0.7.1 的 QPS 中位数提升 3.69%–13.10%，P99 均改善；进程级分配由约 482–520 B/op 降到约 138–166 B/op。
- BenchmarkDotNet 的 `Rpc_Add` 精确分配由 672 B/op 降到 360–364 B/op；剩余调用上下文与 `AsyncLocal` 分配保留认证、授权和公共 `SharpLinkCallContext.Current` 语义。

### 测试与文档

- 完整性能矩阵增加 c256/c512，并把原始输出统一到 `artifacts/performance/v0.7.2/`；补充多连接、流式、CPU、锁竞争、Allocation 与历史版本二分证据。
- 增加 0.7.2 性能报告和迁移说明。Protocol v2 wire format、公共 API、服务生命周期和资源上限均未改变。

## [0.7.1] - 2026-07-20

### 新增

- Source Generator 为每个程序集生成唯一的 RPC Manifest、定位特性与模块初始化器；Contract 程序集拥有 Descriptor、Proxy、contract-based Stub 和 Codec，Service 程序集拥有 Activator、生命周期与显式依赖。
- `[RpcService]` 服务在 Server `Build()` 时自动注册，默认生命周期为 `Singleton`；新增 `Connection` 与 `Call` 生命周期，以及 `EnableService`、`ExcludeService`、`DisableAutomaticServiceRegistration` 和 `ReplaceService` 筛选/替换入口。
- Client 与 Server 支持运行时程序集原子注册和异步安全注销；注册使用结构化成功/错误结果，注销报告剩余调用、流和框架引用释放状态。
- 动态模块支持 Running、Draining、Released/DrainTimedOut 状态、分片租约计数、依赖阻止卸载、超时定点取消，以及 collectible `AssemblyLoadContext` 卸载验证。
- Generator 增加服务声明、Contract/Method/Codec/Service 碰撞诊断，并为规范化描述生成 SHA-256 wire/schema 指纹。

### 变更

- 删除全部旧 `AddService` 重载；类型服务迁移为 `[RpcService]` 自动注册，实例和 factory 迁移为 `ReplaceService`。
- Singleton 根服务不创建调用 Scope；Connection 按物理连接惰性创建独立 Scope；Call Scope 覆盖完整 Unary、OneWay 或 Streaming 调用。
- Builder 和运行时动态 Registry 使用实例级原子快照；静态冲突优先由 Generator 报告，Build 与动态注册继续执行防御性验证和事务回滚。
- NativeAOT 保留纯静态 Manifest 路径，运行时程序集注册返回 `PlatformNotSupported`，不增加反射扫描 fallback。

### 修复

- 池化请求操作归还时立即清除 `ManualResetValueTaskSourceCore` 的 continuation，避免动态 DTO、Task 和 collectible ALC 被进程级对象池意外保留。
- Stop、Dispose、Disconnect 与并发注销复用幂等排空操作，避免重复释放、过早移除路由或同步完成留下陈旧操作。

### 性能与兼容性

- Protocol v2 wire format 未改变；静态 Singleton 保留无 Scope、无动态计数的快速路径，动态读路径不进入注册 writer gate。
- 完整性能报告和机器可读逐轮证据保存在本地忽略目录 `artifacts/performance/v0.7.1/`，不进入源代码包。

## [0.7.0] - 2026-07-20

### 新增

- 增加显式启用、仅限同机同一用户的实验性共享内存传输，以及 Client/Server Builder 的 `UseSharedMemory` 配置入口。
- 每条连接使用双向 SPSC 共享内存环传输 RPC 数据；命名管道控制通道只负责有界握手、合并唤醒、关闭和进程存活检测。
- 增加容量、SpinCount 与握手超时配置，并按 LowLatency、Balanced、Throughput profile 提供默认值；双方容量不一致时协商较小值，初始化失败不会静默降级。
- LoadTest、StreamLoadTest、Chaos、PackageSmoke 和 NativeAOT smoke 增加 SharedMemory 模式与结构化性能证据。

### 变更

- 共享内存读写管线支持环内直接写入/读取、分段回卷、有界池化 spill、背压和可复用异步等待，详细热路径计数仅在显式诊断模式启用。
- 映射采用版本化布局和当前用户私有目录；握手校验 nonce、路径、权限与布局，RPC 认证、授权、deadline、流控和心跳继续生效。

### 修复

- 修复共享等待标志可能丢失唤醒、竞态游标快照被误判为数据损坏，以及映射校验失败、握手失败和关闭路径中的资源清理问题。
- 修复通知合并、满环 spill、取消后恢复 Flush、连接强杀与 listener 重启等竞态；连接释放不会等待对端腾出环空间。

### 性能与稳定性

- macOS arm64 完成 Release JIT、独立进程 NativeAOT、包消费与两轮 10 分钟 SharedMemory Chaos；最新一轮为 4,308,099 次成功、0 次非预期失败，结束后指标、活动调用、临时映射与测试进程归零。
- Windows x64、Linux x64、macOS arm64 的 Release 构建、Unit、Generator、Integration、PackageSmoke、独立进程 SharedMemory NativeAOT 和两分钟 Chaos smoke 全部通过；正式五轮性能矩阵、2 小时及 24 小时门禁仍待完成，因此该传输仍为实验性功能，不进入正式支持矩阵。
- 32 B / LowLatency 的单轮方向性样本中，SharedMemory 在 c1/c8/c32/c128 吞吐均领先 UDS；该样本不替代正式五轮性能门禁。

完整设计、正确性证据、性能数据和未完成门禁见 `doc/shared-memory-experiment.md`。

## [0.6.10] - 2026-07-18

### 新增

- Protocol minor 2 增加可协商的 `CancellationReason` capability；协商后 Cancel 帧携带 `UserCancellation`、`DeadlineExceeded` 或 `ConsumerAbandoned`，与 0.6.9 对端仍使用空载荷 Cancel 互操作。
- Source Generator 增加 `SHARPLINK014`：Streaming 契约缺少 `CancellationToken` 时编译失败；`[NonCancellable]` 可显式豁免。增加 `SHARPLINK015`，拒绝特性与 Token 同时声明。
- `sharplink.calls.abandoned` 增加低基数终止原因标签；新增 `sharplink.responses.late_dropped` 指标。

### 变更

- 生成 Stub 将显式 Token、任意 client stream 参数和 stream 返回值都声明为框架可取消；即使业务方法标记 `[NonCancellable]`，stream pump、dispatcher、窗口等待和连接资源仍可终止。
- 服务端在请求入口把绝对 UTC deadline 换算为 monotonic timestamp，之后的到期调度和响应仲裁不再受 wall clock 调整影响。
- 服务端 deadline 使用每物理连接一个 Timer 扫描最多 1,024 个在途调用；正常完成路径不维护 timer node，也不进入 scheduler lock。
- 每连接迟到响应 Warning 最多五秒一次，并携带前一窗口被抑制的数量；迟到响应 metric 仍逐次记录。

### 修复

- 修复 deadline CTS 先取消业务 Token、后发布 `DeadlineExceeded`，导致业务取消回调可能观察到空或错误终止原因的竞态。
- 非协作调用不再仅因携带 deadline 创建 invocation CTS；超时后继续观察用户 Task，但抑制其迟到成功或异常响应。
- deadline 扫描快照同时保存 request ID 与池化 state；旧代扫描不能获取已经归池并被新请求租用的对象。
- 修复无业务 Token 的 server streaming 提前停止消费后，框架流泵可能无法及时终止的问题。
- 修复本地 stream 取消与已取得的异步 dispatch 竞争时，Cancel 可能先于最后的 WindowUpdate 到达并使对端把合法 credit 误判为协议错误、关闭健康连接的问题。
- 修复连接池扩容恰逢滚动重启失败后可能没有把零 Ready 池交给持久重连 worker，客户端永久停留在 `Draining/Reconnecting` 的问题。
- Ready connection snapshot 现在是请求选择的事实来源；全局状态发布的瞬时滞后不再拒绝已就绪连接。GoAway 排空且暂无连接返回 `Unavailable`，只有 Client Stop/Dispose 返回 `ConnectionClosed`。
- 修复 StreamManager 终止 drain 与迟到 stream 注册竞争时，dispatcher 可能挂在已经移出 map 的节点上并永久多计一个 active stream 的问题；正常注册使用两次终态读取，不增加全局锁。
- Server call admission 现在区分排空中的 `Unavailable` 与真实容量耗尽的 `ResourceExhausted`，不再把 Request accept 后发生的停机竞态错误归类为限流。

### 性能与稳定性

- `ServerCallCancellationState` 专项基准中，cooperative deadline 从实验性独立 CTS 的 368 B/op 降至 80 B/op；相对 0.6.9 的 320 B/op 也显著下降。non-cooperative deadline 从 320 B/op 降至 32 B/op。
- 增加 100,000 次客户端 Response/Cancel/Deadline 和服务端 Cancel/Response/Deadline 终态竞态、10,000 次真实 stream early-break、10,000 次 stream register/drain 竞态，以及 Stop/Connection 并发取消测试。
- 五轮交替 A/B 的所有 Unary/Streaming 场景均通过 97%/105% 门禁；`Rpc_Add` ShortRun 保持 672 B/op。
- Stream register/drain 修复的第一版全局锁实验因 QPS -7.54%、P99 +14.34% 被撤销；最终无锁版本专项五轮 A/B 为 QPS +5.31%、P99 -8.27%，两边均为零失败。
- 最终代码提交的 2 核 120 秒混合 Chaos 完成 2,632,568 次成功调用、11 次滚动重启与 0 次非预期失败；最大恢复 331ms，最终所有框架 gauge 为 0。

完整协议、迁移和证据见 `doc/protocol-v2.md`、`doc/migration-0.6.10.md`、`doc/performance-0.6.10.md` 与 `doc/chaos-0.6.10.md`。

## [0.6.9] - 2026-07-17

### 新增

- 增加 `SharpLink.ChaosTests`，覆盖混合 Unary/Streaming、提前停止消费、取消/deadline、滚动 TCP 重启、重连和最终框架指标归零。
- 增加 PR 两分钟、Nightly 两小时 Chaos 分级 Gate，以及专用宿主连续 24 小时长稳脚本与结构化 JSON 证据。
- 增加 JIT/NativeAOT 性能矩阵入口，覆盖 Transport、Profile、连接池、payload、并发、Unary、OneWay、同步/异步服务和 Streaming。
- LoadTest 增加 Empty Unary、OneWay 与 AOT-safe source-generated JSON 报告。

### 变更

- `StopAsync` 使用 graceful timeout 与固定五秒框架清理预算有界返回；不合作的业务 Task 不再永久阻塞宿主停机。
- 仍在执行的用户调用保留自身 DI scope/provider，并在真实结束后延迟清理；listener、session、Pipe、send queue 等框架资源不被业务 Task 长期保留。
- Stream Dispatcher 静态池改为每个 item 类型最多保留 1,024 个对象；大于 256 的缓冲在回池前缩回初始容量并清除引用。
- OneWay 性能证据区分正常单生产者吞吐与主动耗尽有界发送队列的 backpressure 场景。

### 修复

- 修复 server/duplex stream 提前停止消费时，WindowUpdate、Cancel、pending slot、dispatcher 租约和 send credit 之间的竞态与泄漏。
- 修复 dispatcher 在旧调用仍持有 dispatch entry 时过早回池，随后被另一调用复用并被迟到完成污染的问题。
- 修复 server/duplex stream 在异步等待连接注册期间被消费者释放后提前回池，恢复线程随后把已清空 Codec 的 dispatcher 注册到新连接的问题。
- 修复 Cancel 到达已完成调用后，响应 stream send state 未被终止并可能重新创建 credit 状态的问题。
- 修复 Session 已终止后迟到的 `NotifyConnected` 重新增加 active-connection 指标、且再无关闭机会抵消的问题。
- 修复 TLS 重连测试可能把上一连接代际尚未清空时的旧 Ready 状态误判为新连接已经可用的问题。
- 修复握手帧与后续 GoAway/首个 Request 共用同一 Pipe buffer 时，未消费尾帧被错误标记为已检查并一直等待新字节的问题。
- 重连改为实例级持久 supervisor：容量 1 的故障信号不会在 worker 退出边界丢失；同时已被立即排空的连接不再发布全局 Ready，连接归零后会持续补充替代连接直到 Client Stop。
- 修复 PendingCall 发布到 slot 后、owner active count 注册前被 Cancel/断连取走，造成 active count underflow 并击穿客户端读循环的竞态。
- 修复 NativeAOT LoadTest 报告依赖反射 JSON 序列化的问题。

### 性能与稳定性

- 五轮 TCP Unary A/B：c1 QPS +1.86%、P99 持平；c128 QPS -0.15%、P99 -1.82%，全部零错误并通过门禁。
- 五轮 Server Streaming A/B：QPS -1.45%、P99 -2.49%，零错误并通过门禁。
- `Rpc_Add` 保持 672 B/op；JIT/NativeAOT smoke 正常矩阵全部零错误，AOT publish 零 trimming/AOT 警告。
- 两分钟混合 Chaos 完成 2,943,483 次成功调用和 9 次滚动重启；以连续五次探针确认稳定恢复，最大端到端恢复 15.583 秒，非预期失败为 0，结束时所有框架 gauge 为 0。

0.6.8 → 0.6.9 没有公共 API 或 Protocol v2 wire 变更。完整证据见 `doc/performance-0.6.9.md`、`doc/chaos-0.6.9.md` 和 `doc/migration-0.6.9.md`。

## [0.6.8] - 2026-07-17

### 新增

- `ServerConnectionState` 统一拥有物理连接的 Session、认证上下文、最后接受的 request ID、每连接调用额度、取消表、连接 token 与 Handshaking/Ready/Draining/Closed 生命周期。
- `ServerCallCancellationState` 为远端 Cancel、deadline、Server Stop、连接故障和正常完成提供 first-wins 终态仲裁与独立错误分类。
- 增加连接认证隔离、幂等关闭、独立 admission、deadline timer、抛异常取消回调及 10,000 次取消/完成/Dispose 竞态测试。

### 变更

- 服务端三个 session 字典合并为单一 connection dictionary；业务请求热路径直接持有 connection state，不再逐请求查认证上下文或最后请求 ID。
- 心跳超时、同 ID 连接替换、读循环退出和 Server Stop 统一经过幂等连接关闭入口；单连接故障只取消该连接的服务调用。
- 框架任务继续被显式持有、等待和观察异常；异步用户调用改由 active-call counter 与统一 observer 收敛，不再为每个调用进入全局 Task HashSet 锁。

### 性能

- 同机 TCP c128 对同步、`Task.Yield()` 和 1 ms async 服务各执行五轮交替 A/B，全部零错误并通过 QPS/P99 回归门禁。
- 目标 `Task.Yield()` 路径 QPS +0.08%、P99 -1.54%，未达到可宣称性能收益阈值；本版本只确认全局任务集合锁从该路径消失，不宣称平均吞吐提升。
- StreamFlowController、Writer Pool、Interceptor pipeline、Throughput flush timer 与 Generated Stub Codec lookup 均因缺少触发阈值证据而保持不变。

### 修复

- 修复连接替换或心跳关闭只释放 Session、但不完成统一连接生命周期和连接级服务调用取消的问题。
- 修复携带 deadline 的调用可能把远端取消、服务停机或连接故障误归类为 `DeadlineExceeded` 的竞态。
- 修复已完成 framework task 在 Stop 获取快照前移出集合时，异常可能未被观察的问题。
- 修复 Windows Release Gate 的 TLS PFX 导出、NamedPipe 测试 flush 顺序和 PackageSmoke NuGet 源隔离问题。

完整 A/B、附加实验和保留结论见 `doc/performance-0.6.8.md`。

## [0.6.7] - 2026-07-17

### 新增

- 每条物理连接独立拥有 `ClientConnection`、pending table、request ID、monotonic deadline timer、取消源、active count 与 Ready/Draining/Closed 状态。
- `[NonCancellable]` 显式声明服务方法不支持协作取消；Source Generator 通过 `SHARPLINK004` 警告未声明 `CancellationToken` 的 RPC 方法。
- `sharplink.calls.abandoned` 指标与限频结构化日志，记录客户端已放弃但服务业务仍在执行的调用。

### 变更

- Response、error、用户取消、deadline、断连、GoAway、send failure、stream complete 与提前停止消费统一经过每连接 `PendingCallTable.TryComplete` 仲裁。
- 每连接只使用一个 monotonic timer 扫描有界 pending table；正常完成不再进入旧 timeout scheduler 的 Schedule/Cancel 锁。
- 默认 Unary timeout 仍为 30 秒并固定映射为 `SharpLinkException(DeadlineExceeded)`；`DisableRequestTimeout()` 提供真正无客户端默认 timeout 的显式入口。
- 客户端 deadline 与服务执行取消解耦：没有 `CancellationToken` 的服务调用会在客户端超时后标记 Abandoned、抑制迟到响应，并在业务任务真实结束后释放 admission 与 DI scope。
- Server/duplex stream 提前 Dispose 会发送 Cancel，并一次性释放 pending slot、dispatcher、producer、credit waiter 与 active count。

### 性能

- 同步且 `[NonCancellable]` 的服务调用不租用 cancellation state、不进入服务端 cancellation map；只有支持协作取消或真正异步未完成的调用才注册状态。
- 同机五轮 A/B 中位数相对 `v0.6.6`：c1 QPS +0.59%，c128 QPS +0.33%，c128 P99 -16.82%，`Rpc_Add` allocation 保持 672 B/op。
- 已撤销“所有服务调用无条件注册 cancellation state”的实验；其拆分测量导致 c128 相对客户端候选回退约 10%。

### 修复

- 迟到 response 不再需要永久 tombstone，也不会完成已复用的 request ID 或关闭健康连接。
- 修复提前退出 server/duplex stream 后客户端 pending、dispatcher 与 active count 泄漏。
- 修复只因请求携带 deadline 就把任意 `OperationCanceledException` 误报为 `DeadlineExceeded` 的错误分类。
- Cancel、deadline、response 与 disconnect 竞态只允许一个终态，operation 仍在调用方 `GetResult` 后才回池。

完整 A/B 环境、失败实验和最终结论见 `doc/performance-0.6.7.md`。

## [0.6.6] - 2026-07-17

### 新增

- LoadTest、StreamLoadTest 与热路径 Micro Benchmark 增加可重复 JSON 证据，记录 commit、机器、Runtime、GC、Transport、Profile、连接池、payload 与并发度。
- `IRpcBufferWriterPool.Rent(int maxWrittenBytes)` 与 session 协商后的出站帧硬上限。
- Server Interceptor 异步等待期间使用有界 ArrayPool owner 持有业务 arguments，避免越过 `PipeReader.AdvanceTo` 后读取复用内存。

### 性能

- SendPump 没有 admission waiter 时跳过容量通知锁；默认单连接池达到上限时跳过扩容状态锁。
- Generated Stub 的多段固定宽度参数改用有界 stack scratch，不再为每个参数分配临时数组。
- `SharpLinkCallContext` scope 改为值类型；基准从约 104 B/op 降至 72 B/op。
- Parser 只做帧结构验证，Metadata/Error/Handshake 等语义 payload 在消费位置解析一次；Metadata parser 基准降为 0 B/op。

### 变更

- 删除进程级 `BufferWriterPool`、`RuntimeConcurrency`、`RpcCodecRegistry`、`RpcCodec` 兼容入口；Codec、Pool 与状态容器配置只属于构建它们的 Client/Server Context。
- 删除旧 Client 调用排列组合和 CallOptions wrapper；生成代理只使用 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类 `IRpcChannel` invoker。
- 测试与示例迁移到实例级 Runtime Context、`SharpLinkBufferWriterPool` 和生成调用链。

### 修复

- Client/Server 所有业务帧、流帧与错误帧统一执行双方握手协商后的较小 frame limit；本地超限返回 `ResourceExhausted` 且不关闭健康连接。
- 修复启用异步 Server Interceptor 后请求参数可能引用已归还 Pipe buffer 的生命周期问题。

完整 A/B 环境、数据和结论见 `doc/performance-0.6.6.md`。

## [0.6.0] - 2026-07-17

### 新增

- TCP TLS 与双向证书认证，TLS 在 Protocol v2 handshake 前完成并使用独立超时。
- TLS 协议/cipher 结构化日志；默认保留平台证书链和 hostname 校验。
- `ISharpLinkClientAuthenticator`、`ISharpLinkServerAuthenticator`、二进制认证请求与 delegate adapter。
- 显式 `RequireAuthentication()` Build 校验；默认模式保持 Anonymous。
- Client/Server interceptor pipeline、实例级 `IRpcExceptionMapper` 与显式 `[Idempotent]` 方法元数据。
- `SharpLink.Client` / `SharpLink.Server` ActivitySource 与 `SharpLink` Meter；覆盖连接、调用、字节、队列、pending、stream 和失败指标。
- instance/type/factory 三类服务注册、Singleton/Scoped/Transient 生命周期与宿主 DI scope。
- Protocol v2 health-check capability、`CheckHealthAsync`、本地 readiness 和 Microsoft health checks。

### 变更

- 删除 string/bool authenticator Builder API；认证 payload 在每次重连时异步创建并受 handshake 上限约束。
- 认证上下文挂入每次服务调用；handshake 自动拒绝已过期 context，授权 helper 可在调用前再次校验 expiry/scope/tenant。
- 未注册 interceptor 时生成调用继续直达泛型 invoker；注册后客户端可修改调用选项或短路，服务端可鉴权、限流与审计。
- 未映射业务异常默认只公开 `Internal` 和通用消息；详细错误需要显式启用，stream 错误使用同一 mapper。
- Activity/Meter 无 listener 时不构建 tag collection、Activity 或调用 observer；结构化日志继续使用 `LoggerMessage` 预编译路径。
- 默认服务生命周期保持 Singleton 热路径；Scoped/Transient scope 覆盖完整调用或 stream，并在异常、取消、断线和停机时释放。
- Server 停机顺序固定为 readiness=false、停止 accept、GoAway、等待在途调用、超时取消、flush 与资源释放。

### 修复

- Source Generator 辅助 request/stream 类型加入契约名前缀，多个接口出现相同 method hash 时不再发生编译期类型冲突。
- NativeAOT 服务注册显式保留 DI 所需 public constructor，避免服务构造器被 trimming 后在首个调用失败。

## [0.5.0] - 2026-07-17

### 新增

- 可异步 admission 的 `PendingRequestTable`，完整 64 位 request ID 匹配与统一完成仲裁。
- `PooledByteBufferWriter`、明确的 frame owner 生命周期与 Context 有界 writer pool。
- Source Generator 原生 DTO/闭合集合 Codec、稳定字段 ID、未知字段跳过、required 校验与 64 层类型图边界。
- `[RpcSerializable]`、`[RpcMember]`、`[RpcIgnore]`、`[RpcRequired]`、`[RpcExternalCodec]`。
- append-only generated Codec manifest；Runtime Context 在 Build 时冻结 manifest 快照。
- Protocol v2 stream/connection 双层字节窗口、`WindowUpdate` 与单个大帧临时借用。
- 每 Endpoint 有界客户端连接池、压力扩容与 power-of-two choices 连接选择。

### 变更

- 生成代理收敛到 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类内部 Invoker；静态 descriptor/Codec 避免捕获 delegate。
- `IRpcCodec<T>.Serialize` 统一写入 `IBufferWriter<byte>`；协议回填使用 `IRpcByteBufferWriter`。
- AOT Smoke 不再依赖 MemoryPack，覆盖 class、record、struct、嵌套数组和生成 manifest。
- client/server stream sender 在额度不足时异步等待；消费、取消、超时和断连统一释放额度等待者。
- stream dispatcher 按已编码字节记账，迟到的已取消 stream data 只丢弃并计数，不重建 dispatcher。
- stream 调用固定绑定创建时的连接；`GoAway` 连接停止接收新请求并在变为空闲后退出，其他健康连接继续服务。
- LoadTest 与 StreamLoadTest 支持 `--min-connections` / `--max-connections`。

### 修复

- 长请求跨越 pending table ID 周期、乱序响应、cancel/timeout/disconnect 竞态不会误命中新请求或重复归还 operation。
- client/server stream 的额度等待、取消、迟到数据与 terminal ACK 统一收敛，慢消费者不再驱动无界缓冲。
- 多连接下单条 session 断开只失败绑定到该连接的请求，不影响其他 ready session。

## [0.4.0] - 2026-07-17

本版本合并原实施路线图的 0.4 安全基线与 0.5 Runtime/Protocol v2 重构。

### 新增

- 实例级 `SharpLinkRuntimeContext`、性能预设与不可变配置快照。
- TCP/UDS、NamedPipe、AnonymousPipe 的 Client Factory、Server Listener 和独立 Connection 模型。
- 15 字节固定头的 Protocol v2、能力协商、Ping/Pong、Cancel、GoAway、deadline 与 metadata。
- `SharpLinkCallOptions`、`SharpLinkMetadata`、扩展错误码及服务端调用上下文。
- 字节有界单写者 SendPump、强制 flush marker 与资源耗尽保护。
- Client/Server 原子生命周期、自动重连、断连单次收敛和优雅排空。
- NuGet-only PackageSmoke；`SharpLink.Sdk` 自动携带 Source Generator。

### 变更

- `ConnectAsync` 成功返回、失败抛结构化异常，不再返回 `bool`。
- Client/Server 主生命周期统一为 `StopAsync` / `DisposeAsync`。
- 默认 Unary timeout 为 30 秒；stream 默认不设置 timeout。
- 自定义 Codec、Buffer Pool 与并发配置归属实例 Context，不再互相覆盖全局状态。
- 默认认证明确为 Anonymous；删除默认 Password 和旧字符串错误 wire 格式。
- 所有正式包目标框架更新为 .NET 10。

### 修复

- Parser/Codec 在切片、分配前验证所有网络长度，非法数据稳定映射为协议/数据错误。
- pending request、stream、server concurrency、anonymous pipe offer 与 writer retention 增加硬边界。
- 修复 timeout 调度器取消墓碑导致的吞吐退化，取消节点现在立即从有界堆移除。
- 修复 send/dispose、heartbeat/stop、read/write fault 和 GoAway/new request 等生命周期竞态。
- CI 与 PackageSmoke 使用隔离 NuGet 缓存，避免同版本旧包污染验证。

### 兼容性说明

- 本版本包含公共 API 与线协议破坏性升级，不提供 Protocol v1 兼容层。
- c32 Unary QPS 为前一基线的 96.75%，保留原基线并在 0.5.1 优先处理。

## [0.2.0] - 2026-02-18

### 新增

- 引入 `RpcCodecRegistry`，支持统一注册与复用类型 Codec。
- 引入可选 MemoryPack 回退序列化器。
- 增强 blittable struct 容器编解码。

### 变更与修复

- 区分正常关闭与异常断连日志。
- 修复 Hosting 正常停机噪音与 pending request 失败传播问题。
- 本版本包含破坏性变更。

## [0.1.0] - 2026-02-12

### 新增

- 初始化 Abstractions、Runtime、Client、Server、SDK、Hosting 与 Generator。
- 支持 Unary、OneWay、客户端流、服务端流和双向流。
- 增加示例、Unit/Integration/AOT/Load/Benchmark 测试和 CI 工作流。

### 变更与修复

- 优化流式调用固定分配。
- 修复初始生命周期、取消和断连边界问题。
