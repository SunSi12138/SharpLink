# 迁移到 2.0

SharpLink 2.0 将进程内 Generated ABI 从已发布的 1.1.1/API 3 原子升级一次到 API 4，同时把 Protocol v2 minor 升到 4，并以剩余 `TimeBudget` 取代跨机器绝对 deadline。2.0 的版本计算只以已发布的 1.1.1 为基线；开发期间出现过的中间 ABI 编号不构成兼容边界，也不会继续累加版本号。由于 `IRpcChannel` 调用 ABI 在 #287 中发生破坏性变化，所有 1.1.1/API 3 生成程序集都必须使用 2.0 SDK 重新生成。升级前让同一进程中的全部 SharpLink 包使用 2.0，并在独立环境完成 Client/Server 互操作、AOT、负载和故障测试。

## Generated ABI（API 4）与重新生成

2.0 Generator 只生成 API 4，2.0 Runtime 只接受 `Generated API = 4`、`Protocol = 2`，并要求 locator 携带当前 `SharpLinkGeneratedManifestVersions.AbiIdentity`。已发布的 1.1.1 生成程序集是 API 3，升级到 2.0 时会在 materialize Manifest 或发布任何运行时资源前明确拒绝 API 3，并要求重新生成。开发分支曾使用过的中间 ABI 编号不属于受支持输入，也不作为发布兼容性资产；如果旧开发 artifact 曾复用整数 API 4，但它没有当前 ABI identity，同样会在 materialize 前拒绝，避免同一整数误识别两种不兼容 binary shape。版本与 identity 校验只发生在 assembly load / registration / startup 边界，不进入任何调用热路径。

升级必须同时完成：

1. 把 SDK、Abstractions、Runtime、Client、Server、Hosting 和 serializer adapter 统一为 2.0。
2. 删除所有契约、服务和插件项目的旧 `bin`、`obj` 与缓存生成源码。
3. 重新构建全部 contract assemblies 和 service assemblies。
4. 重新构建并重新部署全部 plugin assemblies；不要把 1.1.x/API 3 与 2.0/API 4 生成程序集装入同一进程。

旧 artifact 在注册/启动期收到稳定的 version mismatch，例如：

```text
IncompatibleManifest: Manifest compatibility mismatch: API 3/4, Protocol 2/2,
Generator '2.0.0'. Action: delete stale generated outputs, then regenerate and
rebuild this assembly with the SharpLink SDK version that matches the current
Runtime.
```

`Assembly`、`LoadContext`（dynamic）、`Expected/Actual Generated ABI`、`Expected/Actual Protocol`、
`Expected/Actual ABI identity` 与 `GeneratorVersion` 字段在所有入口一致。修复方式始终是重新生成：删除旧输出，用当前
2.0 SDK 重新构建，而不是回退包版本或寻找兼容开关。

自动生成代码的用户不需要手写 Bridge。手写生成基础设施的高级用户需要同步采用 API 4：程序集 locator 使用包含 Manifest 类型、`apiVersion: 4`、`protocolVersion: 2`、Generator version 和 `SharpLinkGeneratedManifestVersions.AbiIdentity` 的自描述构造函数；`IRpcStub` 接收 `IRpcGeneratedServerBridge`，响应写入 `IBufferWriter<byte>`；`SharpLinkGeneratedContractDescriptor.StubFactory` 接收 `IRpcCodecProvider`；生成的 DTO Codec 实现 `IRpcCodec<T>` 与 `IRpcSizedCodec<T>`；自定义 Codec 绑定使用 `RpcCodecAttribute`/`RpcCodecImplementationAttribute` 并带 schema identity。

Generated ABI 与网络 minor 是独立版本轴。SharpLink 2.0 以 Protocol v2 minor 4 作为 TimeBudget wire baseline，不再提供 absolute-deadline fallback。该重构只进入 2.0，因此发布门禁只验证 2.0 Client/Server 互操作；pre-2.0 跨版本互操作不属于 2.0 的兼容性承诺。低于 minor 4 的握手会被拒绝，避免旧 absolute-deadline 字节被误解释为 TimeBudget。

## `SharpLinkCallOptions` 迁移

2.0 不保留 `SharpLinkCallOptions` 或兼容 options bag。旧调用点按能力迁移：

- `SharpLinkCallOptions.Metadata` → `client.GetWithMetadata<TContract>(metadata)`，用于调用方为单次/一组显式 invocation 选择 metadata；横切 metadata policy 仍可使用 Client interceptor。
- `SharpLinkCallOptions.Timeout` → 契约方法 `[Timeout]`，或普通 Unary 调用的 Client `UseRequestTimeout` / `DisableRequestTimeout` fallback policy。OneWay 和三类 Streaming 不自动继承 Client-wide fallback；它们需要方法 `[Timeout]` 或继承父调用 lifetime 才会携带对应 `TimeBudget`。timeout 不再是业务方法伪参数。
- `SharpLinkCallOptions.Deadline` → 删除。2.0 不再公开或重建跨机器 absolute UTC deadline，也没有新的 per-call absolute-deadline 替代项；使用相对 timeout policy，并由 runtime 解析本地 monotonic `RpcDeadline`、在 wire 上只传播剩余 `TimeBudget`。
- 调用方取消仍使用业务方法原有的 `CancellationToken` 参数；它从来不是 `SharpLinkCallOptions` 字段。删除旧 options 伪参数时保留正常的 cancellation-token 参数；没有 token 的 RPC 必须明确审计 `[NonCancellable]`。
- `WaitForReady` 不再有每调用兼容开关；连接/readiness 使用 Client readiness API 和拓扑策略表达。

因此生成的业务签名和 `IRpcChannel` ABI 都不再接收 `SharpLinkCallOptions`。迁移时应删除旧 options 参数并重新生成全部 API 4 proxy/stub，而不是创建新的通用调用控制对象。

## Client request-timeout policy

2.0 不再为 Client builder 隐式选择 30 秒 request timeout。升级后，每个 `SharpClientBuilder` 和 `SharpLinkMultiClusterClientBuilder` 都必须在 `Build()` 前显式选择 request-timeout policy；旧代码如果没有选择，会在 Build 或 Generic Host 启动时抛出配置错误，而不是继续静默使用 30 秒默认值。

按应用意图选择以下一种：

```csharp
// 推荐策略：普通 Unary 使用 30 秒 Client fallback。
builder.UseRequestTimeout();

// 自定义普通 Unary fallback。
builder.UseRequestTimeout(TimeSpan.FromSeconds(10));

// 明确不提供 Client-wide fallback。
builder.DisableRequestTimeout();
```

方法 `[Timeout]` 仍优先于 Client fallback；继承的父调用 `TimeBudget` 仍是独立 hard cap。OneWay 和三类 Streaming 不会仅因为选择了 Client-wide fallback 就自动获得该 fallback，它们仍依赖方法 `[Timeout]` 或继承的父调用 lifetime。

MultiCluster coordinator 同样必须显式选择 policy。静态 child slot 以及运行时 Add/Replace child 在没有自行选择 timeout policy 时继承 coordinator 在 Build 后冻结的 policy；child 显式调用 `UseRequestTimeout(...)` 或 `DisableRequestTimeout()` 时覆盖 coordinator policy。迁移时应在 coordinator builder 上做一次明确选择，只在确有不同 lifetime 需求的 child 上覆盖。

## Multi-cluster runtime mutation result

运行时 slot mutation 的 public return contract 已收敛为 operation-specific structured result：

```text
AddClusterAsync     -> ValueTask<SharpLinkClusterAddResult>
ReplaceClusterAsync -> ValueTask<SharpLinkClusterReplacementResult>
RemoveClusterAsync  -> ValueTask<SharpLinkClusterRemovalResult>
```

旧代码若只是 `await client.AddClusterAsync(...);` / `await client.ReplaceClusterAsync(...);` 并忽略返回值，可以继续按语句形式调用；需要处理正常 control-plane rejection 的代码应改为检查 `Succeeded` 与 `FailureCode`，不要再依赖 `InvalidOperationException` / `ArgumentException` message。稳定分支包括 `AlreadyExists`、`NotFound`、`Busy`、`LifecycleClosed`、`RouteConflict`、`CapacityExceeded` 和 replacement 的 `CandidateUnavailable`。诊断 `Message` 不是机器分支 contract。

Add 的 `Succeeded = true` 只表示本地 slot/routes publication 已提交，不代表远端 Ready；需要立即发起 RPC 时继续显式 `WaitForReadyAsync(cluster)`。Replace 保持 ready-before-swap：`Published = false` 的 expected failure 保留旧 generation；swap 已提交时 `Succeeded = true` / `Published = true`，旧资源的 bounded retirement 另由 `ReferencesReleased` / `ForcedStop` 报告。Remove 保留相同 cleanup 字段，并把 valid-but-missing / Busy / lifecycle closed 收敛为 structured rejection。

明显非法参数、configure/builder/manifest 错误、caller cancellation、内部 invariant、unexpected cleanup/runtime failure 仍然抛异常。不要用 catch-all 把这些异常转成普通 result；也不要引入旧 throwing overload 或 generic `Result<T>` compatibility shim。

## Runtime engine API boundary

`IRpcSession`、`IStreamManager`、raw stream dispatcher interfaces、`PooledAsyncStreamDispatcher<T>`、
`RpcSession`、`StreamManager` 和 `RpcSessionExtensions` 不再是公开扩展面。不要构造或控制 Session、读取其 PipeReader、注册 raw
dispatcher、设置 peer activity，或直接发送 protocol control frame。自定义传输应实现
`ITransportConnection` 并经 `IClientTransportFactory` 或 `IServerTransportListener` 配置到 Builder；
generated server code 继续使用 API 4 的 `IRpcGeneratedServerBridge`。完整的 public API diff、保留 SPI
和 ownership 说明见 [`runtime-phase-16-engine-api.md`](runtime-phase-16-engine-api.md)。

## Builder 构建计划与单次使用

`SharpClientBuilder` 和 `SharpLinkServerBuilder` 现在在 `Build()` 中先冻结完整
BuildPlan，再 materialize framework-owned 资源并提交所有权。Builder 本身是一次性的：无论
Build 成功或失败，后续的 `Build()` 或 `Use*`/`Add*` 调用都会抛出
`InvalidOperationException("This SharpLink builder has already been consumed.")`。需要另一个
Client 或 Server 时，创建新 Builder，不要修改或复用已经 Build 过的实例。

Client topology 也必须在第一次配置时确定。`UseTransport`、`UseEndpoint`/`UseEndpoints` 和
`UseEndpointResolver` 不能混用，也不能重复配置同一种 topology；第二次调用会立即失败。静态
endpoint 与 manifest source 只在 Compile 时取一次快照，随后修改原 collection、attribute 字典或
options 不会影响已经编译的 plan。多集群会用同一个 child plan 同时执行预算检查和 materialize，
不再存在 endpoint 预检缓存。

详见 [`runtime-phase-11-build-plan.md`](runtime-phase-11-build-plan.md)。

## Server handshake admission 默认值

Server 的 connection admission 仍然使用现有的 `MaxConcurrentConnections` / `MaxConcurrentHandshakes` 两层边界，但 `MaxConcurrentHandshakes` 的默认行为发生了安全收紧：默认 live connection 上限仍为 1024；默认独立 handshake 上限现在为 64。handshake slot 覆盖 TLS、Protocol v2 与应用认证，并在连接进入 Ready 时立即释放。

如果应用只把 `MaxConcurrentConnections` 配到 64 以下而没有显式设置 handshake 上限，默认 handshake 上限会自动取更低的 connection bound。显式正值仍不能高于 connection bound。

旧版 `MaxConcurrentHandshakes = 0` 的含义保留为显式 opt-out；需要恢复“没有独立 handshake 上限、只由 connection bound 限制”的旧行为时可写：

```csharp
serverBuilder.UseConnectionAdmission(options =>
{
    options.MaxConcurrentConnections = 1024;
    options.MaxConcurrentHandshakes = 0;
});
```

默认值变化不会修改 Protocol v2、TLS wire bytes、认证协议或成功连接生命周期。因为 over-limit handshake 仍采用现有的立即关闭语义，滚动发布或大规模同时重连超过默认安全边界时应错峰/重试；确有容量数据支持时，也可以显式提高正值，但不得高于 `MaxConcurrentConnections`。启动日志会输出最终生效的 `max_connections` / `max_handshakes`。

## Client readiness API

`ISharpLinkClient` 新增 `GetReadinessSnapshot()` 和 `WaitForReadinessAsync(...)`。内置 Client 提供固定、静态与 resolver 拓扑的精确快照；`ConnectAsync` 仍只承担 connectivity，不会等待多 endpoint 收敛。第三方 `ISharpLinkClient` 实现必须随本次 major 升级重新编译，并实现新继承的 capability/registry 接口。readiness 的接口默认实现会明确抛出 `NotSupportedException`，不会伪造单 endpoint 数据；默认实现不是跨 major 二进制兼容承诺。包装或代理实现如果希望支持 readiness，应转发这两个成员并保留调用方独立取消与终止状态语义。

## 包依赖变化

`SharpLink.Sdk` 2.0 只依赖 `SharpLink.Abstractions` 并携带 Analyzer/Source Generator，不再传递引入 `SharpLink.Runtime`。纯契约项目继续只引用 SDK；Client、Server 或 Hosting 应用引用相应应用包，由应用包引入 Runtime。直接使用 Runtime API 的库必须显式引用 `SharpLink.Runtime`。

官方 SharpPack adapter 的公开类型从 `SharpLink.Runtime` 命名空间移动到 `SharpLink.Serializer.SharpPack`。例如：

```csharp
[assembly: RpcCodecAdapter(
    typeof(ThirdPartyGraph),
    typeof(SharpLink.Serializer.SharpPack.SharpPackRpcCodecAdapter))]
```

## 1.1.1 公共 API 迁移对照

以实际 NuGet 1.1.1 包为基线的完整 public/protected 差异保存在
[`eng/public-api/1.1.1-to-2.0.0.diff`](../eng/public-api/1.1.1-to-2.0.0.diff)。
SDK 中的 type forwards 也参与审计。下表补充上述专题，覆盖签名删除、替换与默认值变化；
新增 capability 的精确成员以 [`2.0.0 API 基线`](../eng/public-api/2.0.0) 为准。

| 1.1.1 入口或行为 | 2.0 迁移 |
| --- | --- |
| `CompileSymbols.Debug` | 使用 C# 自带的 `[Conditional("DEBUG")]`；构建符号常量不再作为 RPC 公共 API。 |
| `IRpcChannel.Invoke*` options、`SendClientStreamAsync` | 重新生成 API 4；metadata 进入窄参数，client stream 通过 `IRpcClientStreamSink`。业务代码只调用契约 proxy。 |
| `IRpcStub.Invoke*` 的 Session/专用 writer 参数 | 改为 `IRpcGeneratedServerBridge` 和 `IBufferWriter<byte>`，由 Generator 生成。 |
| `IRpcGeneratedCodecFactory.SchemaId/WireFormatId`、adapter `WireFormatId`、registration attribute 的第三参数 | 采用 `CodecHash` 与 `[RpcCodecSemanticIdentity]`；改变 wire 含义时改变 semantic identity。详见 [契约与 Codec](contracts-and-codecs.md)。 |
| Client/Server `UseCodec<T>`、Client `UseSerializer` | Generated RPC 在契约中用 `[RpcCodec]`/`[RpcCodecImplementation]` 或 adapter attribute 绑定；不能用运行时 resolver 覆盖冻结的 generated Codec。Standalone Context 的 `AddCodec`/resolver 仍供独立 codec 使用。 |
| `SharpLinkGeneratedAssemblyManifestAttribute(Type)` | 重新生成携带 API、Protocol、Generator version 和 ABI identity 的 locator。 |
| `SharpLinkGeneratedContractDescriptor` 旧构造函数和 factories | 重新生成 provider-aware proxy/stub factory；无 legacy 构造函数或 adapter。 |
| `ISharpLinkClient` / `ISharpLinkServer` 直接声明的 assembly registry 方法 | 统一继承 `ISharpLinkAssemblyRegistry`；普通调用语法不变，显式接口实现需要重编译并调整所属接口。 |
| Server `RunAsync` | `await StartAsync()` 后由应用等待 `WaitForShutdownAsync()`；关闭时等待 `StopAsync()`，最终 `DisposeAsync()`。Generic Host 使用 Hosting 集成。 |
| `SharpLinkCallContextSnapshot.Deadline`、Server invocation `Deadline`、Client invocation `Options` | 删除 absolute UTC deadline；使用 cancellation、metadata 与相对 timeout policy，详见本页调用选项章节。 |
| `ProtocolV2FrameFlags.HasDeadline` | `HasTimeBudget`；禁止把旧 deadline 字节当作新字段。双方整体升级至 minor 4。 |
| `ProtocolV2Error` / `ProtocolV2HandshakeRequest` 构造签名 | 使用新结构的 error detail / handshake 字段，勿手工拼旧 wire frame；参见 [协议](protocol-v2.md) 与 [错误详情](error-details.md)。 |
| `SharpLinkHealthCheckResult.Status` 可写非空状态 | 先检查 `Outcome`；仅 `Success` 含远端 `Status`。NotReady/Unavailable/Unsupported 不伪造远端状态，禁止写 `Status` init 属性。 |
| MultiCluster Add/Replace 无返回值的 extension、Remove 旧结果 | 检查 operation-specific result；忽略返回值的 await 语句仍可编译，方法组/委托须更新返回类型。 |
| `SharpLinkCircuitBreakerOptions` / `SharpLinkRetryOptions` | 保留具体配置类，并实现对应 Abstractions 接口；runtime update 使用 capability/result API，不操作内部 engine。 |
| `NamedPipes()` / `UseNamedPipe(name)`、factory/listener 构造函数默认值 | 重新编译可选参数调用；默认启用 `CurrentUserOnly`。需要自定义策略时传 `NamedPipeTransportOptions`，参见 [传输](transports.md)。 |
| Server `UseTcp(port, ip = "0.0.0.0", ...)` | 选择 port-only、显式 `IPAddress` 或 string overload；需要固定监听范围时显式传地址。TLS overload 同样处理。 |
| `ISharpLinkCompressionProvider.Compress` / `Decompress` 返回 `SharpLinkCompressionResult` | 实现 `TryCompress -> bool` 和 `Decompress -> void`，完整消费输入并遵守有界 writer；false 表示候选压缩不适用。 |
| `SharpLinkCompressionOptions.MinimumPayloadBytes/MinimumSavingsBytes/MinimumSavingsRatio` | 删除手动阈值，使用 Runtime 自适应压缩策略；注册 provider 即可。 |
| 内置 Brotli factory、`SharpLinkCompressionResult`、旧压缩 profile | 引用独立 `SharpLink.Compression.Zstd` 或实现 provider。不得复用不兼容的旧 profile identity。 |
| SharpPack adapter 的 Runtime namespace、`WireFormatIdentity` | 移至 `SharpLink.Serializer.SharpPack`，以 semantic identity / CodecHash 管理兼容性。 |
| public Session/StreamManager/raw dispatcher 及全部构造与 mutator | 删除直接 engine 调用；传输扩展实现 factory/listener/connection，流操作使用契约 `IAsyncEnumerable<T>`。 |

## Runtime 配置更新与 Session 刷新

需要处理配置拒绝的调用方使用 operation-specific `TryUpdate*` result 与稳定 failure code，
不要依赖异常消息；现有 throwing API 仍用于错误属于编程错误的入口，详见
[control-plane-results](control-plane-results.md)。连接的 desired session 配置与当前连接实际
协商结果分别报告；配置发布成功不等于已有 Session 已应用。需要主动换代时使用
[session-refresh](session-refresh.md) 的有界刷新流程，检查 Ready/retirement 结果。

## RuntimeContext、Catalog 与释放所有权

普通应用通过 Client/Server Builder 配置 Runtime、时间和协议策略。独立 codec 工具仍可用
`SharpLinkRuntimeContextBuilder.Build()` 创建实例，并由创建者 `Dispose()`；没有 process-default
Context，也不能把 Context 在构造后绑定到 Session。Context 拥有其 buffer pool、generated codec
registration 和 adapter scopes；传入的 `TimeProvider` 仍属调用方，框架不释放它。
Client/Server 持有的 Context 随 owner 关闭释放，不由业务代码提前释放。

`SharpLinkGeneratedAssemblyCatalog` 是 generated bootstrap 基础设施（隐藏于 IntelliSense），
不是应用动态注册入口。动态模块用 client/server 的 `ISharpLinkAssemblyRegistry`；等待返回的
references-released 结果之后，应用才释放自身 Assembly/Type/proxy 引用并请求卸载 ALC。
Catalog 保留弱引用不代表应用引用已经释放。详见 [边界 ADR](adr/0001-2.0-public-api-and-packages.md)。

## 从 0.7.x

- MemoryPack 扩展和 `RpcExternalCodec` 已删除。复杂图使用通用 Codec Adapter SPI；官方实现为 `SharpLink.Serializer.SharpPack`。
- 多 endpoint、Resolver、Retry、Circuit Breaker 和 multi-cluster 使用当前 Builder API；不要依赖旧实验接口或进程级默认 serializer state。
- 动态模块必须提供兼容 generated Manifest，并遵守注册、替换、排空、注销和 collectible ALC 所有权。

## 从早期 0.8.x

- 使用具体 `SharpLinkErrorCode`；`Unknown` 不能作为 wire error 或 `SharpLinkException` code。
- required/non-nullable response 与 stream item 不能由 custom Codec 返回 null；违反现在是 `DataLoss`。
- Codec 必须完整消费 payload，拒绝非规范 null、整数、UTF-8 和尾随字节。
- Client/Server interceptor 的 `next` 只能调用一次并必须被等待；响应调用不能静默不调用 `next`。
- `[NonCancellable]` 明确表示服务业务不接收 token；调用方取消不保证业务停止。
- Shutdown、resolver、hosted service、transport 与动态模块 cleanup 的异常会被保留和观察，不再静默吞掉 sibling failure。

## 文档与包

- 发布源码所有公开 API 由 CS1591 gate 强制 XML 注释。
- 每个运行时 NuGet 包包含与主程序集同名的 XML IntelliSense 文件。
- 旧 `audit-*`、`migration-0.x.*`、`performance-0.x.*` 是开发过程证据，不是 2.0 用户契约，已由当前主题文档、CHANGELOG、测试和最终性能基线替代。

## 升级清单

1. 统一 SDK、Generator、Abstractions、Runtime、Client、Server、Hosting 和 serializer adapter 为 2.0；同一进程不混装 1.1.x。
2. 清理所有契约、服务和插件项目的旧 `bin/obj`，重新生成 API 4，并把 Generator diagnostics 当错误处理。
3. 为每个 Client 和 MultiCluster builder 显式选择 `UseRequestTimeout()`、`UseRequestTimeout(timeout)` 或 `DisableRequestTimeout()`；不要依赖旧的隐式 30 秒 fallback。
4. 为所有没有 token 的 RPC 显式确认 `[NonCancellable]` 是否合理。
5. 验证 DTO field id、required/nullability 和 custom Codec wire identity。
6. 验证 TLS、authentication、authorization、metadata 与错误消息不泄露敏感数据。
7. 验证 Unary、OneWay、三类 Streaming、deadline、取消、断连和 Server Stop。
8. 若使用 topology/resilience，验证 generation churn、last-good、retry deadline 和 breaker。
9. 若使用动态模块，验证替换期间旧调用排空与 ALC 最终回收。
10. 对实际发布入口执行包含五种调用形态的 NativeAOT smoke（若适用）、PackageSmoke 和固定负载基线。

Protocol v2 的当前 wire 定义见 [protocol-v2.md](protocol-v2.md)。Generated ABI（API 4）与 Protocol v2 minor 是独立版本轴；2.0 的 wire lifetime baseline 是 minor-4 `TimeBudget`。pre-2.0 跨版本互操作不在本版本发布门禁范围内。
